using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Cross-references the album catalogue against the Canon piece tree: for every
/// <see cref="CanonPiece"/>, <see cref="CanonPieceVersion"/> and composer name,
/// records which album tracks reference it (directly or via a descendant
/// subpiece).
/// </summary>
/// <remarks>
/// <para>Rebuilt wholesale (cheap — a few thousand pieces, a few thousand refs)
/// whenever pieces or albums change. The singleton instance is exposed via
/// <see cref="Current"/> so WPF value converters — which can't accept DI
/// dependencies — can still read counts.</para>
/// <para>Resolution is tolerant: unresolved refs (bad composer, missing subpiece
/// path, etc.) are silently dropped rather than throwing, since
/// <c>AlbumConsistencyChecker</c> already surfaces those to the user.</para>
/// </remarks>
public class PieceReferenceIndex
{
    /// <summary>
    /// Singleton accessor for WPF value converters. Set by DI on construction;
    /// null before the first rebuild.
    /// </summary>
    public static PieceReferenceIndex? Current { get; private set; }

    /// <summary>
    /// Standard ctor used by the DI-registered singleton. Sets
    /// <see cref="Current"/> on construction so value converters resolved
    /// before any data has loaded still have a non-null index to bind
    /// against (they'll just see empty hit counts).
    /// </summary>
    public PieceReferenceIndex() : this(registerAsCurrent: true) { }

    /// <summary>
    /// Internal ctor that lets a throwaway resolver opt out of becoming
    /// <see cref="Current"/>. Used by save-path code (e.g.
    /// <c>SaveAlbumsCoreAsync</c> / <c>SaveLooseTracksCoreAsync</c>) and by
    /// <c>ItunesImporter</c>, which build a short-lived index purely for
    /// <see cref="BuildResolver"/> + <see cref="TryResolve"/> and never touch
    /// the hit dictionaries. Pre-fix those sites silently stole
    /// <see cref="Current"/> for the duration of the save: every
    /// <c>HitCountBadgeConverter</c> read between the throwaway's
    /// construction and the next real <see cref="Rebuild"/> /
    /// <see cref="RebuildContainers"/> call returned 0, which is the
    /// "badges flicker to zero mid-save" symptom Rework H7 described.
    /// </summary>
    public PieceReferenceIndex(bool registerAsCurrent)
    {
        if (registerAsCurrent) Current = this;
    }

    // Hits at or below a given piece (original + all versions + all subpieces recursively).
    private Dictionary<CanonPiece, List<PieceAlbumHit>> _hitsForPiece = new();

    // Hits under a piece's "original" (non-versioned) branch only — the piece itself
    // when ref has no VersionDescription, plus any subpieces reached via that branch.
    private Dictionary<CanonPiece, List<PieceAlbumHit>> _hitsForOriginal = new();

    // Hits at or below a given version (the version itself plus its subpieces).
    private Dictionary<CanonPieceVersion, List<PieceAlbumHit>> _hitsForVersion = new();

    // All hits whose primary composer equals the given name (case-insensitive).
    // Includes both primary-composer pieces and contributed-role pieces for that name.
    private Dictionary<string, List<PieceAlbumHit>> _hitsForComposer =
        new(StringComparer.OrdinalIgnoreCase);

    // Version is identified by (parent piece, description) in refs; this maps
    // (piece, description) → version instance for resolution.
    private readonly Dictionary<(CanonPiece, string), CanonPieceVersion> _versionLookup = new();

    // Composer → (normalized title → IndexEntry). Populated on Rebuild and reused
    // by the public TryResolve API so external callers (e.g. CanonDbSeeder) share
    // the same resolution semantics as the badge-hit pipeline.
    private Dictionary<string, Dictionary<string, IndexEntry>> _byComposerTitle =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// H41: pieces that lost a same-composer + same-title-key collision during
    /// the last index build. The kept piece won via insertion order
    /// (approved-first per <see cref="RebuildInternal"/>); the dropped piece
    /// is unreachable via <see cref="TryResolve"/> for that key — any album
    /// ref pointing at it will silently fail and the dropped piece's badge
    /// will stay at zero. Surface this in diagnostic UI / tool output so the
    /// user can fix the data (typically by disambiguating one of the titles).
    /// Reset on every <see cref="Rebuild"/> / <see cref="RebuildContainers"/> /
    /// <see cref="BuildResolver"/>.
    /// </summary>
    public IReadOnlyList<TitleCollision> Collisions { get; private set; } =
        Array.Empty<TitleCollision>();

    // The piece list used on the last Rebuild. Cached so album-only rebuilds
    // (RebuildAlbums) can reuse the same CanonPiece instances — critical
    // because the CanonView tree holds reference-identity keys into the hit
    // dictionaries. Re-loading pieces from JSON would produce new instances
    // whose lookups miss, which manifested as badges vanishing after the
    // Albums screen refreshed the index.
    private IReadOnlyList<CanonPiece> _cachedPieces = Array.Empty<CanonPiece>();

