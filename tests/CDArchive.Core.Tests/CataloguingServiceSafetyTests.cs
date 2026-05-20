using System.Text.Json;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Rework H25 + H26 + H27 regression tests — the CataloguingService
/// "safety sweep" bundled into one PR. Three orthogonal correctness /
/// undo-path fixes in the tag-write pipeline:
///
/// <list type="bullet">
///   <item>H25 — <see cref="CataloguingService.FindMp3Folders"/> yields
///     every disc folder (not just the first), so multi-disc albums
///     don't silently skip discs 2..N.</item>
///   <item>H26 — the composer cache keys on the (lastName, firstName)
///     tuple, so shared-surname composers (Bach family, Strauss family,
///     etc.) don't write the wrong birth/death years onto each other.</item>
///   <item>H27 — <see cref="CataloguingService.WriteFileTag"/> snapshots
///     the pre-write tag values to a <c>.tagbackup.json</c> sidecar
///     before mutating, and <see cref="CataloguingService.RestoreFromBackup"/>
///     gives the user an undo path.</item>
/// </list>
/// </summary>
public class CataloguingServiceSafetyTests : IDisposable
{
    private readonly string _tempDir;

    public CataloguingServiceSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "cdarchive-cataloguing-safety-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H25 — FindMp3Folders walks every disc
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindMp3Folders_SingleDisc_YieldsAlbumMp3WithNullDiscNumber()
    {
        var album = Path.Combine(_tempDir, "Single");
        Directory.CreateDirectory(Path.Combine(album, "MP3"));

        var folders = CataloguingService.FindMp3Folders(album).ToList();

        Assert.Single(folders);
        Assert.Equal(Path.Combine(album, "MP3"), folders[0].Folder);
        Assert.Null(folders[0].DiscNumber);
    }

    [Fact]
    public void FindMp3Folders_MultiDisc_YieldsEveryDisc()
    {
        // Pre-fix only the first disc was returned — discs 2 and 3 silently
        // dropped out of the catalogue pipeline. This test seeds a 3-disc
        // album and asserts every disc surfaces.
        var album = Path.Combine(_tempDir, "Triple");
        Directory.CreateDirectory(Path.Combine(album, "Disc 1", "MP3"));
        Directory.CreateDirectory(Path.Combine(album, "Disc 2", "MP3"));
        Directory.CreateDirectory(Path.Combine(album, "Disc 3", "MP3"));

        var folders = CataloguingService.FindMp3Folders(album).ToList();

        Assert.Equal(3, folders.Count);
        Assert.Equal(1, folders[0].DiscNumber);
        Assert.Equal(2, folders[1].DiscNumber);
        Assert.Equal(3, folders[2].DiscNumber);
        Assert.Contains(Path.Combine(album, "Disc 1", "MP3"), folders.Select(f => f.Folder));
        Assert.Contains(Path.Combine(album, "Disc 2", "MP3"), folders.Select(f => f.Folder));
        Assert.Contains(Path.Combine(album, "Disc 3", "MP3"), folders.Select(f => f.Folder));
    }

    [Fact]
    public void FindMp3Folders_MultiDisc_PrefersDiscFoldersOverFlatFiles()
    {
        // Edge case: an album with both Disc N/MP3 folders AND flat MP3 files
        // at the album root (rare, but possible if someone manually copied
        // files). The disc-folder layout wins — flat files are ignored.
        var album = Path.Combine(_tempDir, "MixedLayout");
        Directory.CreateDirectory(Path.Combine(album, "Disc 1", "MP3"));
        File.WriteAllText(Path.Combine(album, "stray.mp3"), "fake");

        var folders = CataloguingService.FindMp3Folders(album).ToList();

        Assert.Single(folders);
        Assert.Equal(1, folders[0].DiscNumber);
    }

