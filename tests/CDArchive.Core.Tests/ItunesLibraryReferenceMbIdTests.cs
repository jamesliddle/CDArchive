using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Slice 6: MBID-shortcut tests for <see cref="ItunesLibraryReference.ParseMbIdsFromComments"/>.
/// Picard writes MusicBrainz tags to file metadata; iTunes surfaces them
/// via the Comments field. The parser pulls release / recording / work
/// MBIDs from the Comments so the planner can skip MB search entirely.
/// </summary>
public class ItunesLibraryReferenceMbIdTests
{
    [Fact]
    public void Empty_Comments_AllNulls()
    {
        var (r, rc, w) = ItunesLibraryReference.ParseMbIdsFromComments("");
        Assert.Null(r); Assert.Null(rc); Assert.Null(w);
    }

    [Fact]
    public void Null_Comments_AllNulls()
    {
        var (r, rc, w) = ItunesLibraryReference.ParseMbIdsFromComments(null);
        Assert.Null(r); Assert.Null(rc); Assert.Null(w);
    }

    [Fact]
    public void PicardStyleComments_AllThreeParsed()
    {
        const string body = """
            MusicBrainz Album Id: 3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e
            MusicBrainz Track Id: 4a0b1c22-aaaa-bbbb-cccc-d2e93b9f7d2c
            MusicBrainz Work Id: 8c6a91a3-9f29-4bba-a8f5-d2e93b9f7d2c
            """;
        var (r, rc, w) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Equal("3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e", r);
        Assert.Equal("4a0b1c22-aaaa-bbbb-cccc-d2e93b9f7d2c", rc);
        Assert.Equal("8c6a91a3-9f29-4bba-a8f5-d2e93b9f7d2c", w);
    }

    [Fact]
    public void ReleaseIdAlias_AcceptedToo()
    {
        // Some tag editors emit "Release Id" instead of Picard's "Album Id".
        const string body = "MusicBrainz Release Id: 3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e";
        var (r, _, _) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Equal("3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e", r);
    }

    [Fact]
    public void RecordingIdAlias_AcceptedToo()
    {
        const string body = "MusicBrainz Recording Id: 4a0b1c22-aaaa-bbbb-cccc-d2e93b9f7d2c";
        var (_, rc, _) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Equal("4a0b1c22-aaaa-bbbb-cccc-d2e93b9f7d2c", rc);
    }

    [Fact]
    public void NoMbidInLine_LineSkipped()
    {
        // Comments may contain unrelated text; only lines carrying a UUID
        // contribute to the parse.
        const string body = """
            User-added note about this recording.
            MusicBrainz Album Id: 3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e
            Mood: cheerful
            """;
        var (r, _, _) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Equal("3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e", r);
    }

    [Fact]
    public void MalformedUuid_NotParsed()
    {
        // 35 chars (one short of a real MBID). Pattern requires the exact
        // 8-4-4-4-12 hex shape with dashes.
        const string body = "MusicBrainz Album Id: 3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8";
        var (r, _, _) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Null(r);
    }

    [Fact]
    public void UppercaseUuid_NormalisedToLowercase()
    {
        // MB IDs are canonically lowercase; the parser normalises.
        const string body = "MusicBrainz Album Id: 3E0C2F88-9BF9-4F4A-BD95-B6F9B87D3A8E";
        var (r, _, _) = ItunesLibraryReference.ParseMbIdsFromComments(body);
        Assert.Equal("3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e", r);
    }
}