    /// <summary>
    /// Rebuilds all indexes from the current piece, album, and loose-track
    /// collections. Thread-safe to call from a background load path so long as
    /// the caller doesn't read the index concurrently.
    /// </summary>
    public void Rebuild(
        IEnumerable<CanonPiece>  pieces,
        IEnumerable<CanonAlbum>  albums,
        IEnumerable<AlbumTrack>? looseTracks = null)
    {
        // Re-claim Current. The constructor sets it, so any code that built a
        // throwaway resolver (e.g. SaveAlbumsAsync, ItunesImporter pre-fix) will
        // have stolen the static accessor and left it pointing at an index with
        // no hit data. Whenever we fully Rebuild we know we have the canonical
        // state, so put Current back on this instance — HitBadgeConverter and
        // every other static-accessor consumer immediately see the right index.
        Current = this;
        _cachedPieces = pieces as IReadOnlyList<CanonPiece> ?? pieces.ToList();
        RebuildInternal(_cachedPieces, albums, looseTracks ?? Array.Empty<AlbumTrack>());
    }

    /// <summary>
    /// Rebuilds the index using the piece list from the last <see cref="Rebuild"/>
    /// call but with fresh album / loose-track collections. Use this when only
    /// container data has changed (e.g. after a save) so badge-dictionary keys
    /// stay reference-equal to the <see cref="CanonPiece"/> instances held by
    /// the Canon tree view — otherwise the tree's lookups would start returning 0.
    /// </summary>
    public void RebuildContainers(
        IEnumerable<CanonAlbum>  albums,
        IEnumerable<AlbumTrack>? looseTracks = null)
    {
        Current = this;   // same Current-reclaim reasoning as Rebuild
        RebuildInternal(_cachedPieces, albums, looseTracks ?? Array.Empty<AlbumTrack>());
    }

    /// <summary>
    /// Legacy overload — kept so callers that only touch albums (the album
    /// editor save path) don't need to know about loose tracks. Loose-track
    /// hits from the prior Rebuild are dropped because they're not re-walked
    /// here; callers that want loose tracks preserved should use
    /// <see cref="RebuildContainers"/>.
    /// </summary>
    public void RebuildAlbums(IEnumerable<CanonAlbum> albums) =>
        RebuildContainers(albums, Array.Empty<AlbumTrack>());

