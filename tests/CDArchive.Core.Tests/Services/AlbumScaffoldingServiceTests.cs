using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.Core.Tests.Services;

public class AlbumScaffoldingServiceTests
{
    private readonly IFileSystemService _fs;
    private readonly IArchiveSettings _settings;
    private readonly AlbumScaffoldingService _sut;

    public AlbumScaffoldingServiceTests()
    {
        _fs = Substitute.For<IFileSystemService>();
        _settings = Substitute.For<IArchiveSettings>();
        _settings.ArchiveRootPath.Returns(@"C:\Archive");

        _fs.CombinePath(Arg.Any<string[]>())
            .Returns(ci =>
            {
                var parts = ci.Arg<string[]>();
                return string.Join(@"\", parts);
            });

        _sut = new AlbumScaffoldingService(_settings, _fs);
    }

    #region GetDiscFolderName

    [Theory]
    [InlineData(1, 3, "Disc 1")]
    [InlineData(2, 9, "Disc 2")]
    [InlineData(5, 5, "Disc 5")]
    public void GetDiscFolderName_TotalDiscsUnder10_ReturnsUnpadded(int discNumber, int totalDiscs, string expected)
    {
        var result = _sut.GetDiscFolderName(discNumber, totalDiscs);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1, 10, "Disc 01")]
    [InlineData(3, 12, "Disc 03")]
    [InlineData(10, 10, "Disc 10")]
    [InlineData(1, 15, "Disc 01")]
    public void GetDiscFolderName_TotalDiscs10OrMore_ReturnsPadded(int discNumber, int totalDiscs, string expected)
    {
        var result = _sut.GetDiscFolderName(discNumber, totalDiscs);
        Assert.Equal(expected, result);
    }

    #endregion

    #region GetAlbumPath

    [Fact]
    public void GetAlbumPath_CombinesRootWithAlbumName()
    {
        var result = _sut.GetAlbumPath("My Album");
        Assert.Equal(@"C:\Archive\My Album", result);
    }

    #endregion

    #region CreateAlbumStructure

    [Fact]
    public void CreateAlbumStructure_SingleDisc_CreatesFlacAndMp3Folders()
    {
        _fs.DirectoryExists(Arg.Any<string>()).Returns(false);

        var result = _sut.CreateAlbumStructure("Test Album", 1);

        Assert.Equal("Test Album", result.Name);
        Assert.Equal(1, result.DiscCount);
        Assert.Single(result.Discs);

        var disc = result.Discs[0];
        Assert.Equal(1, disc.DiscNumber);
        Assert.True(disc.HasFlacFolder);
        Assert.True(disc.HasMp3Folder);

        _fs.Received(1).CreateDirectory(@"C:\Archive\Test Album\FLAC");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Test Album\MP3");
    }

    [Fact]
    public void CreateAlbumStructure_SingleDisc_CreatesAlbumDirectoryWhenNotExists()
    {
        _fs.DirectoryExists(Arg.Any<string>()).Returns(false);

        _sut.CreateAlbumStructure("Test Album", 1);

        _fs.Received(1).CreateDirectory(@"C:\Archive\Test Album");
    }

    [Fact]
    public void CreateAlbumStructure_SingleDisc_SkipsAlbumDirCreationWhenExists()
    {
        _fs.DirectoryExists(@"C:\Archive\Test Album").Returns(true);

        _sut.CreateAlbumStructure("Test Album", 1);

        // Should not call CreateDirectory for the album root itself
        _fs.DidNotReceive().CreateDirectory(@"C:\Archive\Test Album");
    }

    [Fact]
    public void CreateAlbumStructure_MultiDisc_CreatesDiscSubfoldersWithFlacAndMp3()
    {
        _fs.DirectoryExists(Arg.Any<string>()).Returns(false);

        var result = _sut.CreateAlbumStructure("Box Set", 3);

        Assert.Equal("Box Set", result.Name);
        Assert.Equal(3, result.DiscCount);
        Assert.Equal(3, result.Discs.Count);

        for (int i = 0; i < 3; i++)
        {
            var disc = result.Discs[i];
            Assert.Equal(i + 1, disc.DiscNumber);
            Assert.True(disc.HasFlacFolder);
            Assert.True(disc.HasMp3Folder);
            Assert.Equal($"Disc {i + 1}", disc.FolderName);
        }

        // Verify disc subfolder creation
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 1\FLAC");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 1\MP3");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 2\FLAC");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 2\MP3");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 3\FLAC");
        _fs.Received(1).CreateDirectory(@"C:\Archive\Box Set\Disc 3\MP3");
    }

    [Fact]
    public void CreateAlbumStructure_MultiDisc_UsesCorrectPaddingFor10PlusDiscs()
    {
        _fs.DirectoryExists(Arg.Any<string>()).Returns(false);

        var result = _sut.CreateAlbumStructure("Large Box Set", 10);

        Assert.Equal(10, result.Discs.Count);
        Assert.Equal("Disc 01", result.Discs[0].FolderName);
        Assert.Equal("Disc 10", result.Discs[9].FolderName);
    }

    #endregion
}
