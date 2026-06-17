using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Filesystem-backed tests for <see cref="AlbumArtworkLocator"/>. Each test
/// lays out a temp archive folder and checks resolution: embedded-tag art
/// first (via an injected reader seam, so no binary audio asset is needed),
/// then a conventionally-named folder image, then the single/largest image in
/// the folder, plus caching + invalidation.
/// </summary>
public class AlbumArtworkLocatorTests : IDisposable
{
    private readonly string _root;

    public AlbumArtworkLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "CDArchiveArtTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
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

    // Builds a locator over the temp root. The embedded reader defaults to
    // "no embedded art" so the file-fallback paths are exercised; tests that
    // care about embedded art pass an explicit reader.
    private AlbumArtworkLocator Make(IArchiveSettings settings, Func<string, byte[]?>? reader = null)
        => new(settings, new ArchiveAudioLocator(settings), reader ?? (_ => null));

    private static AlbumDisc DiscWithTrack(int discNumber, int trackNumber)
    {
        var d = new AlbumDisc { DiscNumber = discNumber };
        d.Tracks.Add(new AlbumTrack { TrackNumber = trackNumber });
        return d;
    }

    // ── Folder-image resolution (no embedded art) ──────────────────────────

    [Fact]
    public void Resolve_FindsCoverJpg_InAlbumFolder()
    {
        var expected = Touch("Mahler 2 Bernstein", "cover.jpg");
        var album = new CanonAlbum { ArchiveFolder = "Mahler 2 Bernstein" };
        Assert.Equal(expected, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumTitle_WhenArchiveFolderNull()
    {
        var expected = Touch("Goldberg Variations Gould", "folder.jpg");
        var album = new CanonAlbum { Title = "Goldberg Variations Gould" };
        Assert.Equal(expected, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoImagePresent()
    {
        Touch("No Art Album", "notes.txt");
        var album = new CanonAlbum { ArchiveFolder = "No Art Album" };
        Assert.Null(Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album));
    }

    [Fact]
    public void Resolve_FallsBackToSingleArbitrarilyNamedImage()
    {
        var expected = Touch("Arbitrary Art", "71KMUqGkSwL._UF1000.jpg");
        Touch("Arbitrary Art", "notes.txt");
        var album = new CanonAlbum { ArchiveFolder = "Arbitrary Art" };
        Assert.Equal(expected, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Resolve_PrefersConventionalNameOverArbitrary()
    {
        var conventional = Touch("Mixed Names", "folder.jpg");
        Touch("Mixed Names", "s-l1200.jpg");
        var album = new CanonAlbum { ArchiveFolder = "Mixed Names" };
        Assert.Equal(conventional, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Resolve_PicksLargestImage_WhenMultipleArbitrary()
    {
        var small = Path.Combine(_root, "Multi Art", "back.jpg");
        var big   = Path.Combine(_root, "Multi Art", "scan.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(small)!);
        File.WriteAllBytes(small, new byte[100]);
        File.WriteAllBytes(big, new byte[5000]);
        var album = new CanonAlbum { ArchiveFolder = "Multi Art" };
        Assert.Equal(big, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Resolve_PrefersDiscFolderArt_OnMultiDiscAlbum()
    {
        Touch("Box Set", "cover.jpg");
        var discArt = Touch("Box Set", "Disc 2", "cover.jpg");
        var album = new CanonAlbum { ArchiveFolder = "Box Set" };
        album.Discs.Add(new AlbumDisc { DiscNumber = 1 });
        var disc2 = new AlbumDisc { DiscNumber = 2 };
        album.Discs.Add(disc2);
        Assert.Equal(discArt, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album, disc2)?.FilePath);
    }

    [Fact]
    public void Resolve_FallsBackToAlbumRoot_WhenDiscFolderHasNoArt()
    {
        var rootArt = Touch("Box Set 2", "cover.jpg");
        Touch("Box Set 2", "Disc 1", "notes.txt");
        var album = new CanonAlbum { ArchiveFolder = "Box Set 2" };
        var disc1 = new AlbumDisc { DiscNumber = 1 };
        album.Discs.Add(disc1);
        album.Discs.Add(new AlbumDisc { DiscNumber = 2 });
        Assert.Equal(rootArt, Make(new FakeSettings { ArchiveRootPath = _root }).Resolve(album, disc1)?.FilePath);
    }

    [Fact]
    public void Resolve_AbsoluteArchiveFolder_BypassesRoot()
    {
        var otherRoot = Path.Combine(_root, "elsewhere", "Special Album");
        Directory.CreateDirectory(otherRoot);
        var expected = Path.Combine(otherRoot, "front.jpg");
        File.WriteAllText(expected, "");
        var settings = new FakeSettings { ArchiveRootPath = Path.Combine(_root, "main") };
        var album = new CanonAlbum { ArchiveFolder = otherRoot };
        Assert.Equal(expected, Make(settings).Resolve(album)?.FilePath);
    }

    [Fact]
    public void Invalidate_RepicksUpNewlyAddedArt()
    {
        var locator = Make(new FakeSettings { ArchiveRootPath = _root });
        var album = new CanonAlbum { ArchiveFolder = "Late Art" };
        Directory.CreateDirectory(Path.Combine(_root, "Late Art"));

        Assert.Null(locator.Resolve(album));           // none yet (miss cached)
        var added = Touch("Late Art", "cover.jpg");
        Assert.Null(locator.Resolve(album));           // still cached miss
        locator.Invalidate();
        Assert.Equal(added, locator.Resolve(album)?.FilePath);
    }

    // ── Embedded-tag art (preferred over folder images) ────────────────────

    [Fact]
    public void Resolve_PrefersEmbeddedArt_OverFolderImage()
    {
        Touch("Embedded Album", "cover.jpg");          // folder image present...
        var flac = Touch("Embedded Album", "FLAC", "01 first.flac");
        var settings = new FakeSettings { ArchiveRootPath = _root };
        byte[] art = { 1, 2, 3, 4, 5 };

        var locator = Make(settings,
            path => string.Equals(path, flac, StringComparison.OrdinalIgnoreCase) ? art : null);

        var album = new CanonAlbum { ArchiveFolder = "Embedded Album" };
        album.Discs.Add(DiscWithTrack(1, 1));

        var result = locator.Resolve(album);
        Assert.NotNull(result);
        Assert.Equal(art, result!.EmbeddedData);       // embedded wins
        Assert.Null(result.FilePath);
    }

    [Fact]
    public void Resolve_FallsBackToFolderImage_WhenNoEmbeddedArt()
    {
        var cover = Touch("No Embedded", "cover.jpg");
        Touch("No Embedded", "FLAC", "01 first.flac");
        var settings = new FakeSettings { ArchiveRootPath = _root };

        var locator = Make(settings, _ => null);       // reader finds nothing embedded
        var album = new CanonAlbum { ArchiveFolder = "No Embedded" };
        album.Discs.Add(DiscWithTrack(1, 1));

        var result = locator.Resolve(album);
        Assert.Equal(cover, result?.FilePath);
        Assert.Null(result?.EmbeddedData);
    }

    // ── Loose-track art (embedded from the track's own override file) ──────

    [Fact]
    public void ResolveFromTrack_ReadsEmbeddedFromOverridePath()
    {
        var mp3 = Touch("loose", "Abide with Me.mp3");
        var settings = new FakeSettings { ArchiveRootPath = _root };
        byte[] art = { 7, 7, 7 };
        var locator = Make(settings,
            path => string.Equals(path, mp3, StringComparison.OrdinalIgnoreCase) ? art : null);

        var track = new AlbumTrack { TrackNumber = 0, Mp3Path = mp3 };
        var result = locator.ResolveFromTrack(track);

        Assert.NotNull(result);
        Assert.Equal(art, result!.EmbeddedData);
        Assert.Null(result.FilePath);
    }

    [Fact]
    public void ResolveFromTrack_PrefersFormatOrder_Flac()
    {
        var flac = Touch("loose2", "x.flac");
        var mp3  = Touch("loose2", "x.mp3");
        var settings = new FakeSettings { ArchiveRootPath = _root, PreferredAudioFormat = PreferredAudioFormat.Flac };
        string? readPath = null;
        var locator = Make(settings, p => { readPath = p; return new byte[] { 1 }; });

        var track = new AlbumTrack { FlacPath = flac, Mp3Path = mp3 };
        locator.ResolveFromTrack(track);

        Assert.Equal(flac, readPath); // preferred FLAC read first
    }

    [Fact]
    public void ResolveFromTrack_NullWhenNoOverrideFile()
    {
        var locator = Make(new FakeSettings { ArchiveRootPath = _root }, _ => new byte[] { 1 });
        Assert.Null(locator.ResolveFromTrack(new AlbumTrack { TrackNumber = 0 }));
    }

    [Fact]
    public void ResolveFromTrack_NullWhenNoEmbeddedArt()
    {
        var mp3 = Touch("loose3", "y.mp3");
        var locator = Make(new FakeSettings { ArchiveRootPath = _root }, _ => null);
        Assert.Null(locator.ResolveFromTrack(new AlbumTrack { Mp3Path = mp3 }));
    }

    [Fact]
    public void Resolve_EmbeddedReadIsCached_NotReReadPerCall()
    {
        var flac = Touch("Cached Embedded", "FLAC", "01 first.flac");
        var settings = new FakeSettings { ArchiveRootPath = _root };
        int reads = 0;
        var locator = Make(settings, _ => { reads++; return new byte[] { 9 }; });

        var album = new CanonAlbum { ArchiveFolder = "Cached Embedded" };
        album.Discs.Add(DiscWithTrack(1, 1));

        locator.Resolve(album);
        locator.Resolve(album);
        locator.Resolve(album);
        Assert.Equal(1, reads);                        // read once, then cached
    }
}
