using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Rework H30 regression: <see cref="PickListKinds"/> replaces a positional
/// dispatch in <c>PickListsViewModel</c> (<c>SelectedListIndex == 6</c> for
/// Ensembles, a <c>switch</c> on <c>SelectedListIndex</c> for the
/// string-list dispatch). The display order in <see cref="PickListKinds.OrderedKinds"/>
/// is now the only place that knows about positions; reordering it must
/// leave routing and predicate semantics unchanged.
/// </summary>
public class PickListKindsTests
{
    [Fact]
    public void OrderedKinds_ContainsEveryEnumValue_NoDuplicates()
    {
        var ordered = PickListKinds.OrderedKinds.ToList();
        var enumValues = Enum.GetValues<PickListKind>().ToList();

        Assert.Equal(enumValues.Count, ordered.Count);
        Assert.Equal(enumValues.OrderBy(k => k).ToList(), ordered.OrderBy(k => k).ToList());
    }

    [Fact]
    public void OrderedDisplayNames_MatchOrderedKinds()
    {
        var expected = PickListKinds.OrderedKinds.Select(PickListKinds.DisplayName).ToList();
        Assert.Equal(expected, PickListKinds.OrderedDisplayNames);
    }

    [Theory]
    [InlineData(PickListKind.Forms,          "Forms")]
    [InlineData(PickListKind.Categories,     "Categories")]
    [InlineData(PickListKind.Catalogues,     "Catalogues")]
    [InlineData(PickListKind.Keys,           "Keys")]
    [InlineData(PickListKind.Instruments,    "Instruments")]
    [InlineData(PickListKind.CreativeRoles,  "Creative Roles")]
    [InlineData(PickListKind.Ensembles,      "Ensembles")]
    [InlineData(PickListKind.VoiceTypes,     "Voice Types")]
    [InlineData(PickListKind.PerformerRoles, "Performer Roles")]
    [InlineData(PickListKind.Labels,         "Labels")]
    public void DisplayName_ReturnsExpectedLabel(PickListKind kind, string expected)
    {
        Assert.Equal(expected, PickListKinds.DisplayName(kind));
    }

    [Fact]
    public void KindAt_RoundTripsThroughIndexOf()
    {
        for (int i = 0; i < PickListKinds.OrderedKinds.Count; i++)
        {
            var kind = PickListKinds.KindAt(i);
            Assert.Equal(i, PickListKinds.IndexOf(kind));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void KindAt_RejectsOutOfRange(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PickListKinds.KindAt(index));
    }

    [Fact]
    public void IsStringList_OnlyEnsemblesReturnsFalse()
    {
        foreach (var kind in Enum.GetValues<PickListKind>())
        {
            var expected = kind != PickListKind.Ensembles;
            Assert.Equal(expected, PickListKinds.IsStringList(kind));
        }
    }

    [Theory]
    [InlineData(PickListKind.Forms,          true)]
    [InlineData(PickListKind.Categories,     true)]
    [InlineData(PickListKind.Catalogues,     true)]
    [InlineData(PickListKind.Keys,           true)]
    [InlineData(PickListKind.Instruments,    false)]
    [InlineData(PickListKind.CreativeRoles,  false)]
    [InlineData(PickListKind.Ensembles,      false)]
    [InlineData(PickListKind.VoiceTypes,     false)]
    [InlineData(PickListKind.PerformerRoles, false)]
    [InlineData(PickListKind.Labels,         false)]
    public void IsRenamable_TracksTheFourPieceFieldKinds(PickListKind kind, bool expected)
    {
        Assert.Equal(expected, PickListKinds.IsRenamable(kind));
    }

    /// <summary>
    /// Pin the current default display order. Re-pinning is fine when the
    /// UX intentionally reshuffles; the test exists so the change is
    /// deliberate (and the diff documents intent).
    /// </summary>
    [Fact]
    public void OrderedKinds_DefaultDisplayOrder_IsPinned()
    {
        Assert.Equal(new[]
        {
            PickListKind.Forms,
            PickListKind.Categories,
            PickListKind.Catalogues,
            PickListKind.Keys,
            PickListKind.Instruments,
            PickListKind.CreativeRoles,
            PickListKind.Ensembles,
            PickListKind.VoiceTypes,
            PickListKind.PerformerRoles,
            PickListKind.Labels,
        }, PickListKinds.OrderedKinds);
    }
}