    private void RebuildInternal(
        IReadOnlyList<CanonPiece> pieces,
        IEnumerable<CanonAlbum>   albums,
        IEnumerable<AlbumTrack>   looseTracks)
    {
        var hitsForPiece    = new Dictionary<CanonPiece, List<PieceAlbumHit>>();
        var hitsForOriginal = new Dictionary<CanonPiece, List<PieceAlbumHit>>();
        var hitsForVersion  = new Dictionary<CanonPieceVersion, List<PieceAlbumHit>>();
        var hitsForComposer = new Dictionary<string, List<PieceAlbumHit>>(StringComparer.OrdinalIgnoreCase);

        // composer+title -> (piece, its ancestor "set" chain).
        // Pieces without a JSON "title" (e.g. "Piano Sonata #21…") store their
        // TrackPieceRef.PieceTitle as the full DisplayTitle, so we register every
        // piece under each of the candidate titles callers might have written out.
        // Subpieces of a "set" (e.g. Beethoven's Three Piano Sonatas Op. 31) are
        // independent works nested under a container piece, so we also register
        // them at the top level. Set-level hit aggregation happens in a post-
        // processing pass (AggregateSetHits) rather than per-ref, so the set's
        // badge only reflects albums that carry every member of the set.
        var byComposerTitle = new Dictionary<string, Dictionary<string, IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        // Register approved (non-provisional) pieces first so they win the
        // TryAdd tie-break when a provisional duplicate shares the same
        // computed title key. A user's canonical entry like 5707
        // (Title="Piano Concerto #21 in C, KV 467") and an old provisional
        // duplicate like 979 (Form="Piano Concerto" + Number=21 + Key=C/major
        // + catalog "KV 467", which BuildDisplayTitle renders identically)
        // would otherwise be tiebroken by insertion order — and load-order is
        // by id, which gives the provisional duplicate the win.
        // H41: record any same-composer + same-title-key collisions so the
        // seeder + diagnostic UI can surface them. Pre-fix the TryAdd inside
        // RegisterPiece silently swallowed the loser. The approved-first
        // OrderBy still applies, so the kept entry in each collision is the
        // approved piece when one exists.
        var collisions = new List<TitleCollision>();
        foreach (var p in pieces.OrderBy(p => p.IsProvisional))
            RegisterPiece(p, p.Composer?.Trim() ?? "", ancestors: [], byComposerTitle, collisions);
        _byComposerTitle = byComposerTitle;
        Collisions = collisions;

        foreach (var album in albums)
        {
            foreach (var disc in album.Discs)
            {
                foreach (var track in disc.Tracks)
                    IndexTrack(track, album, disc, byComposerTitle,
                               hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);
            }
        }

        // Loose tracks: same indexing, but with album/disc null. AddHitForRef
        // and IndexTrack are agnostic to the container — they take whatever
        // album/disc the caller supplies (or null) and stamp it onto the hit.
        foreach (var track in looseTracks)
            IndexTrack(track, album: null, disc: null, byComposerTitle,
                       hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);

        // Set-level aggregation: a set's own badge should only reflect albums
        // that contain every member of the set — an "Op. 31" album that has
        // only two of the three sonatas shouldn't credit the set container.
        // Computed after the main pass so we can check subpiece hits.
        foreach (var p in pieces)
            AggregateSetHits(p, hitsForPiece, hitsForOriginal);

        _hitsForPiece    = hitsForPiece;
        _hitsForOriginal = hitsForOriginal;
        _hitsForVersion  = hitsForVersion;
        _hitsForComposer = hitsForComposer;

        Indexed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Walks the piece tree post-order and, for every <c>form: "set"</c> container,
    /// replaces its hit lists with the subset of member hits whose album contains
    /// all members of the set. Processed post-order so a set-of-sets sees its
    /// inner sets' aggregated hits before its own aggregation runs.
    /// </summary>
    private static void AggregateSetHits(
        CanonPiece p,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForPiece,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForOriginal)
    {
        // Recurse first so inner sets are resolved before their outer parent.
        if (p.Subpieces is { Count: > 0 })
            foreach (var sub in p.Subpieces)
                AggregateSetHits(sub, hitsForPiece, hitsForOriginal);

        if (!string.Equals(p.Form, "set", StringComparison.OrdinalIgnoreCase)
            || p.Subpieces is null or { Count: 0 })
            return;

        // Intersect album sets across members: an album must reference every
        // member to qualify. Loose-track hits are skipped — a loose track is
        // one track and can't satisfy "contains every member of a set" (unless
        // the set has exactly one member, in which case the math still works
        // via the album-only path).
        HashSet<CanonAlbum>? fullSetAlbums = null;
        foreach (var member in p.Subpieces)
        {
            var memberAlbums = hitsForPiece.TryGetValue(member, out var h)
                ? h.Where(x => x.Album is not null).Select(x => x.Album!).ToHashSet()
                : new HashSet<CanonAlbum>();
            if (fullSetAlbums is null) fullSetAlbums = memberAlbums;
            else fullSetAlbums.IntersectWith(memberAlbums);
            if (fullSetAlbums.Count == 0) break; // early-out: no qualifying albums
        }

        if (fullSetAlbums is null or { Count: 0 })
        {
            // Explicitly clear in case a prior pass left stale hits on p.
            hitsForPiece.Remove(p);
            hitsForOriginal.Remove(p);
            return;
        }

        // Collect every member's hits on the qualifying albums (so the "Show Albums"
        // dialog shows each track referenced, not just one row per album).
        var setHits = new List<PieceAlbumHit>();
        foreach (var member in p.Subpieces)
        {
            if (!hitsForPiece.TryGetValue(member, out var h)) continue;
            foreach (var hit in h)
                if (hit.Album is not null && fullSetAlbums.Contains(hit.Album))
                    setHits.Add(hit);
        }

        hitsForPiece[p]    = setHits;
        hitsForOriginal[p] = setHits;
    }

    /// <summary>Raised after <see cref="Rebuild"/> completes so UI can refresh badges.</summary>
    public event EventHandler? Indexed;

    // ── Public count/hit accessors ────────────────────────────────────────────

    // Badges show distinct-container counts, not raw-track counts — a sonata
    // with 4 movements on 9 albums is "9", not 36. A loose track is its own
    // container, so two loose tracks of the same piece count as 2. Use
    // HitsFor… if you need the full raw hit list.
    public int CountForPiece(CanonPiece piece)      => DistinctContainerCount(HitsForPiece(piece));
    public int CountForOriginal(CanonPiece piece)   => DistinctContainerCount(HitsForOriginal(piece));
    public int CountForVersion(CanonPieceVersion v) => DistinctContainerCount(HitsForVersion(v));
    public int CountForComposer(string name)        => DistinctContainerCount(HitsForComposer(name));

    /// <summary>
    /// Counts distinct containers: albums dedupe by <see cref="CanonAlbum"/>
    /// identity (multiple tracks on the same album count once), and loose
    /// tracks dedupe by <see cref="AlbumTrack"/> identity (each loose track
    /// counts once even if it has multiple piece-refs to the same piece).
    /// </summary>
    private static int DistinctContainerCount(IReadOnlyList<PieceAlbumHit> hits)
    {
        if (hits.Count == 0) return 0;
        var albums = new HashSet<CanonAlbum>();
        var looseTracks = new HashSet<AlbumTrack>();
        foreach (var h in hits)
        {
            if (h.Album is not null) albums.Add(h.Album);
            else                     looseTracks.Add(h.Track);
        }
        return albums.Count + looseTracks.Count;
    }

    public IReadOnlyList<PieceAlbumHit> HitsForPiece(CanonPiece piece)
        => _hitsForPiece.TryGetValue(piece, out var l) ? l : Array.Empty<PieceAlbumHit>();
    public IReadOnlyList<PieceAlbumHit> HitsForOriginal(CanonPiece piece)
        => _hitsForOriginal.TryGetValue(piece, out var l) ? l : Array.Empty<PieceAlbumHit>();
    public IReadOnlyList<PieceAlbumHit> HitsForVersion(CanonPieceVersion v)
        => _hitsForVersion.TryGetValue(v, out var l) ? l : Array.Empty<PieceAlbumHit>();
    public IReadOnlyList<PieceAlbumHit> HitsForComposer(string name)
        => _hitsForComposer.TryGetValue(name, out var l) ? l : Array.Empty<PieceAlbumHit>();

    /// <summary>
    /// Sum of hits across every piece in <paramref name="pieces"/>, deduplicated
    /// so a single hit counted at multiple ancestor pieces only counts once.
    /// Used for the "contributed role group" badge (e.g. "Libretto: Barber").
    /// </summary>
    public int CountForPieces(IEnumerable<CanonPiece> pieces)
        => DistinctContainerCount(HitsForPieces(pieces));

    public IReadOnlyList<PieceAlbumHit> HitsForPieces(IEnumerable<CanonPiece> pieces)
        => pieces.SelectMany(p => _hitsForPiece.TryGetValue(p, out var l)
                                  ? (IEnumerable<PieceAlbumHit>)l : Array.Empty<PieceAlbumHit>())
                 .Distinct()
                 .ToList();

    /// <summary>
    /// Builds only the composer+title index (and caches the piece list), without
    /// doing the album hit aggregation pass. Used by seeders / migration tools
    /// that need <see cref="TryResolve"/> to share the same resolution semantics
    /// as the runtime but don't care about album-hit counts.
    /// </summary>
    public void BuildResolver(IEnumerable<CanonPiece> pieces)
    {
        _cachedPieces = pieces as IReadOnlyList<CanonPiece> ?? pieces.ToList();
        var byComposerTitle = new Dictionary<string, Dictionary<string, IndexEntry>>(
            StringComparer.OrdinalIgnoreCase);
        // Approved pieces first — see comment in RebuildInternal.
        var collisions = new List<TitleCollision>();
        foreach (var p in _cachedPieces.OrderBy(p => p.IsProvisional))
            RegisterPiece(p, p.Composer?.Trim() ?? "", ancestors: [], byComposerTitle, collisions);
        _byComposerTitle = byComposerTitle;
        Collisions = collisions;
    }

    /// <summary>
    /// Resolves a <see cref="TrackPieceRef"/> to the <see cref="CanonPiece"/> /
    /// <see cref="CanonPieceVersion"/> it refers to, or <c>null</c> when the ref
    /// can't be matched. Uses the same strict-then-loose subpiece matching and
    /// title-variant indexing as the hit-tracking pipeline.
    /// Must be called after <see cref="Rebuild"/> or <see cref="BuildResolver"/>.
    /// <para>
    /// Returns the <em>leaf</em> piece — the deepest subpiece walked into via
    /// <see cref="TrackPieceRef.SubpiecePath"/> — so callers (e.g. the seeder)
    /// preserve movement-level identity when persisting refs. When the ref has
    /// no subpath, the entry piece is itself the leaf.
    /// </para>
    /// </summary>
    public (CanonPiece Piece, CanonPieceVersion? Version)? TryResolve(TrackPieceRef pr)
    {
        if (TryResolve(pr, _byComposerTitle, out var piece, out _, out var version,
                       out var ancestorSubpieces))
        {
            var leaf = ancestorSubpieces.Count > 0 ? ancestorSubpieces[^1] : piece;
            return (leaf, version);
        }
        return null;
    }

    /// <summary>
    /// Collects every <see cref="VariantInfo"/> available to a resolved ref,
    /// in path order: the top-level piece, the referenced version (if any),
    /// then each subpiece walked down to the leaf. Variants can live above the
    /// leaf — e.g. an opera's alternate ending sits on the top piece while a
    /// track refs a single scene — so the whole resolved path contributes.
    /// Returns an empty list when the ref doesn't resolve or no node on the
    /// path carries variants. Drives the piece-ref editor's variant picker
    /// (hidden when empty) and the "variant available but unchosen" indicator.
    /// </summary>
    public IReadOnlyList<VariantInfo> CollectAvailableVariants(TrackPieceRef pr)
    {
        if (!TryResolve(pr, _byComposerTitle, out var piece, out _, out var version,
                        out var ancestorSubpieces))
            return Array.Empty<VariantInfo>();

        var result = new List<VariantInfo>();
        if (piece.Variants is { Count: > 0 })   result.AddRange(piece.Variants);
        if (version?.Variants is { Count: > 0 }) result.AddRange(version.Variants);
        foreach (var sub in ancestorSubpieces)
            if (sub.Variants is { Count: > 0 })  result.AddRange(sub.Variants);
        return result;
    }

    /// <summary>
    /// True when <paramref name="pr"/> resolves to a path that defines one or
    /// more variants but the ref itself identifies none — the "variant available
    /// but unchosen" state. Drives the unchosen-ref indicator and the
    /// missing-variant filter/report. "No variant identified" is a valid state,
    /// so this is a findability signal, not an error.
    /// </summary>
    public bool NeedsVariantIdentification(TrackPieceRef pr)
        => pr.Variants is not { Count: > 0 } && CollectAvailableVariants(pr).Count > 0;

    /// <summary>
    /// Resolves <paramref name="pr"/> and credits every bucket that should receive the hit.
    /// Extracted so the PieceRefs loop and the description-fallback path share one code path.
    /// <para>
    /// Range refs (<see cref="TrackPieceRef.EndSubpiecePath"/> populated): when both
    /// endpoints resolve to siblings of the same parent at the same depth, every leaf
    /// in <c>[start..end]</c> inclusive is credited. This matches the through-composed-
    /// opera scenario where a single recording's track spans several adjacent
    /// subpieces (e.g. La bohème Act III, "3j → 3k → 3l"). When the two endpoints
    /// don't share a parent — a misformed ref — the start endpoint is credited alone.
    /// </para>
    /// <para>
    /// Marker anchors (<see cref="TrackPieceRef.StartMarker"/> / <see cref="TrackPieceRef.EndMarker"/>)
    /// don't change credit attribution — they describe <em>where in</em> a subpiece the
    /// recording starts/ends, not <em>which</em> subpiece is referenced. They travel
    /// on the hit for display purposes only.
    /// </para>
    /// </summary>
    /// <summary>
    /// Walks one track's piece-refs (or its parsed Description for uncatalogued
    /// tracks) and credits each hit. <paramref name="album"/> and
    /// <paramref name="disc"/> are null when <paramref name="track"/> is a loose
    /// track — they get stamped onto the <see cref="PieceAlbumHit"/> as-is.
    /// </summary>
    private static void IndexTrack(
        AlbumTrack track,
        CanonAlbum? album,
        AlbumDisc?  disc,
        Dictionary<string, Dictionary<string, IndexEntry>> byComposerTitle,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForPiece,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForOriginal,
        Dictionary<CanonPieceVersion, List<PieceAlbumHit>> hitsForVersion,
        Dictionary<string, List<PieceAlbumHit>> hitsForComposer)
    {
        if ((track.PieceRefs is null || track.PieceRefs.Count == 0)
            && !string.IsNullOrWhiteSpace(track.Description))
        {
            var synth = TryParseDescription(track.Description!, byComposerTitle);
            if (synth is not null)
            {
                AddHitForRef(synth, album, disc, track, byComposerTitle,
                             hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);
            }
            return;
        }

        if (track.PieceRefs is null) return;
        foreach (var pr in track.PieceRefs)
            AddHitForRef(pr, album, disc, track, byComposerTitle,
                         hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);
    }

    private static void AddHitForRef(
        TrackPieceRef pr, CanonAlbum? album, AlbumDisc? disc, AlbumTrack track,
        Dictionary<string, Dictionary<string, IndexEntry>> byComposerTitle,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForPiece,
        Dictionary<CanonPiece, List<PieceAlbumHit>> hitsForOriginal,
        Dictionary<CanonPieceVersion, List<PieceAlbumHit>> hitsForVersion,
        Dictionary<string, List<PieceAlbumHit>> hitsForComposer)
    {
        if (!TryResolve(pr, byComposerTitle, out var piece, out var setAncestors,
                        out var version, out var ancestorSubpieces))
            return;

        var hit = new PieceAlbumHit(album, disc, track, pr);

        // Composer credit — use the matched piece's composer, falling back to the ref's
        // composer (subpieces of a set inherit from the set and often have null Composer).
        var composerName = piece.Composer ?? pr.Composer ?? "";
        if (composerName.Length > 0)
            Add(hitsForComposer, composerName, hit);

        foreach (var contribName in CollectContributors(piece))
            if (!string.Equals(contribName, composerName, StringComparison.OrdinalIgnoreCase))
                Add(hitsForComposer, contribName, hit);

        // Set containers (e.g. "Three Piano Sonatas, Op. 31") are handled by
        // the post-processing AggregateSetHits pass — they should only count
        // albums that carry every member of the set, which we can't decide
        // here on a per-ref basis. The setAncestors list stays on the
        // IndexEntry in case a future caller needs it, but we don't credit
        // it on each hit anymore.
        _ = setAncestors;

        Add(hitsForPiece, piece, hit);

        if (version is not null)
        {
            Add(hitsForVersion, version, hit);
            foreach (var sp in ancestorSubpieces)
                Add(hitsForPiece, sp, hit);
        }
        else
        {
            Add(hitsForOriginal, piece, hit);
            foreach (var sp in ancestorSubpieces)
            {
                Add(hitsForPiece, sp, hit);
                Add(hitsForOriginal, sp, hit);
            }
        }

        // Range credit: walk the sibling list from the start leaf to the end
        // leaf (inclusive) and credit each intermediate sibling. We re-resolve
        // the end path through TryResolve so loose-match rules apply uniformly
        // and the resulting end-leaf is identity-comparable to the canon's
        // CanonPiece instances used as keys in hitsForPiece.
        if (pr.EndSubpiecePath is { Count: > 0 })
        {
            var endProbe = new TrackPieceRef
            {
                Composer           = pr.Composer ?? "",
                PieceTitle         = pr.PieceTitle ?? "",
                VersionDescription = pr.VersionDescription,
                SubpiecePath       = pr.EndSubpiecePath,
            };
            if (TryResolve(endProbe, byComposerTitle, out _, out _, out _,
                           out var endAncestors) &&
                endAncestors.Count == ancestorSubpieces.Count &&
                endAncestors.Count > 0)
            {
                // Identify the parent of both endpoints and the index range.
                // start/end-1 are the immediate parents in the path; if the
                // chain matches up to that point, the last segment names
                // siblings under the same parent.
                bool sharedParent = true;
                for (int i = 0; i < ancestorSubpieces.Count - 1 && sharedParent; i++)
                    sharedParent = ReferenceEquals(ancestorSubpieces[i], endAncestors[i]);

                if (sharedParent)
                {
                    var siblings = ancestorSubpieces.Count == 1
                        ? piece.Subpieces
                        : ancestorSubpieces[^2].Subpieces;
                    if (siblings is { Count: > 0 })
                    {
                        var startIdx = siblings.IndexOf(ancestorSubpieces[^1]);
                        var endIdx   = siblings.IndexOf(endAncestors[^1]);
                        if (startIdx >= 0 && endIdx >= 0 && endIdx >= startIdx)
                        {
                            for (int i = startIdx + 1; i <= endIdx; i++)
                            {
                                var sib = siblings[i];
                                Add(hitsForPiece, sib, hit);
                                if (version is null) Add(hitsForOriginal, sib, hit);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Registers a piece in the composer+title lookup, then recurses into any
    /// <c>form: "set"</c> subpieces so each constituent work is discoverable by
    /// its own title while still crediting the set container on hit.
    ///
    /// <para>H41: when a key already has an entry, the new piece is dropped
    /// (TryAdd semantics) AND a <see cref="TitleCollision"/> is appended to
    /// <paramref name="collisions"/> so callers can surface the diagnostic.
    /// The first-write-wins behaviour is preserved (combined with the
    /// approved-first OrderBy in <see cref="RebuildInternal"/>) — the kept
    /// piece is the approved one when one of the colliders is provisional.</para>
    /// </summary>
    private static void RegisterPiece(
        CanonPiece p, string composer, IReadOnlyList<CanonPiece> ancestors,
        Dictionary<string, Dictionary<string, IndexEntry>> index,
        List<TitleCollision> collisions)
    {
        if (composer.Length == 0) return;
        if (!index.TryGetValue(composer, out var titleMap))
            index[composer] = titleMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (var key in EnumerateTitleKeys(p))
        {
            var norm = NormalizeTitle(key);
            if (titleMap.TryGetValue(norm, out var existing))
            {
                // Don't self-collide: a piece that emits the same key under
                // multiple variants (e.g. Title == DisplayTitle when no
                // catalog/key) shouldn't show as a "collision against
                // itself". Only record a collision when the conflicting
                // entry belongs to a different piece.
                if (!ReferenceEquals(existing.Piece, p))
                    collisions.Add(new TitleCollision(composer, norm, KeptPiece: existing.Piece, DroppedPiece: p));
                continue;
            }
            titleMap[norm] = new IndexEntry(p, ancestors);
        }

        // Recurse into set-type containers: their subpieces are independent
        // works, not movements. Other forms' subpieces are movements, reached
        // through SubpiecePath resolution instead.
        if (string.Equals(p.Form, "set", StringComparison.OrdinalIgnoreCase)
            && p.Subpieces is { Count: > 0 })
        {
            var childAncestors = new List<CanonPiece>(ancestors) { p };
            foreach (var sub in p.Subpieces)
            {
                var subComposer = !string.IsNullOrWhiteSpace(sub.Composer)
                    ? sub.Composer!.Trim()
                    : composer;
                RegisterPiece(sub, subComposer, childAncestors, index, collisions);
            }
        }
    }

    /// <summary>A piece plus its chain of containing "set" pieces (root → direct parent).</summary>
    private readonly record struct IndexEntry(CanonPiece Piece, IReadOnlyList<CanonPiece> SetAncestors);

    /// <summary>
    /// Parses a free-text track description of the form
    /// <c>"Composer: Piece Title [- subpath [- deeper subpath ...]]"</c> and,
    /// if it matches a known piece, returns a synthesised <see cref="TrackPieceRef"/>.
    /// The " - " in a movement name itself (e.g. "Scherzo. Sehr schnell - Trio. Etwas langsamer")
    /// is disambiguated by trying longer title prefixes before falling back to shorter ones.
    /// </summary>
    private static TrackPieceRef? TryParseDescription(
        string description,
        Dictionary<string, Dictionary<string, IndexEntry>> index)
    {
        var colonIdx = description.IndexOf(':');
        if (colonIdx <= 0) return null;

        var composer = description[..colonIdx].Trim();
        var body     = description[(colonIdx + 1)..].Trim();
        if (composer.Length == 0 || body.Length == 0) return null;
        if (!index.TryGetValue(composer, out var titleMap)) return null;

        // Try the whole body as the title first; then progressively peel " - segment"
        // suffixes off the right and treat them as the subpiece path.
        var segments = SplitOnSeparator(body, " - ");
        for (var titleSegs = segments.Count; titleSegs >= 1; titleSegs--)
        {
            var titleCandidate = string.Join(" - ", segments.Take(titleSegs));
            if (!titleMap.TryGetValue(NormalizeTitle(titleCandidate), out var entry)) continue;

            var pathSegs = segments.Skip(titleSegs).ToList();
            return new TrackPieceRef
            {
                Composer     = entry.Piece.Composer ?? composer,
                PieceTitle   = entry.Piece.Title ?? titleCandidate,
                SubpiecePath = pathSegs.Count > 0 ? pathSegs : null,
            };
        }
        return null;
    }

    private static List<string> SplitOnSeparator(string s, string sep)
    {
        var list = new List<string>();
        var start = 0;
        while (true)
        {
            var idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0) { list.Add(s[start..]); return list; }
            list.Add(s[start..idx]);
            start = idx + sep.Length;
        }
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a ref to its terminal piece/version. <paramref name="ancestorSubpieces"/>
    /// receives every subpiece-CanonPiece walked through on the path (excluding the
    /// top-level piece itself, which is returned separately).
    /// </summary>
    private static bool TryResolve(
        TrackPieceRef pr,
        Dictionary<string, Dictionary<string, IndexEntry>> index,
        out CanonPiece piece,
        out IReadOnlyList<CanonPiece> setAncestors,
        out CanonPieceVersion? version,
        out List<CanonPiece> ancestorSubpieces)
    {
        piece = null!;
        setAncestors = Array.Empty<CanonPiece>();
        version = null;
        ancestorSubpieces = [];

        if (!index.TryGetValue(pr.Composer?.Trim() ?? "", out var byTitle)) return false;
        if (!byTitle.TryGetValue(NormalizeTitle(pr.PieceTitle ?? ""), out var entry)) return false;
        piece = entry.Piece;
        setAncestors = entry.SetAncestors;
        var p = entry.Piece;

        List<CanonPiece>? subpieces;
        if (pr.VersionId != 0 || !string.IsNullOrWhiteSpace(pr.VersionDescription))
        {
            if (p.Versions is null) return false;
            // Prefer the stable id; fall back to description (JSON import / post-reseed).
            version = (pr.VersionId != 0
                          ? p.Versions.FirstOrDefault(v => v.Id == pr.VersionId)
                          : null)
                      ?? (!string.IsNullOrWhiteSpace(pr.VersionDescription)
                          ? p.Versions.FirstOrDefault(v =>
                                string.Equals(v.Description, pr.VersionDescription, StringComparison.OrdinalIgnoreCase))
                          : null);
            if (version is null) return false;
            subpieces = version.Subpieces;
        }
        else
        {
            subpieces = p.Subpieces;
        }

        if (pr.SubpiecePath is { Count: > 0 })
        {
            foreach (var segment in pr.SubpiecePath)
            {
                if (subpieces is null or { Count: 0 }) return false;
                // Try strict match first; fall back to looser prefix / number match
                // so that albums cataloguing extra tempo markings still resolve
                // (e.g. album "3. Rondo. Allegretto - Adagio - Tempo I - Adagio -
                // Presto" against a Canon piece whose tempos list only carries
                // "Allegretto").
                var match = subpieces.FirstOrDefault(s => StrictSubpieceMatch(s, segment))
                         ?? subpieces.FirstOrDefault(s => LooseSubpieceMatch(s, segment));
                if (match is null) return false;
                ancestorSubpieces.Add(match);
                subpieces = match.Subpieces;
            }
        }

        return true;
    }

    /// <summary>
    /// Exact-equality match against any of the known title variants.
    /// </summary>
    private static bool StrictSubpieceMatch(CanonPiece sp, string segment)
    {
        var normSeg = NormalizeTitle(segment);
        return string.Equals(NormalizeTitle(sp.Title ?? ""), normSeg, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeTitle(sp.DisplayTitle), normSeg, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeTitle(sp.SubpieceDisplayTitle), normSeg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looser match used as a fallback. Accepts the segment when:
    /// (1) it starts with the piece's subpiece title followed by a ". " or " - "
    ///     joiner — this catches album refs that append extra tempo markings
    ///     (e.g. "… Allegretto - Adagio - Tempo I …") absent from the Canon data; or
    /// (2) it starts with the piece's "N. " number prefix and no better match
    ///     exists — a safety net for minor form/tempo wording differences.
    /// </summary>
    private static bool LooseSubpieceMatch(CanonPiece sp, string segment)
    {
        var normSeg = NormalizeTitle(segment);
        var subTitle = NormalizeTitle(sp.SubpieceDisplayTitle ?? "");
        if (subTitle.Length > 0)
        {
            if (normSeg.StartsWith(subTitle + " - ", StringComparison.OrdinalIgnoreCase)) return true;
            if (normSeg.StartsWith(subTitle + ". ", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (sp.Number.HasValue)
        {
            var numPrefix = $"{sp.Number}. ";
            if (normSeg.StartsWith(numPrefix, StringComparison.Ordinal))
                return true;

            // "N<letter>. " prefix — lettered sub-section of movement N
            // (e.g. "1a. Allegro maestoso…", "5c. Langsam"), common in
            // rehearsal-style albums that split single movements into
            // their constituent tempi. Credit them against the main
            // movement rather than dropping the hit.
            var digits = sp.Number.Value.ToString();
            if (normSeg.Length >= digits.Length + 3
                && normSeg.StartsWith(digits, StringComparison.Ordinal)
                && char.IsLetter(normSeg[digits.Length])
                && normSeg[digits.Length + 1] == '.'
                && normSeg[digits.Length + 2] == ' ')
                return true;

            // Zero-padded numeric prefix — e.g. "01. ", "02. ". Some albums
            // (Mahler Symphony #8, etc.) pad track numbers to a fixed width
            // so "01. Part I. Hymnus…" should still resolve to movement 1.
            var padded = digits.PadLeft(2, '0') + ". ";
            if (padded.Length != numPrefix.Length
                && normSeg.StartsWith(padded, StringComparison.Ordinal))
                return true;

            // Reverse case: the subpiece display carries the "N. " prefix
            // but the album ref does not (e.g. Symphony #10's sole movement
            // is stored as "1. Adagio" but recorded on albums as just
            // "Adagio"). Accept when stripping the "N. " prefix makes the
            // display title equal to the segment.
            if (subTitle.StartsWith(numPrefix, StringComparison.Ordinal)
                && string.Equals(subTitle[numPrefix.Length..], normSeg,
                                 StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every title string under which a piece might be referenced from a
    /// <see cref="TrackPieceRef.PieceTitle"/>. Matches what PiecePickerWindow
    /// writes today ("Piece.Title ?? DisplayTitle") plus historical variants —
    /// in particular, album refs often omit the nickname and subtitle suffixes
    /// that <c>DisplayTitle</c> appends (e.g. album says
    /// "Piano Sonata #17 in d, Op. 31 #2" but DisplayTitle is
    /// "Piano Sonata #17 in d, Op. 31 #2 \"Tempest\""), so the nicknamed
    /// subpieces of Beethoven's Op. 31 set would otherwise fail to resolve.
    /// </summary>
    private static IEnumerable<string> EnumerateTitleKeys(CanonPiece p)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Emit(string? s, List<string> collector)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var t = s.Trim();
            if (seen.Add(t)) collector.Add(t);
        }

        var keys = new List<string>();
        Emit(p.Title, keys);
        var dt  = p.DisplayTitle;
        var dts = p.DisplayTitleShort;
        Emit(dt,  keys);
        Emit(dts, keys);
        Emit(StripNicknameAndSubtitle(dt,  p), keys);
        Emit(StripNicknameAndSubtitle(dts, p), keys);
        return keys;
    }

    /// <summary>
    /// Strips the trailing <c>, Subtitle</c> and/or <c> "Nickname"</c> suffixes
    /// that <see cref="CanonPiece.BuildDisplayTitle"/> appends, yielding the
    /// canonical title form that most album refs use.
    /// </summary>
    private static string? StripNicknameAndSubtitle(string? displayTitle, CanonPiece p)
    {
        if (string.IsNullOrWhiteSpace(displayTitle)) return null;
        var result = displayTitle;

        // Nickname suffix: ` "Nickname"` (appended last by BuildDisplayTitle).
        if (!string.IsNullOrEmpty(p.Nickname))
        {
            var nick = $" \"{p.Nickname}\"";
            if (result.EndsWith(nick, StringComparison.Ordinal))
                result = result[..^nick.Length];
        }
        // Subtitle suffix: `, Subtitle` (appended before the nickname).
        if (!string.IsNullOrEmpty(p.Subtitle))
        {
            var sub = $", {p.Subtitle}";
            if (result.EndsWith(sub, StringComparison.Ordinal))
                result = result[..^sub.Length];
        }
        return result;
    }

    private static IEnumerable<string> CollectContributors(CanonPiece piece)
    {
        // Structured composers list (preferred) — ComposerCredit entries with role != primary/composer.
        if (piece.Composers is { Count: > 0 })
        {
            foreach (var c in piece.Composers)
            {
                var name = c.Name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                // A contributor is anyone in Composers[] other than the main composer.
                // We yield unconditionally — the caller filters out the primary composer.
                yield return name;
            }
        }
    }

    /// <summary>
    /// Canonicalises a title so refs written with Unicode accidentals
    /// ("E♭", "F♯") match pieces whose <c>DisplayTitle</c> uses the hyphenated
    /// ASCII spelling ("E-flat", "F-sharp"), and vice versa. Case/whitespace
    /// are left to the dictionary's <c>OrdinalIgnoreCase</c> comparer.
    /// </summary>
    private static string NormalizeTitle(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // ♭ U+266D, ♯ U+266F — convert to the hyphenated ASCII form.
        // Also handle the double flat/sharp (U+1D12B / U+1D12A) if they ever appear.
        s = s.Replace("\u266D", "-flat")
             .Replace("\u266F", "-sharp")
             .Replace("\u1D12B", "-double-flat")
             .Replace("\u1D12A", "-double-sharp");
        return s.Trim();
    }

    private static void Add<TKey>(Dictionary<TKey, List<PieceAlbumHit>> dict, TKey key, PieceAlbumHit hit)
        where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        list.Add(hit);
    }
}

/// <summary>
/// A same-composer + same-normalized-title-key collision detected during
/// <see cref="PieceReferenceIndex"/> build. The <see cref="KeptPiece"/>
/// won the slot in the resolver (its album refs continue to resolve);
/// the <see cref="DroppedPiece"/> is unreachable for that key, so any
/// album ref pointing at it via that title variant silently fails and
/// the piece's badge stays at zero. Surface via the seeder's report and
/// any future diagnostics tab in the app — H41.
/// </summary>
public sealed record TitleCollision(
    string Composer,
    string NormalizedKey,
    CanonPiece KeptPiece,
    CanonPiece DroppedPiece);
