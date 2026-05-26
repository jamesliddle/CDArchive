using CDArchive.App.Helpers;

namespace CDArchive.App.Tests.Helpers;

/// <summary>
/// H13 TrackEditor (slice 4): <see cref="MixedCollection{T}"/> is the
/// list-shaped equivalent of <see cref="MixedField{T}"/>. Same StartedMixed +
/// WasEdited tri-state contract, but for an
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// rather than a scalar.
/// </summary>
public class MixedCollectionTests
{
    [Fact]
    public void InitUnanimous_PopulatesItems_AndClearsFlags()
    {
        var c = new MixedCollection<string>();
        c.InitUnanimous(new[] { "a", "b", "c" });

        Assert.Equal(new[] { "a", "b", "c" }, c.Items);
        Assert.False(c.StartedMixed);
        Assert.False(c.WasEdited);
    }

    [Fact]
    public void InitMixed_ClearsItems_AndSetsStartedMixed_LeavesWasEditedFalse()
    {
        var c = new MixedCollection<string>();
        c.InitUnanimous(new[] { "old" });
        c.InitMixed();

        Assert.Empty(c.Items);
        Assert.True(c.StartedMixed);
        Assert.False(c.WasEdited);
    }

    [Fact]
    public void Init_DoesNotTripWasEdited_EvenWhenCollectionChanges()
    {
        // Regression: Init* methods Clear + Add to Items. Each Add raises
        // CollectionChanged, which would naturally trip WasEdited; the
        // _initializing flag suppresses that. Re-initialising must always
        // leave WasEdited false.
        var c = new MixedCollection<int>();
        c.InitUnanimous(new[] { 1, 2, 3 });
        c.Items.Add(4);
        Assert.True(c.WasEdited);   // user mutation trips it

        c.InitUnanimous(new[] { 10, 20 });
        Assert.False(c.WasEdited);  // re-init resets it
        Assert.Equal(new[] { 10, 20 }, c.Items);
    }

    [Fact]
    public void UserAdd_AfterInitMixed_FlipsWasEdited_AndKeepsStartedMixed()
    {
        var c = new MixedCollection<string>();
        c.InitMixed();

        c.Items.Add("new entry");

        Assert.True(c.WasEdited);
        Assert.True(c.StartedMixed);   // history bit stays
    }

    [Fact]
    public void UserAdd_AfterInitUnanimous_TripsWasEdited()
    {
        var c = new MixedCollection<string>();
        c.InitUnanimous(new[] { "a" });

        c.Items.Add("b");

        Assert.True(c.WasEdited);
        Assert.False(c.StartedMixed);
    }

    [Fact]
    public void UserRemove_FlipsWasEdited()
    {
        var c = new MixedCollection<string>();
        c.InitUnanimous(new[] { "a", "b" });

        c.Items.Remove("a");

        Assert.True(c.WasEdited);
    }

    [Fact]
    public void UserReplaceViaIndexer_FlipsWasEdited()
    {
        // The PieceRefs ListBox's "Details…" path mutates via indexer to
        // trigger a Replace event; that should count as a user edit too.
        var c = new MixedCollection<string>();
        c.InitUnanimous(new[] { "old" });

        c.Items[0] = "new";

        Assert.True(c.WasEdited);
    }

    [Fact]
    public void ShouldWriteOnSave_Unanimous_AlwaysTrue()
    {
        var c = new MixedCollection<int>();
        c.InitUnanimous(new[] { 1, 2, 3 });
        Assert.True(c.ShouldWriteOnSave);   // unanimous, untouched
        c.Items.Add(4);
        Assert.True(c.ShouldWriteOnSave);   // unanimous, touched
    }

    [Fact]
    public void ShouldWriteOnSave_Mixed_FalseWhenUntouched_TrueWhenTouched()
    {
        // The contract this whole class exists for: was-Mixed + untouched
        // means save must NOT wipe each track's list.
        var c = new MixedCollection<int>();
        c.InitMixed();
        Assert.False(c.ShouldWriteOnSave);   // Mixed, untouched → skip

        c.Items.Add(7);
        Assert.True(c.ShouldWriteOnSave);    // Mixed, touched → write
    }

    [Fact]
    public void WasEditedDoesNotFlipMultipleTimes_PropertyChangedFiresOnceOnly()
    {
        // Internal optimisation: if !WasEdited then set WasEdited = true on
        // the first mutation. Subsequent mutations should not re-raise
        // PropertyChanged for WasEdited (already true).
        var c = new MixedCollection<int>();
        c.InitMixed();
        var count = 0;
        c.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MixedCollection<int>.WasEdited)) count++;
        };

        c.Items.Add(1);
        c.Items.Add(2);
        c.Items.Add(3);

        Assert.Equal(1, count);   // PropertyChanged for WasEdited fires exactly once
        Assert.True(c.WasEdited);
    }
}
