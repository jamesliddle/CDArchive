using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Filesystem-backed tests for <see cref="ArchiveAudioLocator"/>. Each test
/// creates a temp directory laid out like the user's real archive, runs the
/// locator, then tears the directory down.
/// </summary>
public class ArchiveAudioLocatorTests : IDisposable
{
    private readonly string _root;

    public ArchiveAudioLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "CDArchiveLocatorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private sealed class FakeSettings : IArchiveSettings
    {
        public string ArchiveRootPath { get; set; } = "";
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
        public bool ShowAlbumListArtwork { get; set; } = true;
        public bool ShowTrackListArtwork { get; set; }
        public bool ShowPlayerArtwork    { get; set; } = true;
        public ArtworkSize AlbumListArtworkSize { get; set; } = ArtworkSize.Medium;
        public ArtworkSize TrackListArtworkSize { get; set; } = ArtworkSize.Small;
        public ArtworkSize PlayerArtworkSize    { get; set; } = ArtworkSize.Medium;
        public void Save() { }
        public void Initialize() { }
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void SingleDisc_Convention_ResolvesPreferredFlac()
    {
        Touch("Beethoven Symphonies 1 3 Bernstein", "FLAC", "01 I. Symphony No. 1.flac");
        Touch("Beethoven Symphonies 1 3 Bernstein", "MP3",  "01 I. Symphony No. 1.mp3");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Beethoven Symphonies 1 3 Bernstein" };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        var track = new AlbumTrack { TrackNumber = 1 };
        album.Discs.Add(disc);

        var hit = locator.Resolve(album, disc, track);

        Assert.NotNull(hit);
        Assert.Equal(AudioFormat.Flac, hit!.Value.Format);
        Assert.EndsWith(".flac", hit.Value.Path);
    }

    [Fact]
    public void SingleDisc_Convention_FallsBackToMp3WhenFlacMissing()
    {
        Touch("Wirén Strings", "MP3", "02 - II. Andante.mp3");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Wirén Strings" };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        var track = new AlbumTrack { TrackNumber = 2 };
        album.Discs.Add(disc);

        var hit = locator.Resolve(album, disc, track);

        Assert.NotNull(hit);
        Assert.Equal(AudioFormat.Mp3, hit!.Value.Format);
    }

    [Fact]
    public void PreferenceMp3_ReturnsMp3WhenBothExist()
    {
        Touch("Album", "FLAC", "01 Track.flac");
        Touch("Album", "MP3",  "01 Track.mp3");

        var settings = new FakeSettings
        {
            ArchiveRootPath = _root,
            PreferredAudioFormat = PreferredAudioFormat.Mp3,
        };
        var locator = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Album" };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        var track = new AlbumTrack { TrackNumber = 1 };
        album.Discs.Add(disc);

        var hit = locator.Resolve(album, disc, track);
        Assert.Equal(AudioFormat.Mp3, hit!.Value.Format);
    }

    [Fact]
    public void MultiDisc_DefaultDiscFolder_Resolves()
    {
        Touch("Beethoven Symphonies 1 2 4 5 Böhm", "Disc 2", "FLAC", "01 Sym 4 - I. Adagio.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Beethoven Symphonies 1 2 4 5 Böhm" };
        var d1    = new AlbumDisc { DiscNumber = 1 };
        var d2    = new AlbumDisc { DiscNumber = 2 };
        album.Discs.Add(d1);
        album.Discs.Add(d2);
        var track = new AlbumTrack { TrackNumber = 1 };

        var hit = locator.Resolve(album, d2, track);

        Assert.NotNull(hit);
        Assert.EndsWith(".flac", hit!.Value.Path);
    }

    [Fact]
    public void MultiDisc_FolderNameOverride_Resolves()
    {
        // Brilliant Classics-style sub-disc naming
        Touch("Brilliant Classics Bach Edition", "Disc 3-06", "MP3", "01 Cantata.mp3");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Brilliant Classics Bach Edition" };
        var d     = new AlbumDisc { DiscNumber = 3, FolderName = "Disc 3-06" };
        album.Discs.Add(d);
        // Album has more than one disc — without FolderName override the default
        // "Disc 3" wouldn't match the on-disk "Disc 3-06".
        album.Discs.Add(new AlbumDisc { DiscNumber = 1 });

        var hit = locator.Resolve(album, d, new AlbumTrack { TrackNumber = 1 });

        Assert.NotNull(hit);
        Assert.Equal(AudioFormat.Mp3, hit!.Value.Format);
    }

    [Fact]
    public void PerTrackOverride_BeatsConvention()
    {
        // Convention would resolve under root, but the per-track override
        // points somewhere else entirely.
        Touch("Album", "FLAC", "01 Convention.flac");
        var overridePath = Touch("Standalone", "01 Override.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Album" };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);
        var track = new AlbumTrack { TrackNumber = 1, FlacPath = overridePath };

        var hit = locator.Resolve(album, disc, track);

        Assert.NotNull(hit);
        Assert.Equal(overridePath, hit!.Value.Path);
    }

