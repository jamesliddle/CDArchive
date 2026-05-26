using CDArchive.App.Helpers;

namespace CDArchive.App.Tests.Helpers;

/// <summary>
/// H13 (slice 1): <see cref="MixedField{T}"/> is the VM-side tri-state
/// wrapper that replaced the editor's <c>HashSet&lt;string&gt; _mixedFields</c>
/// plus direct TextBox.Text manipulation.
/// </summary>
public class MixedFieldTests
{
    [Fact]
    public void InitUnanimous_SetsValue_AndClearsIsMixed_AndWasEdited()
    {
        var f = new MixedField<string>();
        f.InitUnanimous("hello");

        Assert.Equal("hello", f.Value);
        Assert.False(f.IsMixed);
        Assert.False(f.WasEdited);
    }

    [Fact]
    public void InitMixed_SetsPlaceholder_AndIsMixed_AndClearsWasEdited()
    {
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");

        Assert.Equal("(Mixed)", f.Value);
        Assert.True(f.IsMixed);
        Assert.False(f.WasEdited);
    }

    [Fact]
    public void Init_DoesNotTripWasEdited_EvenWhenValueChanges()
    {
        // Regression: Init* methods set Value internally. The OnValueChanged
        // partial-method hook would naturally trip WasEdited; the
        // _initializing flag suppresses that. Re-initialising must always
        // leave WasEdited false.
        var f = new MixedField<string>();
        f.InitUnanimous("first");
        f.Value = "user typed this";
        Assert.True(f.WasEdited);   // user typing trips it

        f.InitUnanimous("re-loaded");
        Assert.False(f.WasEdited);  // re-init resets it
        Assert.Equal("re-loaded", f.Value);
    }

    [Fact]
    public void UserEdit_AfterInitMixed_ClearsIsMixed_AndSetsWasEdited()
    {
        // Simulates the MixedPlaceholder.WireClearBehavior flow: load Mixed,
        // user clears the placeholder, types something new.
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");

        f.Value = "";                       // MixedPlaceholder clear sets Text=""
        Assert.False(f.IsMixed);            // placeholder dismissed
        Assert.True(f.WasEdited);

        f.Value = "user typed this";        // subsequent keystrokes
        Assert.True(f.WasEdited);
        Assert.False(f.IsMixed);
    }

    [Fact]
    public void UserEdit_AfterInitUnanimous_TripsWasEdited()
    {
        var f = new MixedField<string>();
        f.InitUnanimous("loaded");

        f.Value = "edited";

        Assert.True(f.WasEdited);
        Assert.False(f.IsMixed);   // never was mixed in this scenario
    }

    [Fact]
    public void IsMixed_StaysTrue_UntilUserEdits()
    {
        // The placeholder must NOT auto-clear just because we read Value or
        // anything else benign happens. It only clears on a real value
        // change (which only happens on user edit because we're inside Init*
        // when the load happens).
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");

        // Read value, observe properties — none of this should clear IsMixed.
        _ = f.Value;
        _ = f.IsMixed;
        _ = f.WasEdited;

        Assert.True(f.IsMixed);
        Assert.False(f.WasEdited);
    }

    // ── H13 slice 4: StartedMixed property ───────────────────────────────────

    [Fact]
    public void InitUnanimous_SetsStartedMixedFalse()
    {
        var f = new MixedField<string>();
        f.InitUnanimous("hi");
        Assert.False(f.StartedMixed);
    }

    [Fact]
    public void InitMixed_SetsStartedMixedTrue()
    {
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");
        Assert.True(f.StartedMixed);
    }

    [Fact]
    public void StartedMixed_StaysTrue_AfterUserEdits()
    {
        // This is the whole point: the editor's SaveMulti needs to know "did
        // this field start Mixed?" even after the user dismissed the
        // placeholder and typed something. IsMixed clears on edit; StartedMixed
        // must not.
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");
        f.Value = "user typed this";

        Assert.False(f.IsMixed);
        Assert.True(f.StartedMixed);
        Assert.True(f.WasEdited);
    }

    [Fact]
    public void StartedMixed_ResetsToFalse_OnInitUnanimousAfterMixed()
    {
        // Re-initialising must reset StartedMixed — important for the
        // theoretical re-show flow where the same VM is reused across loads.
        var f = new MixedField<string>();
        f.InitMixed("(Mixed)");
        Assert.True(f.StartedMixed);

        f.InitUnanimous("re-loaded");
        Assert.False(f.StartedMixed);
    }

    [Fact]
    public void StartedMixed_StaysFalse_OnUnanimousFieldThatUserEdits()
    {
        var f = new MixedField<string>();
        f.InitUnanimous("loaded");
        f.Value = "user edit";

        Assert.False(f.StartedMixed);
        Assert.True(f.WasEdited);
    }
}