    [Fact]
    public void FindMp3Folders_FlatLayout_YieldsAlbumRootWithNullDiscNumber()
    {
        var album = Path.Combine(_tempDir, "Flat");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "01 track.mp3"), "fake");

        var folders = CataloguingService.FindMp3Folders(album).ToList();

        Assert.Single(folders);
        Assert.Equal(album, folders[0].Folder);
        Assert.Null(folders[0].DiscNumber);
    }

    [Fact]
    public void FindMp3Folders_NoAudio_YieldsNothing()
    {
        var album = Path.Combine(_tempDir, "Empty");
        Directory.CreateDirectory(album);

        Assert.Empty(CataloguingService.FindMp3Folders(album).ToList());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H26 — composer cache distinguishes shared surnames
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recording <see cref="ICatalogueReference"/> stub: returns a deterministic
    /// <see cref="ComposerInfo"/> per (lastName, firstName) lookup and counts
    /// how many times each pair was queried. The H26 cache-collision symptom
    /// is verified by feeding it two same-surname / different-firstName
    /// composers and asserting it received TWO lookups (not one).
    /// </summary>
    private sealed class RecordingReference : ICatalogueReference
    {
        public string SourceName => "Recording";
        public List<(string Last, string? First)> Calls { get; } = new();

        public Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null)
        {
            Calls.Add((lastName, firstName));
            // Map each distinct (last, first) to a distinct birth year so the
            // formatted Composer field shows whether the cache returned the
            // right entry.
            var birthYear = (firstName ?? "") switch
            {
                "Johann II"  => 1825,
                "Richard"    => 1864,
                "Johann Sebastian" => 1685,
                "Carl Philipp Emanuel" => 1714,
                _ => 1800,
            };
            ComposerInfo? info = new ComposerInfo
            {
                LastName  = lastName,
                FirstName = firstName ?? "",
                BirthYear = birthYear,
                DeathYear = birthYear + 70,
            };
            return Task.FromResult<ComposerInfo?>(info);
        }

        public Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm) =>
            Task.FromResult<WorkInfo?>(null);
    }

    [Fact]
    public async Task ComposerCache_SharedSurnameDifferentFirstName_DoesNotCollide()
    {
        // Pre-fix: an album with both Strausses cached on "Strauss" alone.
        // The second-encountered Strauss reused the first's birth/death years
        // — silent metadata corruption on save. After H26 each (last, first)
        // is a distinct cache key.
        var reference = new RecordingReference();
        var svc = new CataloguingService(reference);

        // Build a fake album with two MP3 files whose filenames decode to
        // the two distinct Strausses.
        var album = Path.Combine(_tempDir, "StraussFamily");
        var mp3 = Path.Combine(album, "MP3");
        Directory.CreateDirectory(mp3);
        // Filenames TagParser will see; the parse contract is "(composerLast),
        // (composerFirst) - (Work) - (Movement)". We don't actually invoke
        // TagLib for the read path because we want the formatting / cache
        // logic only — write empty files that ReadFileTag will fall back to
        // filename for, then exercise FormatEntriesAsync via the public
        // ReadAlbumTagsAsync entry point. Since TagLib will throw on the
        // empty MP3, the catch logs a warning and sets Name = filename.
        File.WriteAllText(Path.Combine(mp3, "01 Strauss, Johann II - On the Beautiful Blue Danube.mp3"), "");
        File.WriteAllText(Path.Combine(mp3, "02 Strauss, Richard - Also sprach Zarathustra.mp3"), "");

        // Don't actually need to inspect the output entries here — the
        // existence of TWO distinct cache lookups is the contract:
        var _ = await svc.ReadAlbumTagsAsync(album);

        // Both Strausses must have triggered a lookup. Pre-fix only the
        // first (Johann II) would have, and the Richard entry would have
        // reused Johann II's cached ComposerInfo.
        Assert.Contains(reference.Calls, c => c.Last == "Strauss" && c.First == "Johann II");
        Assert.Contains(reference.Calls, c => c.Last == "Strauss" && c.First == "Richard");
    }

    [Fact]
    public async Task ComposerCache_SameNameRepeated_HitsCacheOnSecondAccess()
    {
        // Verifies the cache STILL works — two tracks with the same
        // (last, first) should hit exactly once. Pre-fix this also worked,
        // and the H26 fix mustn't regress it.
        var reference = new RecordingReference();
        var svc = new CataloguingService(reference);

        var album = Path.Combine(_tempDir, "TwoTracksOneComposer");
        var mp3 = Path.Combine(album, "MP3");
        Directory.CreateDirectory(mp3);
        File.WriteAllText(Path.Combine(mp3, "01 Bach, Johann Sebastian - Goldberg Variations - 1. Aria.mp3"), "");
        File.WriteAllText(Path.Combine(mp3, "02 Bach, Johann Sebastian - Goldberg Variations - 2. Var. I.mp3"), "");

        await svc.ReadAlbumTagsAsync(album);

        var bachCalls = reference.Calls
            .Where(c => c.Last == "Bach" && c.First == "Johann Sebastian")
            .Count();
        Assert.Equal(1, bachCalls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H27 — tag-backup sidecar
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetTagBackupPath_AppendsSidecarSuffix()
    {
        var path = Path.Combine(_tempDir, "album", "01 track.mp3");
        Assert.Equal(path + ".tagbackup.json", CataloguingService.GetTagBackupPath(path));
    }

    [Fact]
    public void TagSnapshot_RoundTripsThroughJson()
    {
        // The sidecar lives across pipeline runs and across app restarts;
        // its JSON shape is the contract. This pins down deserialisation
        // so a careless field rename doesn't silently break every existing
        // user's backups.
        var snapshot = new TagSnapshot
        {
            Title          = "Symphony No. 9 - 4. Finale",
            Performers     = new[] { "Karajan, Herbert von" },
            Album          = "Beethoven Symphonies 9 Karajan",
            Composers      = new[] { "Beethoven, Ludwig van" },
            Genres         = new[] { "Classical" },
            Track          = 4,
            TrackCount     = 4,
            Year           = 1962,
            Disc           = 5,
            DiscCount      = 5,
            TitleSort      = "Symphony No. 9",
            AlbumSort      = "Beethoven Symphonies 9 Karajan",
            PerformersSort = new[] { "Karajan, Herbert von" },
            ComposersSort  = new[] { "Beethoven, Ludwig van" },
        };

        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<TagSnapshot>(json)!;

        Assert.Equal(snapshot.Title,      restored.Title);
        Assert.Equal(snapshot.Performers, restored.Performers);
        Assert.Equal(snapshot.Album,      restored.Album);
        Assert.Equal(snapshot.Composers,  restored.Composers);
        Assert.Equal(snapshot.Genres,     restored.Genres);
        Assert.Equal(snapshot.Track,      restored.Track);
        Assert.Equal(snapshot.TrackCount, restored.TrackCount);
        Assert.Equal(snapshot.Year,       restored.Year);
        Assert.Equal(snapshot.Disc,       restored.Disc);
        Assert.Equal(snapshot.DiscCount,  restored.DiscCount);
        Assert.Equal(snapshot.TitleSort,  restored.TitleSort);
        Assert.Equal(snapshot.AlbumSort,  restored.AlbumSort);
    }

    [Fact]
    public void RestoreFromBackup_NoBackupFile_ReturnsFailureWithoutThrowing()
    {
        var svc = new CataloguingService(reference: null!);
        var bogus = Path.Combine(_tempDir, "missing.mp3");

        var result = svc.RestoreFromBackup(bogus);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("No tag backup found", result.ErrorMessage);
    }

    [Fact]
    public void RestoreFromBackup_CorruptBackup_ReturnsFailureWithoutThrowing()
    {
        var svc  = new CataloguingService(reference: null!);
        var path = Path.Combine(_tempDir, "audio.mp3");
        File.WriteAllText(path, "not really an mp3");
        File.WriteAllText(CataloguingService.GetTagBackupPath(path), "{ this is not valid json");

        var result = svc.RestoreFromBackup(path);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("corrupt", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
