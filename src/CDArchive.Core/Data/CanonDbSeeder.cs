using System.Text.Json;
using System.Text.Json.Serialization;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Data;

/// <summary>
/// One-shot migration: takes the Canon JSON files and populates a freshly-created
/// SQLite database via <see cref="CanonDbContext"/>.
///
/// <para>
/// Album track piece-refs are resolved against the piece tree using the computed
/// <c>DisplayTitle</c> / <c>BuildSubpieceTitle</c> strings, because that's what
/// the existing JSON data actually contains. Unresolvable refs are dropped and
/// reported; if a track ends up with no resolved refs at all, the first broken
/// ref's display summary is copied into <c>album_tracks.description</c> so the
/// information isn't lost.
/// </para>
/// </summary>
public class CanonDbSeeder
{
    private readonly CanonDbContext _db;

    // Lookup tables populated during seeding, used later for album-ref resolution.
    private readonly Dictionary<string, long> _composerIdByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CanonPiece, PieceRow> _pieceRowByModel = new();
    private readonly Dictionary<CanonPieceVersion, PieceVersionRow> _versionRowByModel = new();
    // Marker-instance → row, used by ResolvePieceRefs to translate
    // TrackPieceRef.StartMarker/EndMarker (which may carry a model marker by
    // Id or by Kind+Value match) to the persisted row's auto-assigned id.
    private readonly Dictionary<MusicalMarker, PieceMarkerRow> _markerRowByModel = new();

    // Shares resolution logic (title variants, set recursion, strict+loose
    // subpiece matching) with the runtime badge pipeline.
    private readonly PieceReferenceIndex _resolver = new();

    public CanonDbSeeder(CanonDbContext db)
    {
        _db = db;
    }