    [Fact]
    public void AbsoluteAlbumFolder_UsedAsIs()
    {
        // ArchiveFolder is absolute → ignore the root setting entirely.
        var albumDir = Path.Combine(_root, "Loose Folder");
        Touch("Loose Folder", "FLAC", "03 Anything.flac");

        var settings = new FakeSettings { ArchiveRootPath = @"D:\bogus-not-used" };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = albumDir };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);
        var track = new AlbumTrack { TrackNumber = 3 };

        var hit = locator.Resolve(album, disc, track);

        Assert.NotNull(hit);
        Assert.EndsWith(".flac", hit!.Value.Path);
    }

    [Fact]
    public void NoFileAnywhere_ReturnsNull()
    {
        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = "Nonexistent" };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);

        Assert.Null(locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 }));
    }

    [Fact]
    public void NoArchiveFolder_NoTitle_NoOverride_ReturnsNull()
    {
        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum { ArchiveFolder = null, Title = null };
        var disc  = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);

        Assert.Null(locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 }));
    }

    [Fact]
    public void NoArchiveFolder_FallsBackToAlbumTitle()
    {
        // No explicit ArchiveFolder — locator should use the Title as the
        // folder name, matching the "folder name = album title" convention.
        Touch("Beethoven Symphonies 1 3 Bernstein", "FLAC", "01 Symphony.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum
        {
            ArchiveFolder = null,
            Title         = "Beethoven Symphonies 1 3 Bernstein",
        };
        var disc = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);

        var hit = locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hit);
        Assert.EndsWith(".flac", hit!.Value.Path);
    }

    [Fact]
    public void ArchiveFolder_OverridesTitleWhenSet()
    {
        // Title doesn't match the on-disk folder; ArchiveFolder does.
        // The explicit override wins.
        Touch("on-disk-name", "FLAC", "01 Track.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);

        var album = new CanonAlbum
        {
            ArchiveFolder = "on-disk-name",
            Title         = "Display Title That Differs From Folder",
        };
        var disc = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);

        var hit = locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hit);
        Assert.EndsWith(".flac", hit!.Value.Path);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rework H8 — filesystem-probe caching
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Setup helper for the cache tests: one album, one disc, 10 tracks all
    /// living in the same FLAC folder. Returns the locator + the album + disc
    /// + track list ready to feed into Resolve.
    /// </summary>
    private (ArchiveAudioLocator locator, CanonAlbum album, AlbumDisc disc, List<AlbumTrack> tracks)
        BuildTenTrackFixture()
    {
        for (int i = 1; i <= 10; i++)
            Touch("Album", "FLAC", $"{i:D2} Track {i}.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);
        var album    = new CanonAlbum { Title = "Album" };
        var disc     = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);
        var tracks = Enumerable.Range(1, 10)
            .Select(n => new AlbumTrack { TrackNumber = n })
            .ToList();
        return (locator, album, disc, tracks);
    }

    /// <summary>
    /// The H8 win: resolving every track on a single-disc album probes the
    /// filesystem twice total (Directory.Exists for the album dir +
    /// Directory.Exists + Directory.EnumerateFiles for the FLAC dir), not
    /// once-per-track. Pre-fix this was ~30 syscalls for 10 tracks; with
    /// the cache it's 3.
    /// </summary>
    [Fact]
    public void Resolve_OnSameDisc_CachesFilesystemProbes()
    {
        var (locator, album, disc, tracks) = BuildTenTrackFixture();

        foreach (var track in tracks)
            Assert.NotNull(locator.Resolve(album, disc, track));

        // Album-dir exists (1) + FLAC-dir exists (1) + FLAC-dir enumerate (1) = 3.
        // Pre-fix every track would have re-probed all three, for ~30 total.
        Assert.Equal(3, locator.FilesystemProbeCount);
    }

    [Fact]
    public void Resolve_AfterInvalidate_ReProbesFilesystem()
    {
        var (locator, album, disc, tracks) = BuildTenTrackFixture();

        Assert.NotNull(locator.Resolve(album, disc, tracks[0]));
        var probesBefore = locator.FilesystemProbeCount;
        Assert.True(probesBefore > 0);

        locator.Invalidate();
        // Invalidate resets the counter — sanity check.
        Assert.Equal(0, locator.FilesystemProbeCount);

        Assert.NotNull(locator.Resolve(album, disc, tracks[0]));
        // Same 3 probes again now that the cache is empty.
        Assert.Equal(3, locator.FilesystemProbeCount);
    }

    [Fact]
    public void Resolve_AfterArchiveRootChange_AutoInvalidates()
    {
        // Two parallel fixtures with the same on-disk shape but different
        // archive roots. Switching the settings between them must cause
        // the cache to drop — otherwise the second Resolve would look up
        // a path constructed under the old root.
        var rootA = Path.Combine(_root, "RootA");
        var rootB = Path.Combine(_root, "RootB");
        File.WriteAllText(Touch("RootA", "Album", "FLAC", "01 a.flac"), "");
        File.WriteAllText(Touch("RootB", "Album", "FLAC", "01 b.flac"), "");

        var settings = new FakeSettings { ArchiveRootPath = rootA };
        var locator  = new ArchiveAudioLocator(settings);
        var album    = new CanonAlbum { Title = "Album" };
        var disc     = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc);

        var hitA = locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hitA);
        Assert.Contains("RootA", hitA!.Value.Path);

        // Pretend the user changed ArchiveRootPath in the Settings tab.
        settings.ArchiveRootPath = rootB;

        var hitB = locator.Resolve(album, disc, new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hitB);
        Assert.Contains("RootB", hitB!.Value.Path);
        // Cache was rebuilt — the auto-invalidation re-probed for the new
        // root. Probe count reflects only the post-invalidation work.
        Assert.Equal(3, locator.FilesystemProbeCount);
    }

    [Fact]
    public void Resolve_AcrossMultipleDiscs_CachesPerDirectory()
    {
        // A two-disc album with separate FLAC folders. The album dir and each
        // disc/format dir caches independently.
        Touch("DoubleAlbum", "Disc 1", "FLAC", "01 a.flac");
        Touch("DoubleAlbum", "Disc 1", "FLAC", "02 b.flac");
        Touch("DoubleAlbum", "Disc 2", "FLAC", "01 c.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);
        var album    = new CanonAlbum { Title = "DoubleAlbum" };
        var d1       = new AlbumDisc { DiscNumber = 1 };
        var d2       = new AlbumDisc { DiscNumber = 2 };
        album.Discs.Add(d1);
        album.Discs.Add(d2);

        Assert.NotNull(locator.Resolve(album, d1, new AlbumTrack { TrackNumber = 1 }));
        Assert.NotNull(locator.Resolve(album, d1, new AlbumTrack { TrackNumber = 2 }));
        Assert.NotNull(locator.Resolve(album, d2, new AlbumTrack { TrackNumber = 1 }));

        // Probes:
        //   album dir (1)
        //   d1 dir (1) + d1 FLAC exists (1) + d1 FLAC enumerate (1)
        //   d2 dir (1) + d2 FLAC exists (1) + d2 FLAC enumerate (1)
        // = 7 total. Second Resolve on d1/track2 reuses every cached entry.
        Assert.Equal(7, locator.FilesystemProbeCount);
    }

    /// <summary>
    /// Rework H46 regression: a 10+ disc box set scaffolded with padded
    /// folder names (<c>Disc 01</c>) must resolve for discs 1-9 via the
    /// locator. Pre-fix the locator only tried unpadded <c>Disc 1</c>;
    /// playback silently failed and the user had to set
    /// <see cref="AlbumDisc.FolderName"/> manually for every padded disc.
    /// </summary>
    [Fact]
    public void Resolve_PaddedDiscFolder_ResolvesViaCandidateNames()
    {
        // Album scaffolded as a 12-disc box set: Disc 01..Disc 12.
        Touch("BoxSet", "Disc 01", "FLAC", "01 a.flac");
        Touch("BoxSet", "Disc 09", "FLAC", "01 i.flac");
        Touch("BoxSet", "Disc 12", "FLAC", "01 l.flac");

        var settings = new FakeSettings { ArchiveRootPath = _root };
        var locator  = new ArchiveAudioLocator(settings);
        var album    = new CanonAlbum { Title = "BoxSet" };
        for (int i = 1; i <= 12; i++)
            album.Discs.Add(new AlbumDisc { DiscNumber = i });

        // Disc 1 — padded folder, must still resolve.
        var hit1  = locator.Resolve(album, album.Discs[0],
            new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hit1);
        Assert.Contains("Disc 01", hit1!.Value.Path);

        // Disc 9 — also padded.
        var hit9 = locator.Resolve(album, album.Discs[8],
            new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hit9);
        Assert.Contains("Disc 09", hit9!.Value.Path);

        // Disc 12 — naturally two-digit; the helper's unpadded form is
        // already "Disc 12" so this resolves the same way.
        var hit12 = locator.Resolve(album, album.Discs[11],
            new AlbumTrack { TrackNumber = 1 });
        Assert.NotNull(hit12);
        Assert.Contains("Disc 12", hit12!.Value.Path);
    }
}
