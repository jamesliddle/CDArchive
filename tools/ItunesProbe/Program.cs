using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CDArchive.Core.Data;
using CDArchive.Core;

// ── End-to-end save probe: insert a piece with IsProvisional=true and read it back ──
{
    var services = new ServiceCollection();
    services.AddCoreServices();
    using var sp = services.BuildServiceProvider();
    var svc = (SqliteCanonDataService)sp.GetRequiredService<ICanonDataService>();

    // Load current canon, append a new test piece, save, query DB for the inserted row.
    var composers = await svc.LoadComposersAsync();
    var mozart = composers.FirstOrDefault(c => c.Name.StartsWith("Mozart")) ?? composers.First();

    var existing = await svc.LoadPiecesAsync();
    var fresh = new CDArchive.Core.Models.CanonPiece
    {
        Composer      = mozart.Name,
        Title         = "TEST PIECE for save probe " + Guid.NewGuid().ToString("N")[..8],
        IsProvisional = true,
    };
    existing.Add(fresh);
    await svc.SavePiecesAsync(existing);

    // Read back via raw SQL to bypass any model-layer mapping.
    var dbPath2 = @"C:\Users\james\source\repos\CDArchive\data\ClassicalCanon.db";
    using var conn = new SqliteConnection($"Data Source={dbPath2}");
    conn.Open();
    using var query = conn.CreateCommand();
    query.CommandText = "SELECT id, title, is_provisional FROM pieces WHERE title = $t";
    query.Parameters.AddWithValue("$t", fresh.Title);
    using var rdr = query.ExecuteReader();
    while (rdr.Read())
        Console.WriteLine($"Inserted: id={rdr[0]} title='{rdr[1]}' is_provisional={rdr[2]} (expected 1)");

    // Clean up: delete the test piece.
    using var cleanup = conn.CreateCommand();
    cleanup.CommandText = "DELETE FROM pieces WHERE title = $t";
    cleanup.Parameters.AddWithValue("$t", fresh.Title);
    var deleted = cleanup.ExecuteNonQuery();
    Console.WriteLine($"Cleaned up {deleted} test row.");
    Console.WriteLine();

    // Dry-run the iTunes importer against the current canon. We DON'T persist —
    // just probe whether the new resolver-based matching finds 979/1004 and their
    // subpieces for the Serkin tracks, without creating duplicates.
    {
        var fakeServices = new ServiceCollection();
        fakeServices.AddCoreServices();
        using var fakeSp = fakeServices.BuildServiceProvider();
        var svc2 = fakeSp.GetRequiredService<ICanonDataService>();
        var itunesRef = fakeSp.GetRequiredService<ItunesLibraryReference>();

        var allComposers = (await svc2.LoadComposersAsync()).ToList();
        var allPieces    = (await svc2.LoadPiecesAsync()).ToList();
        var allTracks = await itunesRef.LoadAllTracksAsync();
        var taylorTracks = allTracks
            .Where(t => t.Album == "Taylor Through the Looking Glass Schwarz")
            .ToList();
        Console.WriteLine($"Found {taylorTracks.Count} Taylor tracks in iTunes.");

        var before = allPieces.Count;
        var result = ItunesImporter.Import(taylorTracks, allComposers, allPieces);
        Console.WriteLine("Taylor album-level performers after dry-run import:");
        foreach (var album in result.NewAlbums)
            foreach (var p in album.Performers ?? new())
                Console.WriteLine($"  '{p.Name}' role='{p.Role}'");
        Console.WriteLine("Taylor per-track performer overrides (only tracks that differ):");
        foreach (var album in result.NewAlbums)
            foreach (var disc in album.Discs)
                foreach (var trk in disc.Tracks)
                    if (trk.Performers is { Count: > 0 })
                    {
                        var names = string.Join(", ", trk.Performers.Select(p => p.Name));
                        Console.WriteLine($"  trk{trk.TrackNumber}: {names}");
                    }

        var serkinTracks = allTracks
            .Where(t => t.Album == "Mozart Piano Concertos 21 23 Serkin")
            .ToList();
        Console.WriteLine($"Found {serkinTracks.Count} Serkin tracks in iTunes.");

        result = ItunesImporter.Import(serkinTracks, allComposers, allPieces);
        var after = allPieces.Count;

        Console.WriteLine($"Import (in-memory only):");
        Console.WriteLine($"  New top-level pieces created: {result.NewPieces} (delta {after - before})");
        Console.WriteLine($"  New subpieces created:        {result.NewSubpieces}");
        Console.WriteLine($"  Tracks imported:              {result.TracksImported}");

        // Show which piece instance each track ref resolved to — by reference,
        // not by title — so we can confirm the structured-form pieces won.
        var idByPiece = allPieces
            .SelectMany(p => Walk(p))
            .Where(p => allPieces.Contains(p) || true) // include subpieces too
            .ToList();

        static IEnumerable<CDArchive.Core.Models.CanonPiece> Walk(CDArchive.Core.Models.CanonPiece p)
        {
            yield return p;
            if (p.Subpieces is { } subs)
                foreach (var s in subs)
                    foreach (var d in Walk(s))
                        yield return d;
        }

        foreach (var album in result.NewAlbums)
            foreach (var disc in album.Discs)
                foreach (var track in disc.Tracks)
                {
                    Console.WriteLine($"  trk{track.TrackNumber}:");
                    foreach (var pr in track.PieceRefs ?? new())
                    {
                        Console.WriteLine($"    title='{pr.PieceTitle}' path=[{string.Join(",", pr.SubpiecePath ?? new())}]");
                    }
                }

        // What do 979/1004's subpieces look like now? My EnsureSubpiecePath should
        // have back-filled titles ("Allegro", "Andante", etc.) on them.
        var p979  = allPieces.FirstOrDefault(p => p.Title == "" &&
                                                  p.Form == "Piano Concerto" &&
                                                  p.Number == 21);
        var p1004 = allPieces.FirstOrDefault(p => p.Title == "" &&
                                                  p.Form == "Piano Concerto" &&
                                                  p.Number == 23);
        if (p979 is not null)
        {
            Console.WriteLine($"  979 (form='Piano Concerto', num=21) subpieces after import (in memory):");
            foreach (var ssp in p979.Subpieces ?? new())
                Console.WriteLine($"    title='{ssp.Title}' num={ssp.Number} musicNum='{ssp.MusicNumber}'");
        }
        if (p1004 is not null)
        {
            Console.WriteLine($"  1004 (form='Piano Concerto', num=23) subpieces after import (in memory):");
            foreach (var ssp2 in p1004.Subpieces ?? new())
                Console.WriteLine($"    title='{ssp2.Title}' num={ssp2.Number} musicNum='{ssp2.MusicNumber}'");
        }
    }
    Console.WriteLine();

    using var nonProvCheck = conn.CreateCommand();
    nonProvCheck.CommandText = @"
        SELECT 'pieces',  COUNT(*) FROM pieces       WHERE is_provisional = 0
        UNION ALL SELECT 'composers', COUNT(*) FROM composers WHERE is_provisional = 0
        UNION ALL SELECT 'albums',    COUNT(*) FROM albums    WHERE is_provisional = 0
        UNION ALL SELECT 'tracks',    COUNT(*) FROM album_tracks WHERE is_provisional = 0";
    using var rNon = nonProvCheck.ExecuteReader();
    Console.WriteLine("Current rows with is_provisional = 0:");
    while (rNon.Read()) Console.WriteLine($"  {rNon[0],-10} {rNon[1]}");
    Console.WriteLine();

    // What do the structured-form pieces 979 / 1004 and their movement subpieces look like?
    using var dump = conn.CreateCommand();
    dump.CommandText = @"
        SELECT p.id, p.title, p.form, p.number, p.music_number, p.key_tonality, p.key_mode,
               p.parent_piece_id, p.position, p.is_provisional
        FROM pieces p
        WHERE p.id IN (979, 1004)
           OR p.parent_piece_id IN (979, 1004)
        ORDER BY p.parent_piece_id NULLS FIRST, p.position";
    using var rD = dump.ExecuteReader();
    Console.WriteLine("979 / 1004 + their movement subpieces:");
    while (rD.Read())
        Console.WriteLine(
            $"  id={rD[0]} title='{rD[1]}' form='{rD[2]}' num={rD[3]} musicNum='{rD[4]}'"
            + $" key={rD[5]}/{rD[6]} parent={rD[7]} pos={rD[8]} prov={rD[9]}");
    Console.WriteLine();

    // Sample of those piece rows so we can confirm they're the import-orphaned ones.
    using var sample = conn.CreateCommand();
    sample.CommandText = @"
        SELECT p.id, p.title, p.parent_piece_id, c.name
        FROM pieces p JOIN composers c ON c.id = p.composer_id
        WHERE p.is_provisional = 0
        ORDER BY p.id DESC LIMIT 15";
    using var rSam = sample.ExecuteReader();
    Console.WriteLine("Most-recent prov=0 pieces:");
    while (rSam.Read())
        Console.WriteLine($"  id={rSam[0]} title='{rSam[1]}' parent={rSam[2]} composer='{rSam[3]}'");
    Console.WriteLine();

    // Show ALL Mozart top-level pieces so we can spot 979/1004/5715/5716 etc.
    using var allM = conn.CreateCommand();
    allM.CommandText = @"
        SELECT p.id, p.title, p.form, p.number, p.is_provisional
        FROM pieces p JOIN composers c ON c.id = p.composer_id
        WHERE c.name LIKE 'Mozart%' AND p.parent_piece_id IS NULL
          AND (p.title LIKE 'Piano Concerto%' OR p.form = 'Piano Concerto')
        ORDER BY p.id";
    using var rdr2 = allM.ExecuteReader();
    Console.WriteLine("Mozart top-level Piano Concerto pieces (after save probe):");
    while (rdr2.Read())
        Console.WriteLine($"  id={rdr2[0]} title='{rdr2[1]}' form='{rdr2[2]}' num={rdr2[3]} prov={rdr2[4]}");
    Console.WriteLine();
}

