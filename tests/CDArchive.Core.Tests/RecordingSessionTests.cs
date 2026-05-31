using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Display-summary tests for <see cref="RecordingSession"/>. Pins the
/// Engineers / Producers / Location / Display summary getters used by the
/// Sessions tab in <c>AlbumEditorWindow.xaml</c>. The Engineers column
/// previously bound to <c>DisplaySummary</c> by mistake — these tests pin
/// the three summaries as distinct so a future re-binding regression is
/// caught.
/// </summary>
public class RecordingSessionTests
{
    // ── EngineersSummary ─────────────────────────────────────────────────

    [Fact]
    public void EngineersSummary_NullList_IsEmpty()
    {
        var s = new RecordingSession();
        Assert.Equal(string.Empty, s.EngineersSummary);
    }

    [Fact]
    public void EngineersSummary_EmptyList_IsEmpty()
    {
        var s = new RecordingSession { Engineers = new List<string>() };
        Assert.Equal(string.Empty, s.EngineersSummary);
    }

    [Fact]
    public void EngineersSummary_SingleEntry_ReturnsName()
    {
        var s = new RecordingSession { Engineers = new List<string> { "Kenneth Wilkinson" } };
        Assert.Equal("Kenneth Wilkinson", s.EngineersSummary);
    }

    [Fact]
    public void EngineersSummary_MultipleEntries_JoinedWithCommaSpace()
    {
        var s = new RecordingSession
        {
            Engineers = new List<string> { "Kenneth Wilkinson", "James Lock", "Colin Moorfoot" },
        };
        Assert.Equal("Kenneth Wilkinson, James Lock, Colin Moorfoot", s.EngineersSummary);
    }

    [Fact]
    public void EngineersSummary_SkipsNullAndWhitespaceEntries()
    {
        var s = new RecordingSession
        {
            Engineers = new List<string> { "Kenneth Wilkinson", "", "  ", null!, "James Lock" },
        };
        Assert.Equal("Kenneth Wilkinson, James Lock", s.EngineersSummary);
    }

    // ── ProducersSummary ─────────────────────────────────────────────────

    [Fact]
    public void ProducersSummary_NullList_IsEmpty()
    {
        var s = new RecordingSession();
        Assert.Equal(string.Empty, s.ProducersSummary);
    }

    [Fact]
    public void ProducersSummary_EmptyList_IsEmpty()
    {
        var s = new RecordingSession { Producers = new List<string>() };
        Assert.Equal(string.Empty, s.ProducersSummary);
    }

    [Fact]
    public void ProducersSummary_MultipleEntries_JoinedWithCommaSpace()
    {
        var s = new RecordingSession
        {
            Producers = new List<string> { "John Culshaw", "Christopher Raeburn" },
        };
        Assert.Equal("John Culshaw, Christopher Raeburn", s.ProducersSummary);
    }

    [Fact]
    public void ProducersSummary_SkipsNullAndWhitespaceEntries()
    {
        var s = new RecordingSession
        {
            Producers = new List<string> { "  ", "John Culshaw", null!, "" },
        };
        Assert.Equal("John Culshaw", s.ProducersSummary);
    }

    // ── LocationSummary ──────────────────────────────────────────────────

    [Fact]
    public void LocationSummary_AllPartsPresent_JoinsWithCommaSpace()
    {
        var s = new RecordingSession
        {
            Venue   = "Sofiensaal",
            City    = "Vienna",
            Country = "Austria",
        };
        Assert.Equal("Sofiensaal, Vienna, Austria", s.LocationSummary);
    }

    [Fact]
    public void LocationSummary_IncludesStateBetweenCityAndCountry()
    {
        var s = new RecordingSession
        {
            Venue   = "Skywalker Sound",
            City    = "Marin County",
            State   = "California",
            Country = "USA",
        };
        Assert.Equal("Skywalker Sound, Marin County, California, USA", s.LocationSummary);
    }

    [Fact]
    public void LocationSummary_StateOnly_StillRenders()
    {
        var s = new RecordingSession { State = "Bavaria" };
        Assert.Equal("Bavaria", s.LocationSummary);
    }

    [Fact]
    public void LocationSummary_PartialParts_OmitsMissing()
    {
        var s = new RecordingSession { City = "Vienna", Country = "Austria" };
        Assert.Equal("Vienna, Austria", s.LocationSummary);
    }

    [Fact]
    public void LocationSummary_AllMissing_IsEmpty()
    {
        var s = new RecordingSession();
        Assert.Equal(string.Empty, s.LocationSummary);
    }

    [Fact]
    public void LocationSummary_WhitespaceOnly_IsTreatedAsMissing()
    {
        var s = new RecordingSession { Venue = "  ", City = "Vienna", Country = "" };
        Assert.Equal("Vienna", s.LocationSummary);
    }

    // ── DisplaySummary ───────────────────────────────────────────────────

    [Fact]
    public void DisplaySummary_DatesAndLocation_JoinedWithMiddot()
    {
        var s = new RecordingSession
        {
            Dates = "March 3–7, 1967",
            Venue = "Sofiensaal",
            City  = "Vienna",
        };
        Assert.Equal("March 3–7, 1967 · Sofiensaal, Vienna", s.DisplaySummary);
    }

    [Fact]
    public void DisplaySummary_DatesOnly_NoTrailingSeparator()
    {
        var s = new RecordingSession { Dates = "1967" };
        Assert.Equal("1967", s.DisplaySummary);
    }

    [Fact]
    public void DisplaySummary_LocationOnly_NoLeadingSeparator()
    {
        var s = new RecordingSession { Venue = "Sofiensaal", City = "Vienna" };
        Assert.Equal("Sofiensaal, Vienna", s.DisplaySummary);
    }

    [Fact]
    public void DisplaySummary_NothingSet_ReturnsPlaceholder()
    {
        var s = new RecordingSession();
        Assert.Equal("(no session details)", s.DisplaySummary);
    }

    // ── Cross-property invariant ─────────────────────────────────────────

    [Fact]
    public void DisplaySummary_DoesNotIncludeEngineersOrProducers()
    {
        // Pins the original bug: the Sessions tab's Engineers column used to
        // bind to DisplaySummary, which doesn't carry engineer/producer data
        // at all. Even with engineers and producers set, DisplaySummary
        // must remain dates + location only.
        var s = new RecordingSession
        {
            Dates     = "1967",
            Venue     = "Sofiensaal",
            Engineers = new List<string> { "Kenneth Wilkinson" },
            Producers = new List<string> { "John Culshaw" },
        };
        Assert.DoesNotContain("Wilkinson", s.DisplaySummary);
        Assert.DoesNotContain("Culshaw",   s.DisplaySummary);
        Assert.Equal("Kenneth Wilkinson", s.EngineersSummary);
        Assert.Equal("John Culshaw",      s.ProducersSummary);
    }
}
