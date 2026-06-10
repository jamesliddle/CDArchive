using System.Text;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Rework H5 + H6 regression tests for <see cref="ItunesLibraryReference"/>:
///
/// <list type="bullet">
///   <item>H5 — <see cref="ItunesLibraryReference.LoadAllTracksAsync"/> reads
///     from the in-memory cache after the first call; subsequent calls
///     return the same list instance and re-walking the XML is avoided.
///     <see cref="ItunesLibraryReference.Refresh"/> invalidates the cache
///     so the next access re-parses.</item>
///   <item>H6 — the URL-encoded archive-folder filter used to gate composer
///     / works indexing is computed from
///     <see cref="IArchiveSettings.ArchiveRootPath"/>. The default config
///     (<c>D:\CD archive</c>) still produces <c>CD%20archive</c> — the
///     pre-fix hardcoded value, so the change is backward-compatible. A
///     custom <c>ArchiveRootPath</c> produces a substring matching the
///     leaf folder.</item>
/// </list>
/// </summary>
public class ItunesLibraryReferenceTests : IDisposable
{
    private readonly string _tempDir;

    public ItunesLibraryReferenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CDArchive.Itunes.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Writes a tiny iTunes Music Library.xml-shaped file with a fixed
    /// number of Music tracks. Each track lives under
    /// <c>file://localhost/&lt;archiveFolder&gt;/Album/track.mp3</c>, with
    /// the archive folder URL-encoded — so the composer / works indexing
    /// either matches or doesn't depending on whether the filter substring
    /// is the same encoded form.
    /// </summary>
    private string WriteFakeItunesXml(int trackCount, string archiveFolderForLocations)
    {
        var path = Path.Combine(_tempDir, $"library-{Guid.NewGuid():N}.xml");
        // Note on whitespace: the iTunes XML reader uses ReadElementContentAsString
        // on `<key>X</key>` which positions the reader on the *next* node. If
        // the next node is the value element with no separating whitespace,
        // a subsequent AdvanceToElement(reader, "dict") would call Read() and
        // skip past it. Real iTunes XML always has whitespace/newlines between
        // `</key>` and the following element, so we mimic that here.
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        sb.AppendLine("""<plist version="1.0">""");
        sb.AppendLine("<dict>");
        sb.AppendLine("  <key>Tracks</key>");
        sb.AppendLine("  <dict>");
        for (int i = 1; i <= trackCount; i++)
        {
            var encodedFolder = Uri.EscapeDataString(archiveFolderForLocations);
            sb.AppendLine($"    <key>{i}</key>");
            sb.AppendLine($"    <dict>");
            sb.AppendLine($"      <key>Track ID</key> <integer>{i}</integer>");
            sb.AppendLine($"      <key>Name</key> <string>Symphony No. {i}</string>");
            sb.AppendLine($"      <key>Composer</key> <string>Beethoven, Ludwig van (1770-1827)</string>");
            sb.AppendLine($"      <key>Location</key> <string>file://localhost/D:/{encodedFolder}/Album%20{i}/track.mp3</string>");
            sb.AppendLine($"    </dict>");
        }
        sb.AppendLine("  </dict>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>Fake <see cref="IArchiveSettings"/> for tests — no I/O.</summary>
    private sealed class StubSettings : IArchiveSettings
    {
        public string ArchiveRootPath { get; set; } = @"D:\CD archive";
        public string FfmpegPath { get; set; } = "ffmpeg";
        public int Mp3Bitrate { get; set; } = 320;
        public PreferredAudioFormat PreferredAudioFormat { get; set; } = PreferredAudioFormat.Flac;
        public float PlayerVolume { get; set; } = 1.0f;
        public bool StopAfterCurrentTrack { get; set; }
        public bool ShowPlayingFilePath { get; set; }
        public int SeekForwardSeconds { get; set; } = 10;
        public int SeekBackwardSeconds { get; set; } = 10;
        public int PreviousRestartThresholdSeconds { get; set; } = 2;
        public bool EnableMusicBrainzImportEnrichment { get; set; }
        public int  MusicBrainzCandidatesPerProposal  { get; set; } = 3;
        public bool ApplyMbAlbumMetadata          { get; set; } = true;
        public bool ApplyMbPerformerCredits       { get; set; } = true;
        public bool ApplyMbRecordingSessions      { get; set; } = true;
        public bool ApplyMbCanonicalWorkStructure { get; set; } = true;
        public void Save() { }
        public void Initialize() { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H6 — ComputeArchiveFolderFilter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeArchiveFolderFilter_DefaultRoot_ProducesPreFixHardcodedValue()
    {
        // Backward-compat: D:\CD archive → CD%20archive, same as the
        // pre-fix hardcoded string. Users who have never changed
        // ArchiveRootPath see no behavioural difference.
        Assert.Equal("CD%20archive",
            ItunesLibraryReference.ComputeArchiveFolderFilter(@"D:\CD archive"));
    }

    [Fact]
    public void ComputeArchiveFolderFilter_CustomRoot_UsesLeafFolderName()
    {
        Assert.Equal("Classical%20CDs",
            ItunesLibraryReference.ComputeArchiveFolderFilter(@"E:\Music\Classical CDs"));
    }

    [Fact]
    public void ComputeArchiveFolderFilter_TrailingSeparator_StillResolvesLeaf()
    {
        // Path.GetFileName returns "" for a path ending in a separator; the
        // implementation trims trailing separators first.
        Assert.Equal("CD%20archive",
            ItunesLibraryReference.ComputeArchiveFolderFilter(@"D:\CD archive\"));
        Assert.Equal("CD%20archive",
            ItunesLibraryReference.ComputeArchiveFolderFilter(@"D:\CD archive/"));
    }

    [Fact]
    public void ComputeArchiveFolderFilter_EmptyOrNull_ProducesEmptyFilter()
    {
        // An empty filter means "no filter" — BuildCache indexes every track.
        // Used by headless test fixtures where the archive path isn't relevant.
        Assert.Equal("", ItunesLibraryReference.ComputeArchiveFolderFilter(""));
        Assert.Equal("", ItunesLibraryReference.ComputeArchiveFolderFilter("   "));
    }

    [Fact]
    public async Task BuildCache_HonoursCustomArchiveRootPath()
    {
        var settings = new StubSettings { ArchiveRootPath = @"E:\Music\Classical CDs" };
        // Tracks are written with Location URLs containing "Classical CDs"
        // (URL-encoded "Classical%20CDs"), so the H6-derived filter matches
        // and composer indexing populates.
        var xmlPath = WriteFakeItunesXml(trackCount: 2, archiveFolderForLocations: "Classical CDs");

        var svc = new ItunesLibraryReference(settings, xmlPath);
        var match = await svc.LookupComposerAsync("Beethoven");

        Assert.NotNull(match);
        Assert.Equal("Beethoven", match!.LastName);
        Assert.Equal(1770, match.BirthYear);
    }

    [Fact]
    public async Task BuildCache_DoesNotIndexComposers_WhenLocationDoesNotMatchArchiveFolder()
    {
        var settings = new StubSettings { ArchiveRootPath = @"E:\Music\NotMatching" };
        // Tracks live under "CD archive" but the configured root is
        // "NotMatching" — H6 fix: the composer index should be empty.
        var xmlPath = WriteFakeItunesXml(trackCount: 2, archiveFolderForLocations: "CD archive");

        var svc = new ItunesLibraryReference(settings, xmlPath);
        var match = await svc.LookupComposerAsync("Beethoven");

        Assert.Null(match);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H5 — caching
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllTracksAsync_CachesFirstCall()
    {
        var settings = new StubSettings();
        var xmlPath = WriteFakeItunesXml(trackCount: 3, archiveFolderForLocations: "CD archive");
        var svc = new ItunesLibraryReference(settings, xmlPath);

        var first  = await svc.LoadAllTracksAsync();
        var second = await svc.LoadAllTracksAsync();

        Assert.Equal(3, first.Count);
        // Second call returns the same instance — proves the cache is hit.
        // Pre-fix this re-walked the XML and returned a fresh list each time.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task LoadAllTracksAsync_AfterRefresh_RebuildsCache()
    {
        var settings = new StubSettings();
        var xmlPath = WriteFakeItunesXml(trackCount: 1, archiveFolderForLocations: "CD archive");
        var svc = new ItunesLibraryReference(settings, xmlPath);

        var beforeRefresh = await svc.LoadAllTracksAsync();
        Assert.Single(beforeRefresh);

        // Simulate the user editing iTunes — overwrite the same fake XML
        // with a different track count, then call Refresh and verify the
        // next access picks up the new contents.
        File.Delete(xmlPath);
        File.WriteAllText(xmlPath, File.ReadAllText(
            WriteFakeItunesXml(trackCount: 5, archiveFolderForLocations: "CD archive")));

        svc.Refresh();
        var afterRefresh = await svc.LoadAllTracksAsync();

        Assert.Equal(5, afterRefresh.Count);
        // Different instance — the lazy was replaced and the cache rebuilt.
        Assert.NotSame(beforeRefresh, afterRefresh);
    }

    [Fact]
    public async Task LoadAllTracksAsync_AndLookup_ShareSameXmlWalk()
    {
        // Pre-fix LoadAllTracks and BuildCache walked the XML separately;
        // now BuildCache produces both projections in one pass. We can't
        // directly observe the walk count, but we can verify both paths
        // see consistent data after the first access — and the second call
        // returns the cached instance (so no second walk fired).
        var settings = new StubSettings();
        var xmlPath = WriteFakeItunesXml(trackCount: 4, archiveFolderForLocations: "CD archive");
        var svc = new ItunesLibraryReference(settings, xmlPath);

        var composer = await svc.LookupComposerAsync("Beethoven");
        Assert.NotNull(composer);

        var tracks = await svc.LoadAllTracksAsync();
        Assert.Equal(4, tracks.Count);

        // Cached: a second LoadAllTracksAsync returns the same instance.
        var tracksAgain = await svc.LoadAllTracksAsync();
        Assert.Same(tracks, tracksAgain);
    }
}