// ── Parse a real Serkin album track name to see what the parser yields ──
{
    var names = new[]
    {
        "Piano Concerto #21 in C, KV 467 - 1. Allegro",
        "Piano Concerto #23 in A, KV 488 - 2. Adagio",
    };
    foreach (var n in names)
    {
        var parsed = ItunesImportInference.ParseTrackName(n);
        Console.WriteLine($"Name: '{n}'");
        Console.WriteLine($"  Piece: '{parsed.PieceTitle}'");
        Console.WriteLine($"  Bytes: {string.Join(",", System.Text.Encoding.UTF8.GetBytes(n).Take(80))}");
        foreach (var r in parsed.SubpieceRefs)
            Console.WriteLine($"  Sub: num='{r.MusicNumber}' path=[{string.Join(",", r.Path)}]");
    }
    Console.WriteLine();
}

// ── Test resolver tie-break with two synthetic pieces sharing a title ──
{
    var composer = "Mozart, Wolfgang Amadeus";
    var approved = new CDArchive.Core.Models.CanonPiece
    {
        Composer      = composer,
        Title         = "Piano Concerto #21 in C, KV 467",
        IsProvisional = false,
        Subpieces     = new List<CDArchive.Core.Models.CanonPiece>
        {
            new() { Composer = composer, Title = "Allegro",  IsProvisional = false },
            new() { Composer = composer, Title = "Andante",  IsProvisional = false },
            new() { Composer = composer, Title = "Allegro vivace assai", IsProvisional = false },
        },
    };
    var provisionalDup = new CDArchive.Core.Models.CanonPiece
    {
        Composer      = composer,
        Title         = "", // structured-form piece — DisplayTitle is computed
        Form          = "Piano Concerto",
        Number        = 21,
        KeyTonality   = "C",
        KeyMode       = "major",
        IsProvisional = true,
        CatalogInfo   = new List<CDArchive.Core.Models.CatalogInfo>
        {
            new() { Catalog = "KV", CatalogNumber = "467" }
        },
        Subpieces     = new List<CDArchive.Core.Models.CanonPiece>
        {
            new() { Composer = composer, Title = "", IsProvisional = true },
        },
    };

    Console.WriteLine($"Approved DisplayTitle:    '{approved.DisplayTitle}'");
    Console.WriteLine($"Provisional DisplayTitle: '{provisionalDup.DisplayTitle}'");

    var pieces = new List<CDArchive.Core.Models.CanonPiece> { provisionalDup, approved };
    // ^ deliberately provisional first to simulate ID-order load
    var idx = new PieceReferenceIndex();
    idx.BuildResolver(pieces);

    var probe = new CDArchive.Core.Models.TrackPieceRef
    {
        Composer     = composer,
        PieceTitle   = "Piano Concerto #21 in C, KV 467",
        SubpiecePath = new List<string> { "Allegro" },
    };
    var r = idx.TryResolve(probe);
    var matchedTop = ReferenceEquals(r?.Piece, approved.Subpieces[0]) ? "APPROVED tree (5707-ish)"
                   : ReferenceEquals(r?.Piece, provisionalDup.Subpieces[0]) ? "PROVISIONAL tree (979-ish)"
                   : "neither?";
    Console.WriteLine($"Resolved to: {matchedTop} — leaf title='{r?.Piece.Title}'");
    Console.WriteLine();
}

