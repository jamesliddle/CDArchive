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

    public PieceReferenceIndex() { Current = this; }

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

    // The piece list used on the last Rebuild. Cached so album-only rebuilds
    // (RebuildAlbums) can reuse the same CanonPiece instances — critical
    // because the CanonView tree holds reference-identity keys into the hit
    // dictionaries. Re-loading pieces from JSON would produce new instances
    // whose lookups miss, which manifested as badges vanishing after the
    // Albums screen refreshed the index.
    private IReadOnlyList<CanonPiece> _cachedPieces = Array.Empty<CanonPiece>();

    /// <summary>
    /// Rebuilds all indexes from the current piece and album collections.
    /// Thread-safe to call from a background load path so long as the caller
    /// doesn't read the index concurrently.
    /// </summary>
    public void Rebuild(IEnumerable<CanonPiece> pieces, IEnumerable<CanonAlbum> albums)
    {
        _cachedPieces = pieces as IReadOnlyList<CanonPiece> ?? pieces.ToList();
        RebuildInternal(_cachedPieces, albums);
    }

    /// <summary>
    /// Rebuilds the index using the piece list from the last <see cref="Rebuild"/>
    /// call but with a fresh album set. Use this when only album data has changed
    /// (e.g. after the Albums screen saves edits) so badge-dictionary keys stay
    /// reference-equal to the <see cref="CanonPiece"/> instances held by the
    /// Canon tree view — otherwise the tree's lookups would start returning 0.
    /// </summary>
    public void RebuildAlbums(IEnumerable<CanonAlbum> albums)
    {
        RebuildInternal(_cachedPieces, albums);
    }

    private void RebuildInternal(IReadOnlyList<CanonPiece> pieces, IEnumerable<CanonAlbum> albums)
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
        foreach (var p in pieces)
            RegisterPiece(p, p.Composer?.Trim() ?? "", ancestors: [], byComposerTitle);

        foreach (var album in albums)
        {
            foreach (var disc in album.Discs)
            {
                foreach (var track in disc.Tracks)
                {
                    // Uncatalogued tracks: fall back to parsing free-text descriptions like
                    // "Chopin, Frédéric: Scherzo #1 in b, Op. 20 [- movement…]". We synthesise
                    // a TrackPieceRef on the fly so the hit participates in all the normal
                    // piece/version/composer buckets.
                    if ((track.PieceRefs is null || track.PieceRefs.Count == 0)
                        && !string.IsNullOrWhiteSpace(track.Description))
                    {
                        var synth = TryParseDescription(track.Description!, byComposerTitle);
                        if (synth is not null)
                        {
                            AddHitForRef(synth, album, disc, track, byComposerTitle,
                                         hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);
                        }
                        continue;
                    }

                    if (track.PieceRefs is null) continue;
                    foreach (var pr in track.PieceRefs)
                        AddHitForRef(pr, album, disc, track, byComposerTitle,
                                     hitsForPiece, hitsForOriginal, hitsForVersion, hitsForComposer);
                }
            }
        }

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
        // member to qualify.
        HashSet<CanonAlbum>? fullSetAlbums = null;
        foreach (var member in p.Subpieces)
        {
            var memberAlbums = hitsForPiece.TryGetValue(member, out var h)
                ? h.Select(x => x.Album).ToHashSet()
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
                if (fullSetAlbums.Contains(hit.Album))
                    setHits.Add(hit);
        }

        hitsForPiece[p]    = setHits;
        hitsForOriginal[p] = setHits;
    }

    /// <summary>Raised after <see cref="Rebuild"/> completes so UI can refresh badges.</summary>
    public event EventHandler? Indexed;

    // ── Public count/hit accessors ────────────────────────────────────────────

    // Badges show album counts, not track counts — a sonata with 4 movements on
    // 9 albums is "9", not 36. Use HitsFor… if you need the full raw hit list.
    public int CountForPiece(CanonPiece piece)      => DistinctAlbumCount(HitsForPiece(piece));
    public int CountForOriginal(CanonPiece piece)   => DistinctAlbumCount(HitsForOriginal(piece));
    public int CountForVersion(CanonPieceVersion v) => DistinctAlbumCount(HitsForVersion(v));
    public int CountForComposer(string name)        => DistinctAlbumCount(HitsForComposer(name));

    private static int DistinctAlbumCount(IReadOnlyList<PieceAlbumHit> hits)
    {
        if (hits.Count == 0) return 0;
        var seen = new HashSet<CanonAlbum>();
        foreach (var h in hits) seen.Add(h.Album);
        return seen.Count;
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
        => DistinctAlbumCount(HitsForPieces(pieces));

    public IReadOnlyList<PieceAlbumHit> HitsForPieces(IEnumerable<CanonPiece> pieces)
        => pieces.SelectMany(p => _hitsForPiece.TryGetValue(p, out var l)
                                  ? (IEnumerable<PieceAlbumHit>)l : Array.Empty<PieceAlbumHit>())
                 .Distinct()
                 .ToList();

    /// <summary>
    /// Resolves <paramref name="pr"/> and credits every bucket that should receive the hit.
    /// Extracted so the PieceRefs loop and the description-fallback path share one code path.
    /// </summary>
    private static void AddHitForRef(
        TrackPieceRef pr, CanonAlbum album, AlbumDisc disc, AlbumTrack track,
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
    }

    /// <summary>
    /// Registers a piece in the composer+title lookup, then recurses into any
    /// <c>form: "set"</c> subpieces so each constituent work is discoverable by
    /// its own title while still crediting the set container on hit.
    /// </summary>
    private static void RegisterPiece(
        CanonPiece p, string composer, IReadOnlyList<CanonPiece> ancestors,
        Dictionary<string, Dictionary<string, IndexEntry>> index)
    {
        if (composer.Length == 0) return;
        if (!index.TryGetValue(composer, out var titleMap))
            index[composer] = titleMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (var key in EnumerateTitleKeys(p))
            titleMap.TryAdd(NormalizeTitle(key), new IndexEntry(p, ancestors));

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
                RegisterPiece(sub, subComposer, childAncestors, index);
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
        if (!string.IsNullOrWhiteSpace(pr.VersionDescription))
        {
            if (p.Versions is null) return false;
            version = p.Versions.FirstOrDefault(v =>
                string.Equals(v.Description, pr.VersionDescription, StringComparison.OrdinalIgnoreCase));
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
