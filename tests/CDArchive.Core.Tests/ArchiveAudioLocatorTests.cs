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
        public void Save() { }
        public void Load() { }
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
}