// ── Find Serkin tracks in iTunes and dump raw Name field bytes ──
{
    var itunes2 = new ItunesLibraryReference();
    var all = await itunes2.LoadAllTracksAsync();
    var serkin = all.Where(t => t.Album == "Mozart Piano Concertos 21 23 Serkin").ToList();
    Console.WriteLine($"Serkin iTunes tracks: {serkin.Count}");
    foreach (var t in serkin)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(t.Name);
        Console.WriteLine($"  trk={t.TrackNumber} name='{t.Name}'");
        Console.WriteLine($"    bytes (first 80): {string.Join(",", bytes.Take(80))}");
        var parsed = ItunesImportInference.ParseTrackName(t.Name);
        Console.WriteLine($"    parsed: piece='{parsed.PieceTitle}', refs={parsed.SubpieceRefs.Count}");
        foreach (var r in parsed.SubpieceRefs)
            Console.WriteLine($"      sub: num='{r.MusicNumber}' path=[{string.Join(",", r.Path)}]");
    }
    Console.WriteLine();
}

// ── Show the actual stored titles for Mozart Piano Concerto #21/#23 ──
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    string? dbPath = null;
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "data", "ClassicalCanon.db");
        if (File.Exists(candidate)) { dbPath = candidate; break; }
        dir = dir.Parent;
    }
    if (dbPath is not null)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            -- Find the Serkin album and its tracks + piece refs
            SELECT a.id, a.title, a.is_provisional FROM albums a WHERE a.title LIKE '%Serkin%' OR a.title LIKE '%21 23%'";
        using (var reader = cmd.ExecuteReader())
        {
            Console.WriteLine("Albums matching Serkin/21 23:");
            while (reader.Read())
                Console.WriteLine($"  id={reader.GetInt64(0)} title='{reader[1]}' prov={reader.GetBoolean(2)}");
        }
        Console.WriteLine();

        // Now dump the track piece refs for the Serkin album
        using var cmd2 = conn.CreateCommand();
        using (var dumpPiece = conn.CreateCommand())
        {
            dumpPiece.CommandText = @"
                SELECT id, title, subtitle, form, number, music_number, key_tonality, key_mode, is_provisional, parent_piece_id
                FROM pieces WHERE id IN (979, 1004, 5707, 5708)";
            using var r = dumpPiece.ExecuteReader();
            Console.WriteLine("Pieces 979, 1004, 5707, 5708:");
            while (r.Read())
                Console.WriteLine($"  id={r[0]} title='{r[1]}' subtitle='{r[2]}' form='{r[3]}' num={r[4]} musicNum='{r[5]}' key={r[6]}/{r[7]} prov={r[8]} parent={r[9]}");
            Console.WriteLine();
        }
        using (var cols = conn.CreateCommand())
        {
            cols.CommandText = "PRAGMA table_info(pieces)";
            using var r = cols.ExecuteReader();
            Console.WriteLine("pieces columns:");
            while (r.Read())
                if (r.GetString(1).Contains("provisional", StringComparison.OrdinalIgnoreCase)
                    || r.GetString(1) == "title" || r.GetString(1) == "id")
                    Console.WriteLine($"  {r[1]} type={r[2]} notnull={r[3]} dflt={r[4]}");
            Console.WriteLine();
        }
        using (var pieceDump = conn.CreateCommand())
        {
            pieceDump.CommandText = @"
                SELECT p.id, p.title, p.is_provisional, p.parent_piece_id,
                       (SELECT COUNT(*) FROM album_track_piece_refs pr WHERE pr.piece_id = p.id) AS refs
                FROM pieces p
                JOIN composers c ON c.id = p.composer_id
                WHERE c.name LIKE 'Mozart%' AND p.title LIKE 'Piano Concerto #2%KV%'
                ORDER BY p.title, p.id";
            using var r = pieceDump.ExecuteReader();
            Console.WriteLine("Mozart Piano Concerto #2* (KV) pieces:");
            while (r.Read())
                Console.WriteLine($"  id={r[0]} title='{r[1]}' prov={r[2]} parent={r[3]} albumRefs={r[4]}");
            Console.WriteLine();
        }
        // Performance + correctness check for the "Hide already imported" filter.
    {
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var fakeSvc = new ServiceCollection();
        fakeSvc.AddCoreServices();
        using var sp4 = fakeSvc.BuildServiceProvider();
        var svc4 = sp4.GetRequiredService<ICanonDataService>();
        var canonAlbums = await svc4.LoadAlbumsAsync();
        var importedKeys = new HashSet<(string, int, int)>();
        int totalCanonTracks = 0;
        foreach (var a in canonAlbums)
        {
            var t = (a.Title ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            foreach (var d in a.Discs)
                foreach (var trk in d.Tracks)
                {
                    importedKeys.Add((t, d.DiscNumber, trk.TrackNumber));
                    totalCanonTracks++;
                }
        }
        var buildMs = sw2.Elapsed.TotalMilliseconds;

        sw2.Restart();
        var itunesRef2 = sp4.GetRequiredService<ItunesLibraryReference>();
        var allItunes = await itunesRef2.LoadAllTracksAsync();
        int hidden = 0;
        foreach (var t in allItunes)
        {
            if (string.IsNullOrWhiteSpace(t.Album)) continue;
            var key = (t.Album.Trim().ToLowerInvariant(),
                       t.DiscNumber ?? 1, t.TrackNumber ?? 0);
            if (importedKeys.Contains(key)) hidden++;
        }
        var filterMs = sw2.Elapsed.TotalMilliseconds;
        Console.WriteLine(
            $"Hide-already-imported diagnostic:\n"
            + $"  canon: {canonAlbums.Count} albums, {totalCanonTracks} tracks; "
            + $"index built in {buildMs:F1}ms ({importedKeys.Count} keys)\n"
            + $"  iTunes: {allItunes.Count} tracks; "
            + $"filter pass in {filterMs:F1}ms\n"
            + $"  → would hide {hidden} tracks ({allItunes.Count - hidden} shown)");
        Console.WriteLine();
    }

    // End-to-end test: remove Deems Taylor from the loaded composers list and save.
    // Verify the row actually leaves the database.
    {
        var fakeSvc = new ServiceCollection();
        fakeSvc.AddCoreServices();
        using var sp2 = fakeSvc.BuildServiceProvider();
        var svc3 = sp2.GetRequiredService<ICanonDataService>();

        var allComposers = (await svc3.LoadComposersAsync()).ToList();
        var taylor = allComposers.FirstOrDefault(c => c.Name == "Taylor, Deems");
        if (taylor is not null)
        {
            allComposers.Remove(taylor);
            try
            {
                await svc3.SaveComposersAsync(allComposers);
                Console.WriteLine("SaveComposersAsync: succeeded after removing Taylor, Deems");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SaveComposersAsync threw: {ex.Message}");
            }
        }
    }

    using (var taylorComp = conn.CreateCommand())
    {
        taylorComp.CommandText = @"
            SELECT c.id, c.name, c.is_provisional,
                   (SELECT COUNT(*) FROM pieces p WHERE p.composer_id = c.id) AS owned_pieces
            FROM composers c
            WHERE c.name LIKE '%Taylor%' OR c.name LIKE 'Deems%' OR c.name LIKE 'Taylor,%'";
        using var r = taylorComp.ExecuteReader();
        Console.WriteLine("Composers matching 'Taylor':");
        while (r.Read())
            Console.WriteLine($"  id={r[0]} name='{r[1]}' prov={r[2]} owned_pieces={r[3]}");
        Console.WriteLine();
    }
    using (var taylor = conn.CreateCommand())
        {
            taylor.CommandText = @"
                SELECT a.id, a.title, a.is_provisional, t.track_number, pr.piece_id, p.title AS piece_title,
                       (SELECT title FROM pieces pp WHERE pp.id = p.parent_piece_id) AS parent_title
                FROM albums a
                LEFT JOIN album_discs d ON d.album_id = a.id
                LEFT JOIN album_tracks t ON t.disc_id = d.id
                LEFT JOIN album_track_piece_refs pr ON pr.track_id = t.id
                LEFT JOIN pieces p ON p.id = pr.piece_id
                WHERE a.title LIKE '%Looking Glass%' OR a.title LIKE '%Taylor%'
                ORDER BY a.id, t.track_number, pr.position";
            using var r = taylor.ExecuteReader();
            Console.WriteLine("Taylor / Looking Glass album rows:");
            while (r.Read())
                Console.WriteLine($"  album={r[0]} '{r[1]}' prov={r[2]} trk={r[3]} pieceId={r[4]} pieceTitle='{r[5]}' parent='{r[6]}'");
            Console.WriteLine();
        }
        using (var taylorPerf = conn.CreateCommand())
        {
            taylorPerf.CommandText = @"
                SELECT ap.name, ap.role, ap.track_id FROM album_performers ap
                JOIN albums a ON a.id = ap.album_id
                WHERE a.title LIKE '%Looking Glass%' OR a.title LIKE '%Taylor%'";
            using var r = taylorPerf.ExecuteReader();
            Console.WriteLine("Taylor album performers (album_performers rows):");
            int n = 0;
            while (r.Read()) { Console.WriteLine($"  name='{r[0]}' role='{r[1]}' track_id={r[2]}"); n++; }
            if (n == 0) Console.WriteLine("  (none)");
            Console.WriteLine();
        }
        using (var albumDump = conn.CreateCommand())
        {
            albumDump.CommandText = @"SELECT id, title, is_provisional FROM albums WHERE title LIKE '%Serkin%21%23%' OR title LIKE 'Mozart Piano Concertos 21%'";
            using var r = albumDump.ExecuteReader();
            Console.WriteLine("Serkin albums:");
            while (r.Read())
                Console.WriteLine($"  id={r[0]} title='{r[1]}' prov={r[2]}");
            Console.WriteLine();
        }
        using (var refCheck = conn.CreateCommand())
        {
            refCheck.CommandText = @"
                -- Count album refs that point at 5707/5708 OR any of their descendants.
                WITH RECURSIVE descendants(id) AS (
                    SELECT id FROM pieces WHERE id IN (5707, 5708)
                    UNION ALL
                    SELECT p.id FROM pieces p JOIN descendants d ON p.parent_piece_id = d.id
                )
                SELECT descendants.id, COUNT(pr.id) FROM descendants
                LEFT JOIN album_track_piece_refs pr ON pr.piece_id = descendants.id
                GROUP BY descendants.id";
            using var r = refCheck.ExecuteReader();
            Console.WriteLine("Album refs pointing at 5707/5708 and their descendants:");
            while (r.Read())
                Console.WriteLine($"  piece_id={r[0]} refs={r[1]}");
            Console.WriteLine();
        }
        cmd2.CommandText = @"
            SELECT t.track_number, t.description, pr.position, pr.piece_id, p.title, p.is_provisional, p.composer_id, c.name
            FROM albums a
            JOIN album_discs d ON d.album_id = a.id
            JOIN album_tracks t ON t.disc_id = d.id
            LEFT JOIN album_track_piece_refs pr ON pr.track_id = t.id
            LEFT JOIN pieces p ON p.id = pr.piece_id
            LEFT JOIN composers c ON c.id = p.composer_id
            WHERE a.id = 794
            ORDER BY d.disc_number, t.track_number, pr.position";
        using (var reader = cmd2.ExecuteReader())
        {
            Console.WriteLine("Serkin album tracks + piece refs:");
            while (reader.Read())
            {
                Console.WriteLine($"  trk={reader[0]} desc='{reader[1]}' pos={reader[2]} pieceId={reader[3]} title='{reader[4]}' prov={reader[5]} composer='{reader[7]}'");
            }
        }
        Console.WriteLine();
    }
}

