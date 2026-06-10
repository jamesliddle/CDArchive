using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers the behaviour around <see cref="FfmpegConversionService"/> that
/// doesn't require a real ffmpeg.exe: pure path derivation (H16),
/// missing-source short-circuit, missing-ffmpeg failure mode, and
/// partial-output cleanup. The end-to-end process invocation and per-argument
/// escaping (C8) are exercised by the .NET runtime's own well-tested
/// <c>ProcessStartInfo.ArgumentList</c> path.
/// </summary>
public class FfmpegConversionServiceTests : IDisposable
{
    private readonly string _root;

    public FfmpegConversionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "CDArchiveFfmpegTests_" + Guid.NewGuid().ToString("N"));
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
        public void Save() { }
        public void Initialize() { }
    }

    // ---------------- DeriveMp3Path (H16) ----------------

    [Fact]
    public void DeriveMp3Path_ReplacesFlacParentSegment()
    {
        var flac = Path.Combine("D:\\CD archive", "Beethoven", "FLAC", "01 Symphony.flac");
        var mp3  = FfmpegConversionService.DeriveMp3Path(flac);
        var expected = Path.Combine("D:\\CD archive", "Beethoven", "MP3", "01 Symphony.mp3");
        Assert.Equal(expected, mp3);
    }

    [Fact]
    public void DeriveMp3Path_RootContainingFlacSegment_IsNotMangled()
    {
        // H16 example: an archive root with "FLAC" embedded in a non-folder
        // position. The naive global Replace mangled this; the segment-aware
        // derivation must leave the root's "FLAC" segment intact and only
        // touch the immediate parent of the file.
        var flac = Path.Combine("D:\\FLAC", "Archive", "Beethoven", "FLAC", "01 Track.flac");
        var mp3  = FfmpegConversionService.DeriveMp3Path(flac);
        var expected = Path.Combine("D:\\FLAC", "Archive", "Beethoven", "MP3", "01 Track.mp3");
        Assert.Equal(expected, mp3);
    }

    [Fact]
    public void DeriveMp3Path_FilenameContainingFlac_NotMangled()
    {
        // Filename has "FLAC" inside but the parent dir does not match. The
        // file is renamed only via its extension; the parent stays put.
        var flac = Path.Combine("D:\\Music", "Album", "01 The FLAC Song.flac");
        var mp3  = FfmpegConversionService.DeriveMp3Path(flac);
        var expected = Path.Combine("D:\\Music", "Album", "01 The FLAC Song.mp3");
        Assert.Equal(expected, mp3);
    }

    [Fact]
    public void DeriveMp3Path_ParentSegmentMatchedCaseInsensitively()
    {
        var flac = Path.Combine("D:\\CD archive", "Album", "flac", "01 Track.FLAC");
        var mp3  = FfmpegConversionService.DeriveMp3Path(flac);
        var expected = Path.Combine("D:\\CD archive", "Album", "MP3", "01 Track.mp3");
        Assert.Equal(expected, mp3);
    }

    [Fact]
    public void DeriveMp3Path_NoFlacSegment_PlacesMp3NextToSource()
    {
        var flac = Path.Combine("D:\\Stash", "loose-tracks", "01 Lonely.flac");
        var mp3  = FfmpegConversionService.DeriveMp3Path(flac);
        var expected = Path.Combine("D:\\Stash", "loose-tracks", "01 Lonely.mp3");
        Assert.Equal(expected, mp3);
    }

    // ---------------- ConvertFileAsync error paths ----------------

    [Fact]
    public async Task ConvertFileAsync_MissingSource_FailsWithoutSpawningProcess()
    {
        var settings = new FakeSettings { FfmpegPath = "definitely-not-a-real-ffmpeg-xyz123" };
        var svc = new FfmpegConversionService(settings, new FileSystemService());

        var flac = Path.Combine(_root, "nope.flac");
        var mp3  = Path.Combine(_root, "nope.mp3");

        var job = await svc.ConvertFileAsync(flac, mp3);

        Assert.Equal(ConversionStatus.Failed, job.Status);
        Assert.NotNull(job.ErrorMessage);
        Assert.Contains("Source file not found", job.ErrorMessage);
        Assert.False(File.Exists(mp3));
    }

    [Fact]
    public async Task ConvertFileAsync_MissingFfmpeg_FailsWithFriendlyMessageAndCleansUpPartial()
    {
        var settings = new FakeSettings { FfmpegPath = "definitely-not-a-real-ffmpeg-xyz123" };
        var svc = new FfmpegConversionService(settings, new FileSystemService());

        var flac = Path.Combine(_root, "in.flac");
        var mp3  = Path.Combine(_root, "out.mp3");
        File.WriteAllText(flac, "fake flac");
        // Simulate a leftover partial output from a prior failed run.
        File.WriteAllText(mp3, "partial");

        var job = await svc.ConvertFileAsync(flac, mp3);

        Assert.Equal(ConversionStatus.Failed, job.Status);
        Assert.NotNull(job.ErrorMessage);
        Assert.Contains("Could not launch ffmpeg", job.ErrorMessage);
        Assert.False(File.Exists(mp3));
    }

    [Fact]
    public async Task ConvertAlbumAsync_DerivesMp3PathsViaSegmentReplacement()
    {
        // End-to-end check that the album path computes job targets via the
        // segment-aware derivation rather than the old global Replace.
        var settings = new FakeSettings { FfmpegPath = "definitely-not-a-real-ffmpeg-xyz123" };
        var svc = new FfmpegConversionService(settings, new FileSystemService());

        var flacDir = Path.Combine(_root, "Album", "FLAC");
        Directory.CreateDirectory(flacDir);
        var flac = Path.Combine(flacDir, "01 Track.flac");
        File.WriteAllText(flac, "fake flac");

        var album = new AlbumInfo
        {
            Name = "Album",
            FullPath = Path.Combine(_root, "Album"),
            Discs =
            {
                new DiscInfo
                {
                    FullPath = Path.Combine(_root, "Album"),
                    FlacTracks = { new TrackInfo { FullPath = flac } }
                }
            }
        };

        var batch = await svc.ConvertAlbumAsync(album);

        var job = Assert.Single(batch.Jobs);
        var expectedMp3 = Path.Combine(_root, "Album", "MP3", "01 Track.mp3");
        Assert.Equal(expectedMp3, job.TargetMp3Path);
        // Conversion itself fails (no real ffmpeg) but the MP3 directory is
        // created and the target path is the segment-replaced one.
        Assert.Equal(ConversionStatus.Failed, job.Status);
        Assert.True(Directory.Exists(Path.Combine(_root, "Album", "MP3")));
    }
}