    public async Task<SeedResult> SeedAsync(
        List<CanonComposer> composers,
        List<CanonPiece> pieces,
        CanonPickLists pickLists,
        List<CanonAlbum> albums)
    {
        var report = new SeedResult();

        // Rework H43: wrap the three SaveChanges in one transaction. Pre-fix
        // composers + pieces committed independently before albums ran, so
        // a SeedAlbums failure (duplicate (Label, CatalogueNumber) against
        // the filtered unique index, a CHECK violation, etc.) left the
        // composer + piece rows behind with no recovery signal — the user
        // saw a partial DB and could not tell from --export afterward what
        // was missing. Single transaction means a mid-seed failure rolls
        // every subsystem back to the pre-seed empty state.
        await using var tx = await _db.Database.BeginTransactionAsync();

        SeedComposers(composers, report);
        SeedPickLists(pickLists, report);
        await _db.SaveChangesAsync();

        SeedPieces(pieces, report);
        await _db.SaveChangesAsync();

        SeedAlbums(albums, report);
        await _db.SaveChangesAsync();

        await tx.CommitAsync();
        return report;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Composers
    // ─────────────────────────────────────────────────────────────────────────
    private void SeedComposers(List<CanonComposer> composers, SeedResult report)
    {
        foreach (var c in composers)
        {
            var row = new ComposerRow
            {
                Name         = c.Name,
                SortName     = !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                BirthDate    = c.BirthDate,
                BirthPlace   = c.BirthPlace,
                BirthState   = c.BirthState,
                BirthCountry = c.BirthCountry,
                DeathDate    = c.DeathDate,
                DeathPlace   = c.DeathPlace,
                DeathState   = c.DeathState,
                DeathCountry = c.DeathCountry,
                Notes        = c.Notes,
                // Rework H42: preserve the JSON IsProvisional value. Pre-fix
                // this fell through to the row class's C# default of `true`,
                // so every reseed reset every approval the user had ever
                // applied — silent loss of months of curation on the
                // documented recovery path.
                IsProvisional = c.IsProvisional,
            };

            if (c.Aliases is { Count: > 0 })
                for (int i = 0; i < c.Aliases.Count; i++)
                    row.Aliases.Add(new ComposerAliasRow { Position = i, Alias = c.Aliases[i] });

            if (c.CatalogPrefixes is { Count: > 0 })
                for (int i = 0; i < c.CatalogPrefixes.Count; i++)
                    row.CatalogPrefixes.Add(
                        new ComposerCatalogPrefixRow { Position = i, Prefix = c.CatalogPrefixes[i] });

            _db.Composers.Add(row);
            // Populate the name index after SaveChanges resolves Id; we capture the row now
            // and fill the map in FinaliseComposerIndex().
            report.ComposerRows.Add((c, row));
        }
    }

    private void FinaliseComposerIndex(SeedResult report)
    {
        foreach (var (model, row) in report.ComposerRows)
            _composerIdByName[model.Name] = row.Id;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pick lists
    // ─────────────────────────────────────────────────────────────────────────
    private void SeedPickLists(CanonPickLists pl, SeedResult report)
    {
        AddStringList("forms",            pl.Forms);
        AddStringList("categories",       pl.Categories);
        AddStringList("catalog_prefixes", pl.CatalogPrefixes);
        AddStringList("key_tonalities",   pl.KeyTonalities);
        AddStringList("voice_types",      pl.VoiceTypes);
        AddStringList("instruments",      pl.Instruments);
        AddStringList("creative_roles",   pl.CreativeRoles);
        AddStringList("performer_roles",  pl.PerformerRoles);
        AddStringList("labels",           pl.Labels);

        if (pl.Ensembles is { Count: > 0 })
        {
            for (int i = 0; i < pl.Ensembles.Count; i++)
            {
                _db.PickListValues.Add(new PickListValueRow
                {
                    ListName  = "ensembles",
                    Position  = i,
                    Value     = pl.Ensembles[i].Name,
                    ValueJson = JsonSerializer.Serialize(pl.Ensembles[i], _writeOptions),
                });
            }
        }

        report.PickListValueCount = CountPickListValues(pl);
    }

    private void AddStringList(string listName, IList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
            _db.PickListValues.Add(new PickListValueRow
            {
                ListName = listName,
                Position = i,
                Value    = values[i],
            });
    }

    private static int CountPickListValues(CanonPickLists pl) =>
        pl.Forms.Count + pl.Categories.Count + pl.CatalogPrefixes.Count +
        pl.KeyTonalities.Count + pl.VoiceTypes.Count + pl.Instruments.Count +
        pl.CreativeRoles.Count + pl.PerformerRoles.Count + pl.Labels.Count +
        (pl.Ensembles?.Count ?? 0);

    // ─────────────────────────────────────────────────────────────────────────
    // Pieces
    // ─────────────────────────────────────────────────────────────────────────
    private void SeedPieces(List<CanonPiece> pieces, SeedResult report)
    {
        FinaliseComposerIndex(report);

        // Deterministic ordering for stable `position` assignment.
        var ordered = pieces
            .Select((p, i) => (piece: p, originalIndex: i))
            .OrderBy(t => t.piece.Composer ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.originalIndex)
            .ToList();

        int position = 0;
        var orderedPieces = new List<CanonPiece>();
        foreach (var (piece, _) in ordered)
        {
            if (!TryGetComposerId(piece.Composer, report, out var composerId))
                continue;

            var row = MapPiece(piece, composerId, position: position++);
            _db.Pieces.Add(row);
            _pieceRowByModel[piece] = row;
            orderedPieces.Add(piece);
        }

        // Build the shared resolver over all pieces now that _pieceRowByModel is populated.
        _resolver.BuildResolver(orderedPieces);

        // H41: surface same-composer + same-title-key collisions in the seed
        // report. These are pieces unreachable via TryResolve for that title
        // key — typically a sign of duplicate data the user should clean up.
        report.IndexCollisions.AddRange(_resolver.Collisions);

        report.PieceCount = _pieceRowByModel.Count;
    }

    private bool TryGetComposerId(string? composerName, SeedResult report, out long composerId)
    {
        if (!string.IsNullOrWhiteSpace(composerName) &&
            _composerIdByName.TryGetValue(composerName, out composerId))
            return true;

        if (!string.IsNullOrWhiteSpace(composerName))
            report.UnknownComposerReferences.Add(composerName);
        composerId = 0;
        return false;
    }

    private PieceRow MapPiece(CanonPiece src, long composerId, int position)
    {
        var row = new PieceRow
        {
            ComposerId              = composerId,
            Position                = position,
            Title                   = src.Title,
            TitleEnglish            = src.TitleEnglish,
            Subtitle                = src.Subtitle,
            Nickname                = src.Nickname,
            Form                    = src.Form,
            Number                  = src.Number,
            MusicNumber             = src.MusicNumber,
            KeyTonality             = src.KeyTonality,
            KeyMode                 = src.KeyMode,
            PublicationYear         = src.PublicationYear,
            InstrumentationCategory = src.InstrumentationCategory,
            NumberedSubpieces       = src.NumberedSubpieces,
            SubpiecesStart          = src.SubpiecesStart,
            Notes                   = src.Notes,
            // Rework H42: preserve the JSON IsProvisional. MapPiece is called
            // recursively for subpieces, so the fix automatically propagates
            // through the whole piece tree.
            IsProvisional           = src.IsProvisional,

            InstrumentationJson     = RawJson(src.Instrumentation),
            CompositionYearsJson    = RawJson(src.CompositionYears),
            TextAuthorJson          = RawJson(src.TextAuthor),
            RolesJson               = RawJson(src.Roles),
            ArrangementsJson        = RawJson(src.Arrangements),
            CadenzaJson             = RawJson(src.Cadenza),
            TitleNumberJson         = RawJson(src.TitleNumber),

            CatalogSortPrefix       = src.CatalogSortPrefix,
            CatalogSortNumber       = src.CatalogSortNumber,
            CatalogSortSuffix       = src.CatalogSortSuffix,
        };

        AddCatalogEntries(row.CatalogEntries, src.CatalogInfo);
        AddMarkers(row.Markers, src.Markers);
        // Stage-1 migration: every legacy tempo / first-line entry also
        // becomes a kind=Tempo / kind=FirstLine marker so the unified marker
        // pipeline (track anchors, picker UI) sees the data without the user
        // re-entering it. piece_tempos and pieces.first_line stay populated
        // for now — Stage 3 will retire them once consumers are migrated.
        SynthesizeLegacyAnchorMarkers(row.Markers, src.Tempos, src.FirstLine);
        AddComposerCredits(row.ComposerCredits, src.Composers);
        AddVariants(row.Variants, src.Variants);

        if (src.Subpieces is { Count: > 0 })
        {
            for (int i = 0; i < src.Subpieces.Count; i++)
            {
                // Subpieces inherit the parent's composer unless they declare
                // their own (collaborative works like L'éventail de Jeanne, where
                // each movement has a different composer). The parent's composer
                // is the fallback so a Beethoven sonata's movements still map to
                // Beethoven without per-movement composer fields in the JSON.
                var childComposerId = ResolveSubpieceComposerId(src.Subpieces[i].Composer, composerId);
                var childRow = MapPiece(src.Subpieces[i], childComposerId, position: i);
                row.Subpieces.Add(childRow);
                _pieceRowByModel[src.Subpieces[i]] = childRow;
            }
        }

        if (src.Versions is { Count: > 0 })
        {
            for (int i = 0; i < src.Versions.Count; i++)
            {
                var vRow = MapVersion(src.Versions[i], position: i);
                row.Versions.Add(vRow);
                _versionRowByModel[src.Versions[i]] = vRow;

                // Version subpieces live in the pieces table with parent_version_id set.
                if (src.Versions[i].Subpieces is { Count: > 0 })
                {
                    for (int j = 0; j < src.Versions[i].Subpieces!.Count; j++)
                    {
                        var versionSubpiece  = src.Versions[i].Subpieces![j];
                        var childComposerId  = ResolveSubpieceComposerId(versionSubpiece.Composer, composerId);
                        var childRow         = MapPiece(versionSubpiece, childComposerId, position: j);
                        vRow.Subpieces.Add(childRow);
                        _pieceRowByModel[versionSubpiece] = childRow;
                    }
                }
            }
        }

        return row;
    }

    /// <summary>
    /// Resolves a subpiece's composer id, honouring an explicit subpiece-level
    /// <c>composer</c> field when present, falling back to the parent's
    /// composer id. Used for collaborative works (e.g. <em>L'éventail de Jeanne</em>)
    /// where the parent piece's composer is the sentinel <c>(Various)</c> and
    /// each movement carries its real composer in the subpiece-level field.
    /// </summary>
    private long ResolveSubpieceComposerId(string? subpieceComposer, long fallbackComposerId)
    {
        if (!string.IsNullOrWhiteSpace(subpieceComposer) &&
            _composerIdByName.TryGetValue(subpieceComposer, out var resolvedId))
            return resolvedId;
        return fallbackComposerId;
    }

    private PieceVersionRow MapVersion(CanonPieceVersion src, int position)
    {
        var row = new PieceVersionRow
        {
            Position                  = position,
            Description               = src.Description,
            Title                     = src.Title,
            TitleEnglish              = src.TitleEnglish,
            Subtitle                  = src.Subtitle,
            Nickname                  = src.Nickname,
            Form                      = src.Form,
            Number                    = src.Number,
            MusicNumber               = src.MusicNumber,
            KeyTonality               = src.KeyTonality,
            KeyMode                   = src.KeyMode,
            PublicationYear           = src.PublicationYear,
            InstrumentationCategory   = src.InstrumentationCategory,
            NumberedSubpieces         = src.NumberedSubpieces,
            SubpiecesStart            = src.SubpiecesStart,
            Notes                     = src.Notes,

            InstrumentationJson       = RawJson(src.Instrumentation),
            CompositionYearsJson      = RawJson(src.CompositionYears),
            TextAuthorJson            = RawJson(src.TextAuthor),
            RolesJson                 = RawJson(src.Roles),
            ContributingComposersJson = RawJson(src.ContributingComposers),
        };

        AddCatalogEntries(row.CatalogEntries, src.CatalogInfo);
        AddMarkers(row.Markers, src.Markers);
        SynthesizeLegacyAnchorMarkers(row.Markers, src.Tempos, src.FirstLine);
        AddComposerCredits(row.ComposerCredits, src.Composers);
        AddVariants(row.Variants, src.Variants);

        return row;
    }

    private void AddCatalogEntries(List<PieceCatalogEntryRow> target, List<CatalogInfo>? src)
    {
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
            target.Add(new PieceCatalogEntryRow
            {
                Position         = i,
                Catalog          = src[i].Catalog,
                CatalogNumber    = src[i].CatalogNumber,
                CatalogSubnumber = src[i].CatalogSubnumber,
            });
    }

    /// <summary>
    /// Maps the input model's <see cref="MusicalMarker"/> list onto persisted
    /// <see cref="PieceMarkerRow"/>s, recursing through nested sub-markers.
    /// Stable IDs from the input (already-seeded data, JSON re-import, etc.)
    /// are <em>not</em> propagated here — fresh seed runs always allocate new
    /// row IDs from SQLite. Round-trip ID preservation happens in
    /// <c>SqliteCanonDataService.Save*</c>.
    /// <para>
    /// Each (model, row) pair is registered in <see cref="_markerRowByModel"/>
    /// so <c>ResolvePieceRefs</c> can translate <see cref="TrackPieceRef.StartMarker"/>
    /// / <see cref="TrackPieceRef.EndMarker"/> references to the persisted row's
    /// id once SaveChanges has populated it.
    /// </para>
    /// </summary>
    private void AddMarkers(List<PieceMarkerRow> target, List<MusicalMarker>? src)
    {
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
        {
            var markerRow = new PieceMarkerRow
            {
                Position    = i,
                Kind        = src[i].Kind,
                Value       = src[i].Value,
                BarNumber   = src[i].BarNumber,
                Number      = src[i].Number,
                Description = src[i].Description,
            };
            AddSubMarkers(markerRow.SubMarkers, src[i].SubMarkers);
            target.Add(markerRow);
            _markerRowByModel[src[i]] = markerRow;
        }
    }

    private void AddSubMarkers(List<PieceMarkerRow> target, List<MusicalMarker>? src)
    {
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
        {
            var markerRow = new PieceMarkerRow
            {
                Position    = i,
                Kind        = src[i].Kind,
                Value       = src[i].Value,
                BarNumber   = src[i].BarNumber,
                Number      = src[i].Number,
                Description = src[i].Description,
            };
            AddSubMarkers(markerRow.SubMarkers, src[i].SubMarkers);
            target.Add(markerRow);
            _markerRowByModel[src[i]] = markerRow;
        }
    }

    /// <summary>
    /// Stage-1 migration: synthesises kind=Tempo / kind=FirstLine markers from
    /// the legacy <see cref="CanonPiece.Tempos"/> and <see cref="CanonPiece.FirstLine"/>
    /// fields when they aren't already represented in <paramref name="target"/>.
    /// Idempotent under reseed: if the user has already authored markers with
    /// the same kind+value, the legacy entry is skipped — preventing duplicates
    /// on subsequent runs after the editor has touched the data.
    /// <para>
    /// Once Stage 3 retires the legacy <c>piece_tempos</c> / <c>pieces.first_line</c>
    /// storage, this helper goes away — by then every legacy entry will already
    /// live in <c>piece_markers</c> with a stable id, and the JSON read path
    /// will fold the legacy keys directly into <see cref="CanonPiece.Markers"/>.
    /// </para>
    /// </summary>
    private static void SynthesizeLegacyAnchorMarkers(
        List<PieceMarkerRow> target,
        List<TempoInfo>? tempos,
        string? firstLine)
    {
        // Continue numbering after any markers AddMarkers already appended,
        // so positions stay sequential and explicit-marker order wins.
        int position = target.Count;

        if (tempos is { Count: > 0 })
        {
            foreach (var t in tempos)
            {
                if (string.IsNullOrEmpty(t.Description)) continue;

                // Dedup on (kind, value, number) — not just (kind, value) —
                // so a movement that returns to the same tempo at different
                // positions ("Tempo I" appearing as #1 and #5) keeps both
                // anchorable. Drops only when an existing marker is clearly
                // the same entry (e.g. a re-seed picking up its own export's
                // synthesized markers).
                int? num = t.Number == 0 ? null : t.Number;
                if (target.Any(m => m.Kind == MarkerKind.Tempo &&
                    string.Equals(m.Value, t.Description, StringComparison.OrdinalIgnoreCase) &&
                    m.Number == num))
                    continue;

                target.Add(new PieceMarkerRow
                {
                    Position = position++,
                    Kind     = MarkerKind.Tempo,
                    Value    = t.Description,
                    Number   = num,
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            if (!target.Any(m => m.Kind == MarkerKind.FirstLine &&
                string.Equals(m.Value, firstLine, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(new PieceMarkerRow
                {
                    Position = position++,
                    Kind     = MarkerKind.FirstLine,
                    Value    = firstLine.Trim(),
                });
            }
        }
    }

    private void AddComposerCredits(List<PieceComposerCreditRow> target, List<ComposerCredit>? src)
    {
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
        {
            long? composerId = null;
            if (!string.IsNullOrWhiteSpace(src[i].Name) &&
                _composerIdByName.TryGetValue(src[i].Name, out var resolvedId))
                composerId = resolvedId;

            target.Add(new PieceComposerCreditRow
            {
                Position   = i,
                ComposerId = composerId,
                Name       = src[i].Name,
                Role       = src[i].Role,
            });
        }
    }

    private void AddVariants(List<PieceVariantRow> target, List<VariantInfo>? src)
    {
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
            target.Add(new PieceVariantRow
            {
                Position        = i,
                Description     = src[i].Description,
                LongDescription = src[i].LongDescription,
            });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Albums
    // ─────────────────────────────────────────────────────────────────────────
    private void SeedAlbums(List<CanonAlbum> albums, SeedResult report)
    {
        foreach (var album in albums)
        {
            var albumRow = new AlbumRow
            {
                Title           = album.Title,
                Subtitle        = album.Subtitle,
                Label           = album.Label,
                CatalogueNumber = album.CatalogueNumber,
                Barcode         = album.Barcode,
                SparsCode       = album.SparsCode,
                IsStereo        = album.IsStereo,
                Notes           = album.Notes,
                // Rework H42: preserve JSON IsProvisional. See SeedComposers.
                IsProvisional   = album.IsProvisional,
            };

            // ── Volumes ──────────────────────────────────────────────────────
            var volumeByNumber = new Dictionary<int, AlbumVolumeRow>();
            if (album.Volumes is { Count: > 0 })
            {
                foreach (var v in album.Volumes)
                {
                    var vr = new AlbumVolumeRow
                    {
                        Number   = v.Number,
                        Title    = v.Title,
                        Subtitle = v.Subtitle,
                    };
                    albumRow.Volumes.Add(vr);
                    volumeByNumber[v.Number] = vr;
                }
            }

            // ── Sessions ─────────────────────────────────────────────────────
            // H21: tracks reference sessions by stable Id (RecordingSession.Id ↔
            // AlbumTrack.SessionId). Build a by-Id map for the track wire-up
            // below; a per-position map remains as a fallback for pre-H21 JSON
            // snapshots that still carry the legacy positional SessionIndex.
            var sessionByIndex = new Dictionary<int, AlbumSessionRow>();
            var sessionById    = new Dictionary<long, AlbumSessionRow>();
            if (album.Sessions is { Count: > 0 })
            {
                for (int i = 0; i < album.Sessions.Count; i++)
                {
                    var s = album.Sessions[i];
                    var sr = new AlbumSessionRow
                    {
                        Position      = i,
                        Dates         = s.Dates,
                        Venue         = s.Venue,
                        City          = s.City,
                        Country       = s.Country,
                        EngineersJson = SerializeStringList(s.Engineers),
                        ProducersJson = SerializeStringList(s.Producers),
                    };
                    albumRow.Sessions.Add(sr);
                    sessionByIndex[i] = sr;
                    if (s.Id != 0) sessionById[s.Id] = sr;
                }
            }

            // ── Album-level performers ───────────────────────────────────────
            if (album.Performers is { Count: > 0 })
                for (int i = 0; i < album.Performers.Count; i++)
                    albumRow.Performers.Add(MapPerformer(album.Performers[i], position: i));

            // ── Discs and tracks ─────────────────────────────────────────────
            foreach (var disc in album.Discs)
            {
                var discRow = new AlbumDiscRow
                {
                    DiscNumber = disc.DiscNumber,
                    Title      = disc.Title,
                };
                if (disc.VolumeNumber is int vn && volumeByNumber.TryGetValue(vn, out var volRow))
                    discRow.Volume = volRow;

                foreach (var track in disc.Tracks)
                {
                    var trackRow = new AlbumTrackRow
                    {
                        TrackNumber   = track.TrackNumber,
                        Duration      = track.Duration,
                        Description   = track.Description,
                        SparsCode     = track.SparsCode,
                        IsStereo      = track.IsStereo,
                        // Rework H42: preserve JSON IsProvisional. See SeedComposers.
                        IsProvisional = track.IsProvisional,
                    };
                    // H21: prefer the stable SessionId; fall back to positional
                    // SessionIndex only for pre-H21 JSON snapshots.
                    if (track.SessionId is long sid && sessionById.TryGetValue(sid, out var sessRowById))
                        trackRow.Session = sessRowById;
                    else if (track.SessionIndex is int si && sessionByIndex.TryGetValue(si, out var sessRow))
                        trackRow.Session = sessRow;

                    // Track-level performers need both album_id (required FK) and track_id.
                    // Attach each row to both parent collections so EF sets both FKs.
                    if (track.Performers is { Count: > 0 })
                        for (int i = 0; i < track.Performers.Count; i++)
                        {
                            var pRow = MapPerformer(track.Performers[i], position: i);
                            pRow.Track = trackRow;
                            albumRow.Performers.Add(pRow);
                        }

                    // ── Piece refs ──────────────────────────────────────────
                    if (track.PieceRefs is { Count: > 0 })
                        ResolvePieceRefs(album, disc, track, trackRow, report);

                    discRow.Tracks.Add(trackRow);
                }

                albumRow.Discs.Add(discRow);
            }

            _db.Albums.Add(albumRow);
            report.AlbumCount++;
        }
    }

    private void ResolvePieceRefs(
        CanonAlbum album, AlbumDisc disc, AlbumTrack track,
        AlbumTrackRow trackRow, SeedResult report)
    {
        int position = 0;
        foreach (var pr in track.PieceRefs!)
        {
            var resolved = Resolve(pr, out var failureReason);
            if (resolved is null)
            {
                report.UnresolvedRefs.Add(new UnresolvedRef(
                    AlbumTitle:   album.Title ?? "(untitled album)",
                    DiscNumber:   disc.DiscNumber,
                    TrackNumber:  track.TrackNumber,
                    Composer:     pr.Composer   ?? "",
                    PieceTitle:   pr.PieceTitle ?? "",
                    SubpiecePath: pr.SubpiecePath,
                    VersionDesc:  pr.VersionDescription,
                    DisplaySummary: pr.DisplaySummary,
                    FailureReason: failureReason));
                continue;
            }

            // Range refs: resolve the end path the same way (synthesise a
            // probe with EndSubpiecePath as the start path so the resolver's
            // existing leaf-walk logic does the work). When the end path
            // doesn't resolve we degrade to a single-segment ref rather than
            // dropping the whole entry — a partial range is more useful than
            // no ref at all.
            PieceRow? endPieceRow = null;
            if (pr.EndSubpiecePath is { Count: > 0 })
            {
                var endProbe = new TrackPieceRef
                {
                    Composer           = pr.Composer ?? "",
                    PieceTitle         = pr.PieceTitle ?? "",
                    VersionDescription = pr.VersionDescription,
                    SubpiecePath       = pr.EndSubpiecePath.ToList(),
                };
                var endResolved = _resolver.TryResolve(endProbe);
                if (endResolved is not null &&
                    _pieceRowByModel.TryGetValue(endResolved.Value.Piece, out var endRow))
                {
                    endPieceRow = endRow;
                }
            }

            // Marker anchors: walk the resolved leaf's marker rows looking
            // for a match. PieceMarkerRow.Id is already populated here — the
            // outer SaveChanges between SeedPieces and SeedAlbums sees to it —
            // so id-based lookup works whenever the input ref carries an id.
            var startMarkerRow = ResolveMarker(resolved.Value.piece, pr.StartMarker);
            var endMarkerRow   = endPieceRow is not null
                ? ResolveMarker(endPieceRow, pr.EndMarker)
                : ResolveMarker(resolved.Value.piece, pr.EndMarker);

            trackRow.PieceRefs.Add(new AlbumTrackPieceRefRow
            {
                Position      = position++,
                Piece         = resolved.Value.piece,
                Version       = resolved.Value.version,
                EndPiece      = endPieceRow,
                StartMarker   = startMarkerRow,
                EndMarker     = endMarkerRow,
                DisplayLabel  = pr.DisplayLabel,
            });
        }

        // If the track had refs but none resolved, preserve a trace in the description.
        if (track.PieceRefs!.Count > 0 && trackRow.PieceRefs.Count == 0 &&
            string.IsNullOrWhiteSpace(trackRow.Description))
        {
            trackRow.Description = track.PieceRefs[0].DisplaySummary;
        }
    }

    /// <summary>
    /// Resolves a <see cref="MarkerReference"/> to a <see cref="PieceMarkerRow"/>
    /// belonging to <paramref name="leaf"/>. Lookup order: id, then kind+value,
    /// then kind+bar-number. Returns <c>null</c> when no plausible match exists —
    /// the caller treats that as "no marker anchor" rather than failing the ref.
    /// </summary>
    private static PieceMarkerRow? ResolveMarker(PieceRow leaf, MarkerReference? marker)
    {
        if (marker is null) return null;
        if (leaf.Markers is null or { Count: 0 }) return null;

        if (marker.Id != 0)
        {
            var byId = leaf.Markers.FirstOrDefault(m => m.Id == marker.Id);
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrEmpty(marker.Value))
        {
            var byValue = leaf.Markers.FirstOrDefault(m =>
                m.Kind == marker.Kind &&
                string.Equals(m.Value, marker.Value, StringComparison.OrdinalIgnoreCase));
            if (byValue is not null) return byValue;
        }

        if (marker.BarNumber is { } bn)
        {
            var byBar = leaf.Markers.FirstOrDefault(m =>
                m.Kind == marker.Kind && m.BarNumber == bn);
            if (byBar is not null) return byBar;
        }

        return null;
    }

    /// <summary>
    /// Resolves a track piece ref to (PieceRow, PieceVersionRow?) or null if it
    /// can't be matched against the indexed piece tree. Delegates to
    /// <see cref="PieceReferenceIndex.TryResolve"/> so the seeder applies the
    /// same title-variant / set-recursion / loose-subpiece-match rules as the
    /// runtime badge pipeline.
    /// </summary>
    private (PieceRow piece, PieceVersionRow? version)? Resolve(TrackPieceRef pr, out string failureReason)
    {
        failureReason = "";
        var resolved = _resolver.TryResolve(pr);
        if (resolved is null)
        {
            // Diagnose which stage failed so the seed report can bucket the misses.
            failureReason = ClassifyResolveFailure(pr);
            return null;
        }
        var (piece, version) = resolved.Value;
        if (!_pieceRowByModel.TryGetValue(piece, out var pieceRow))
        {
            failureReason = "piece-row-missing";
            return null;
        }
        PieceVersionRow? versionRow = null;
        if (version is not null)
            _versionRowByModel.TryGetValue(version, out versionRow);
        return (pieceRow, versionRow);
    }

    private string ClassifyResolveFailure(TrackPieceRef pr)
    {
        // Walk the same lookups the resolver uses, but only far enough to identify
        // the earliest mismatch — useful for bucketing the unresolved-ref report.
        // This intentionally re-reads from _resolver's public surface rather than
        // duplicating its internal dictionaries.
        var composer = (pr.Composer ?? "").Trim();
        if (string.IsNullOrEmpty(composer)) return "composer-missing";
        // Titles map: we can't see it directly, but we can probe by trying the
        // ref with an empty SubpiecePath — if that resolves, then the failure is
        // in the subpiece walk.
        var probe = new TrackPieceRef
        {
            Composer           = pr.Composer ?? "",
            PieceTitle         = pr.PieceTitle ?? "",
            VersionDescription = pr.VersionDescription,
            // SubpiecePath intentionally omitted
        };
        if (_resolver.TryResolve(probe) is not null)
            return !string.IsNullOrWhiteSpace(pr.VersionDescription)
                ? "version-subpiece-path-not-found"
                : "subpiece-path-not-found";
        if (!string.IsNullOrWhiteSpace(pr.VersionDescription))
        {
            probe.VersionDescription = null;
            if (_resolver.TryResolve(probe) is not null)
                return "version-description-not-found";
        }
        return "piece-title-not-found";
    }

    private AlbumPerformerRow MapPerformer(AlbumPerformer src, int position) =>
        new()
        {
            Position    = position,
            DisplayName = src.Name,
            Role        = src.Role,
            Instrument  = src.Instrument,
            // PersonId / EnsembleId start null — the People / Ensembles tables are empty
            // on initial migration; linking is done later via the editor.
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Returns the raw JSON text for a nullable <see cref="JsonElement"/>, or null.
    /// </summary>
    private static string? RawJson(JsonElement? element) =>
        element.HasValue ? element.Value.GetRawText() : null;

    private static string? SerializeStringList(List<string>? list) =>
        list is { Count: > 0 } ? JsonSerializer.Serialize(list, _writeOptions) : null;
}

// ─────────────────────────────────────────────────────────────────────────────
// Result types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Describes a single <see cref="TrackPieceRef"/> that couldn't be resolved
/// to a piece in the indexed canon.
/// </summary>
public record UnresolvedRef(
    string AlbumTitle,
    int DiscNumber,
    int TrackNumber,
    string Composer,
    string PieceTitle,
    IReadOnlyList<string>? SubpiecePath,
    string? VersionDesc,
    string DisplaySummary,
    string FailureReason);

/// <summary>
/// Counts and issue lists from a seed run.
/// </summary>
public class SeedResult
{
    public int AlbumCount { get; set; }
    public int PieceCount { get; set; }
    public int PickListValueCount { get; set; }

    public List<string> UnknownComposerReferences { get; } = [];
    public List<UnresolvedRef> UnresolvedRefs { get; } = [];

    /// <summary>
    /// H41: any same-composer + same-title-key collisions detected while
    /// building the resolver index. The kept piece won the slot; the
    /// dropped piece is unreachable via that key. Sourced from
    /// <see cref="PieceReferenceIndex.Collisions"/> after
    /// <see cref="PieceReferenceIndex.BuildResolver"/> completes.
    /// </summary>
    public List<Services.TitleCollision> IndexCollisions { get; } = [];

    // Internal: composer-name → row linkage used to build the lookup index
    // after SaveChanges populates Ids.
    internal List<(CanonComposer model, ComposerRow row)> ComposerRows { get; } = [];

    public int ComposerCount => ComposerRows.Count;

    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Composers:          {ComposerCount,6}",
                $"Pieces (all):       {PieceCount,6}",
                $"Pick-list values:   {PickListValueCount,6}",
                $"Albums:             {AlbumCount,6}",
                $"Unresolved refs:    {UnresolvedRefs.Count,6}",
                $"Index collisions:   {IndexCollisions.Count,6}",
                $"Unknown composer references on pieces: {UnknownComposerReferences.Count,6}",
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}