var sw = System.Diagnostics.Stopwatch.StartNew();
var itunes = new ItunesLibraryReference();
var tracks = await itunes.LoadAllTracksAsync();
sw.Stop();

Console.WriteLine($"Loaded {tracks.Count} tracks in {sw.Elapsed.TotalSeconds:F2}s");

// Bucket by genre to confirm composition.
var genreBuckets = tracks
    .GroupBy(t => t.Genre ?? "(no genre)", StringComparer.OrdinalIgnoreCase)
    .OrderByDescending(g => g.Count())
    .Take(15);

Console.WriteLine();
Console.WriteLine("Top 15 genres:");
foreach (var g in genreBuckets)
    Console.WriteLine($"  {g.Count(),6}  {g.Key}");

// Real podcasts have Genre=Podcast OR location ending .xml / containing feed slugs.
// A music track in a folder called "Podcasts" doesn't count — it's still Music.
var realPodcastsLeft = tracks
    .Where(t =>
        string.Equals(t.Genre, "Podcast", StringComparison.OrdinalIgnoreCase) ||
        (t.Location ?? "").EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        (t.Location ?? "").Contains("?feed=", StringComparison.OrdinalIgnoreCase) ||
        (t.Location ?? "").Contains("/feed/podcast", StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine();
Console.WriteLine($"Real podcast survivors (Genre=Podcast or RSS feed location): {realPodcastsLeft.Count}");
foreach (var t in realPodcastsLeft.Take(10))
    Console.WriteLine($"  id={t.TrackId} '{t.Name}' [{t.Genre}] loc={t.Location}");
