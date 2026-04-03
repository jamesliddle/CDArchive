using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.Core.Tests.Services;

public class DuplicateDetectionServiceTests
{
    private readonly IFileSystemService _fs;
    private readonly IArchiveSettings _settings;
    private readonly DuplicateDetectionService _sut;
    private const string ArchiveRoot = @"C:\Archive";

    public DuplicateDetectionServiceTests()
    {
        _fs = Substitute.For<IFileSystemService>();
        _settings = Substitute.For<IArchiveSettings>();
        _settings.ArchiveRootPath.Returns(ArchiveRoot);

        _fs.DirectoryExists(ArchiveRoot).Returns(true);

        _fs.CombinePath(Arg.Any<string[]>())
            .Returns(ci =>
            {
                var parts = ci.Arg<string[]>();
                return string.Join(@"\", parts);
            });

        _fs.GetFileName(Arg.Any<string>())
            .Returns(ci =>
            {
                var path = ci.Arg<string>();
                return path.Split('\\').Last();
            });

        _sut = new DuplicateDetectionService(_settings, _fs);
    }

    [Fact]
    public void FindPotentialDuplicates_ExactNormalizedMatch_IsDetected()
    {
        // "Abbey Road" and "Abbey-Road" both normalize to "abbey road"
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Abbey-Road"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Single(results);
        Assert.Equal(@"C:\Archive\Abbey-Road", results[0]);
    }

    [Fact]
    public void FindPotentialDuplicates_SubstringContainment_IsDetected()
    {
        // "Abbey Road" is contained in "Abbey Road Remaster"
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Abbey Road Remaster"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Single(results);
    }

    [Fact]
    public void FindPotentialDuplicates_CloseLevenshteinMatch_IsDetected()
    {
        // "Abby Road" vs "Abbey Road" - Levenshtein distance of 1
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Abby Road"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Single(results);
    }

    [Fact]
    public void FindPotentialDuplicates_UnrelatedNames_ReturnsEmpty()
    {
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Dark Side of the Moon"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Empty(results);
    }

    [Fact]
    public void FindPotentialDuplicates_NoDirectories_ReturnsEmpty()
    {
        _fs.EnumerateDirectories(ArchiveRoot).Returns(Array.Empty<string>());

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Empty(results);
    }

    [Fact]
    public void FindPotentialDuplicates_ArchiveRootDoesNotExist_ReturnsEmpty()
    {
        _fs.DirectoryExists(ArchiveRoot).Returns(false);

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Empty(results);
    }

    [Fact]
    public void FindPotentialDuplicates_ExcludesSelfFromResults()
    {
        // The album's own path should be excluded
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Abbey Road"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Empty(results);
    }

    [Fact]
    public void FindPotentialDuplicates_PunctuationIgnored_MatchesNormalized()
    {
        // "Abbey Road!" normalizes to "abbey road", same as "Abbey Road"
        _fs.EnumerateDirectories(ArchiveRoot).Returns(new[]
        {
            @"C:\Archive\Abbey Road!"
        });

        var results = _sut.FindPotentialDuplicates("Abbey Road");

        Assert.Single(results);
    }
}
