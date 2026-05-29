using System.Text.Json;
using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// User-reported bug: renaming an instrument in the Pick Lists editor left
/// pieces still pointing at the old name. Pick lists' rename machinery
/// previously only covered Forms / Categories / Catalogues / Keys (the
/// renamable kinds gated by <c>PickListKinds.IsRenamable</c>).
/// <see cref="InstrumentationRenamer"/> walks the piece's flexible
/// <c>Instrumentation</c> JSON and rewrites every matching instrument
/// name in every supported shape.
/// </summary>
public class InstrumentationRenamerTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string Json(JsonElement? element) =>
        element is null ? "" : element.Value.GetRawText();

    private static IReadOnlyDictionary<string, string> Renames(params (string From, string To)[] pairs) =>
        pairs.ToDictionary(p => p.From, p => p.To);

    [Fact]
    public void NullSource_ReturnsNull_WithZeroCount()
    {
        var result = InstrumentationRenamer.ApplyRenames(null, Renames(("a", "b")), out var count);
        Assert.Null(result);
        Assert.Equal(0, count);
    }

    [Fact]
    public void EmptyRenames_ReturnsInputUnchanged_WithZeroCount()
    {
        var src = Parse("""["piano", "violin"]""");
        var result = InstrumentationRenamer.ApplyRenames(src, new Dictionary<string, string>(), out var count);

        Assert.Equal(src.GetRawText(), Json(result));
        Assert.Equal(0, count);
    }

    [Fact]
    public void NoMatch_ReturnsInputUnchanged_WithZeroCount()
    {
        var src = Parse("""["piano", "violin"]""");
        var result = InstrumentationRenamer.ApplyRenames(src, Renames(("trombone", "horn")), out var count);

        Assert.Equal(src.GetRawText(), Json(result));
        Assert.Equal(0, count);
    }

    [Fact]
    public void TopLevelStringElements_AreRenamed()
    {
        var src = Parse("""["piano", "violin", "cello"]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("piano", "fortepiano")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("fortepiano", Json(result));
        Assert.DoesNotContain("\"piano\"", Json(result));
    }

    [Fact]
    public void InstrumentField_OnObject_IsRenamed()
    {
        var src = Parse("""[{"instrument": "clarinet", "key": "B-flat"}]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("clarinet", "clarinet in B-flat")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("clarinet in B-flat", Json(result));
    }

    [Fact]
    public void AlternateInstrumentField_IsRenamed()
    {
        var src = Parse("""[{"instrument": "horn", "alternate_instrument": "cornetto"}]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("cornetto", "cornet")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("cornet", Json(result));
    }

    [Fact]
    public void SectionField_IsRenamed()
    {
        var src = Parse("""[{"section": "violin", "number": 1}]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("violin", "violino")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("violino", Json(result));
    }

    [Fact]
    public void OrchestraArrayMembers_AreRenamed()
    {
        var src = Parse("""[{"orchestra": ["flute", "oboe", "clarinet"]}]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("oboe", "hautbois")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("hautbois", Json(result));
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var src = Parse("""["Piano"]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("piano", "Fortepiano")), out var count);

        Assert.Equal(1, count);
        Assert.Contains("Fortepiano", Json(result));
    }

    [Fact]
    public void MultipleOccurrences_AllRenamed()
    {
        // "violin" appears 3 times (top-level + section + orchestra).
        var src = Parse("""
            [
              "violin",
              {"section": "violin", "number": 1},
              {"orchestra": ["violin", "cello"]}
            ]
            """);
        var result = InstrumentationRenamer.ApplyRenames(
            src, Renames(("violin", "violino")), out var count);

        Assert.Equal(3, count);
        Assert.DoesNotContain("\"violin\"", Json(result));
    }

    [Fact]
    public void MultipleRenames_InOneCall()
    {
        var src = Parse("""["piano", "clarinet"]""");
        var result = InstrumentationRenamer.ApplyRenames(
            src,
            Renames(
                ("piano",    "fortepiano"),
                ("clarinet", "clarinet in B-flat")),
            out var count);

        Assert.Equal(2, count);
        Assert.Contains("fortepiano", Json(result));
        Assert.Contains("clarinet in B-flat", Json(result));
    }
}
