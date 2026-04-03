using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.Core.Tests.Services;

public class ConversionStatusServiceTests
{
    private readonly IArchiveScannerService _scanner;
    private readonly ConversionStatusService _sut;

    public ConversionStatusServiceTests()
    {
        _scanner = Substitute.For<IArchiveScannerService>();
        _sut = new ConversionStatusService(_scanner);
    }

    [Fact]
    public async Task GetMissingConversionsAsync_FlacWithoutMp3_ReturnsMissing()
    {
        var album = new AlbumInfo
        {
            Name = "Test Album",
            FullPath = @"C:\Archive\Test Album",
            DiscCount = 1,
            Discs = new List<DiscInfo>
            {
                new DiscInfo
                {
                    DiscNumber = 1,
                    FolderName = "Test Album",
                    FullPath = @"C:\Archive\Test Album",
                    HasFlacFolder = true,
                    HasMp3Folder = true,
                    FlacTracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - Track One.flac", FullPath = @"C:\Archive\Test Album\FLAC\01 - Track One.flac", Format = AudioFormat.Flac },
                        new TrackInfo { FileName = "02 - Track Two.flac", FullPath = @"C:\Archive\Test Album\FLAC\02 - Track Two.flac", Format = AudioFormat.Flac },
                        new TrackInfo { FileName = "03 - Track Three.flac", FullPath = @"C:\Archive\Test Album\FLAC\03 - Track Three.flac", Format = AudioFormat.Flac }
                    },
                    Mp3Tracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - Track One.mp3", FullPath = @"C:\Archive\Test Album\MP3\01 - Track One.mp3", Format = AudioFormat.Mp3 }
                    }
                }
            }
        };

        _scanner.ScanArchiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlbumInfo> { album });

        var results = await _sut.GetMissingConversionsAsync();

        Assert.Single(results);
        var (resultAlbum, resultDisc, missingMp3s) = results[0];
        Assert.Equal("Test Album", resultAlbum.Name);
        Assert.Equal(2, missingMp3s.Count);
        Assert.Contains("02 - Track Two.flac", missingMp3s);
        Assert.Contains("03 - Track Three.flac", missingMp3s);
    }

    [Fact]
    public async Task GetMissingConversionsAsync_AllConversionsPresent_ReturnsEmpty()
    {
        var album = new AlbumInfo
        {
            Name = "Complete Album",
            FullPath = @"C:\Archive\Complete Album",
            DiscCount = 1,
            Discs = new List<DiscInfo>
            {
                new DiscInfo
                {
                    DiscNumber = 1,
                    FolderName = "Complete Album",
                    FullPath = @"C:\Archive\Complete Album",
                    HasFlacFolder = true,
                    HasMp3Folder = true,
                    FlacTracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - Song.flac", FullPath = @"C:\Archive\Complete Album\FLAC\01 - Song.flac", Format = AudioFormat.Flac },
                        new TrackInfo { FileName = "02 - Song.flac", FullPath = @"C:\Archive\Complete Album\FLAC\02 - Song.flac", Format = AudioFormat.Flac }
                    },
                    Mp3Tracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - Song.mp3", FullPath = @"C:\Archive\Complete Album\MP3\01 - Song.mp3", Format = AudioFormat.Mp3 },
                        new TrackInfo { FileName = "02 - Song.mp3", FullPath = @"C:\Archive\Complete Album\MP3\02 - Song.mp3", Format = AudioFormat.Mp3 }
                    }
                }
            }
        };

        _scanner.ScanArchiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlbumInfo> { album });

        var results = await _sut.GetMissingConversionsAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetMissingConversionsAsync_NoFlacTracks_ReturnsEmpty()
    {
        var album = new AlbumInfo
        {
            Name = "Mp3 Only",
            FullPath = @"C:\Archive\Mp3 Only",
            DiscCount = 1,
            Discs = new List<DiscInfo>
            {
                new DiscInfo
                {
                    DiscNumber = 1,
                    FolderName = "Mp3 Only",
                    FullPath = @"C:\Archive\Mp3 Only",
                    HasMp3Folder = true,
                    Mp3Tracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - Song.mp3", FullPath = @"C:\Archive\Mp3 Only\MP3\01 - Song.mp3", Format = AudioFormat.Mp3 }
                    }
                }
            }
        };

        _scanner.ScanArchiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlbumInfo> { album });

        var results = await _sut.GetMissingConversionsAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetMissingConversionsAsync_MultiDisc_ReportsMissingPerDisc()
    {
        var album = new AlbumInfo
        {
            Name = "Multi Disc Album",
            FullPath = @"C:\Archive\Multi Disc Album",
            DiscCount = 2,
            Discs = new List<DiscInfo>
            {
                new DiscInfo
                {
                    DiscNumber = 1,
                    FolderName = "Disc 1",
                    FullPath = @"C:\Archive\Multi Disc Album\Disc 1",
                    HasFlacFolder = true,
                    HasMp3Folder = true,
                    FlacTracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - A.flac", Format = AudioFormat.Flac }
                    },
                    Mp3Tracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - A.mp3", Format = AudioFormat.Mp3 }
                    }
                },
                new DiscInfo
                {
                    DiscNumber = 2,
                    FolderName = "Disc 2",
                    FullPath = @"C:\Archive\Multi Disc Album\Disc 2",
                    HasFlacFolder = true,
                    HasMp3Folder = true,
                    FlacTracks = new List<TrackInfo>
                    {
                        new TrackInfo { FileName = "01 - B.flac", Format = AudioFormat.Flac }
                    },
                    Mp3Tracks = new List<TrackInfo>() // No MP3s for disc 2
                }
            }
        };

        _scanner.ScanArchiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlbumInfo> { album });

        var results = await _sut.GetMissingConversionsAsync();

        Assert.Single(results); // Only disc 2 has missing conversions
        Assert.Equal(2, results[0].Disc.DiscNumber);
        Assert.Single(results[0].MissingMp3s);
        Assert.Contains("01 - B.flac", results[0].MissingMp3s);
    }
}
