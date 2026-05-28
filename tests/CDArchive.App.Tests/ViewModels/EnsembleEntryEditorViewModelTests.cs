using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 small-editors (slice 4): <see cref="EnsembleEntryEditorViewModel"/>
/// owns ONLY the Members list of an ensemble instrument entry. The parent's
/// Instrument name and IsEnsemble flag are exposed read-only (the editor's
/// scope is "manage members", nothing else). H31 + H33 contract: mutate-in-
/// place on save; never touch Instrument or IsEnsemble.
/// </summary>
public class EnsembleEntryEditorViewModelTests
{
    [Fact]
    public void LoadFromEntry_PopulatesEnsembleNameAndMembers()
    {
        var ensemble = new InstrumentEntry
        {
            Instrument = "string quartet",
            IsEnsemble = true,
            Members    = new List<InstrumentEntry>
            {
                new() { Instrument = "violin 1" },
                new() { Instrument = "violin 2" },
                new() { Instrument = "viola"    },
                new() { Instrument = "cello"    },
            },
        };

        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(ensemble);

        Assert.Equal("string quartet", vm.EnsembleName);
        Assert.Equal(4, vm.Members.Count);
        Assert.Equal("violin 1", vm.Members[0].Instrument);
        Assert.Equal("cello",    vm.Members[3].Instrument);
    }

    [Fact]
    public void LoadFromEntry_NullMembers_LeaveCollectionEmpty()
    {
        var ensemble = new InstrumentEntry { Instrument = "ensemble", IsEnsemble = true };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(ensemble);

        Assert.Equal("ensemble", vm.EnsembleName);
        Assert.Empty(vm.Members);
    }

    [Fact]
    public void LoadFromEntry_ReHydrate_Replaces_NotAppends()
    {
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(new InstrumentEntry
        {
            Instrument = "first",
            Members = new List<InstrumentEntry> { new() { Instrument = "old1" } },
        });
        Assert.Single(vm.Members);

        vm.LoadFromEntry(new InstrumentEntry
        {
            Instrument = "second",
            Members = new List<InstrumentEntry>
            {
                new() { Instrument = "new1" },
                new() { Instrument = "new2" },
            },
        });

        Assert.Equal("second", vm.EnsembleName);
        Assert.Equal(2, vm.Members.Count);
        Assert.Equal("new1", vm.Members[0].Instrument);
        Assert.Equal("new2", vm.Members[1].Instrument);
    }

    [Fact]
    public void SaveToEntry_WritesMembers()
    {
        var ensemble = new InstrumentEntry { Instrument = "ensemble", IsEnsemble = true };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(ensemble);
        vm.Members.Add(new InstrumentEntry { Instrument = "violin" });
        vm.Members.Add(new InstrumentEntry { Instrument = "viola"  });

        vm.SaveToEntry(ensemble);

        Assert.NotNull(ensemble.Members);
        Assert.Equal(2, ensemble.Members!.Count);
        Assert.Equal("violin", ensemble.Members[0].Instrument);
    }

    [Fact]
    public void SaveToEntry_EmptyMembers_WritesNull()
    {
        // Empty collection persists as null on the model — keeps JSON
        // snapshots clean for the common case.
        var ensemble = new InstrumentEntry
        {
            Instrument = "ensemble",
            IsEnsemble = true,
            Members    = new List<InstrumentEntry> { new() { Instrument = "OLD" } },
        };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(ensemble);
        vm.Members.Clear();   // user removed all members

        vm.SaveToEntry(ensemble);

        Assert.Null(ensemble.Members);
    }

    [Fact]
    public void SaveToEntry_DoesNotTouchInstrumentName_H31_H33_Regression()
    {
        // Critical H31 + H33 contract: this editor's scope is the Members
        // list only. The parent's Instrument name and IsEnsemble flag must
        // survive a round-trip even if the user opens the editor on a
        // non-ensemble entry (which shouldn't happen in normal use but the
        // pre-fix code hardcoded IsEnsemble=true on save, which was bug H33).
        var ensemble = new InstrumentEntry
        {
            Instrument = "string quartet",
            IsEnsemble = true,
            PartNumber = 7,
            Key        = "C",
            Alternate  = "string quintet",
            Members    = new List<InstrumentEntry> { new() { Instrument = "violin" } },
        };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(ensemble);
        vm.Members.Add(new InstrumentEntry { Instrument = "viola" });

        vm.SaveToEntry(ensemble);

        // All non-Members fields preserved.
        Assert.Equal("string quartet", ensemble.Instrument);
        Assert.True(ensemble.IsEnsemble);
        Assert.Equal(7,                 ensemble.PartNumber);
        Assert.Equal("C",               ensemble.Key);
        Assert.Equal("string quintet",  ensemble.Alternate);
        // Members updated.
        Assert.Equal(2, ensemble.Members!.Count);
    }

    [Fact]
    public void SaveToEntry_PreservesIsEnsembleFalse_H33_Regression()
    {
        // The H33 specific symptom: pre-fix the OK handler built a new
        // InstrumentEntry with `IsEnsemble = true` hardcoded — even if the
        // caller opened the editor on a non-ensemble entry. The VM's
        // SaveToEntry must NOT touch IsEnsemble.
        var entry = new InstrumentEntry
        {
            Instrument = "x",
            IsEnsemble = false,   // not actually an ensemble
        };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(entry);
        vm.Members.Add(new InstrumentEntry { Instrument = "added" });

        vm.SaveToEntry(entry);

        Assert.False(entry.IsEnsemble);   // unchanged
    }

    [Fact]
    public void SaveToEntry_IndependentListInstance_PostSaveMutationsDontAffectSaved()
    {
        // Each save snapshots Members.ToList() — subsequent VM mutations
        // must NOT affect the saved entry's collection.
        var entry = new InstrumentEntry { Instrument = "x", IsEnsemble = true };
        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(entry);
        vm.Members.Add(new InstrumentEntry { Instrument = "first" });

        vm.SaveToEntry(entry);
        Assert.Single(entry.Members!);

        vm.Members.Add(new InstrumentEntry { Instrument = "late add" });
        Assert.Single(entry.Members!);   // saved snapshot unchanged
    }

    [Fact]
    public void Members_CollectionChanged_FiresOnAddRemove()
    {
        // The Members collection drives the WPF ListBox via ItemsSource
        // binding — verify CollectionChanged still fires correctly through
        // the ObservableCollection.
        var vm = new EnsembleEntryEditorViewModel();
        var notifications = 0;
        vm.Members.CollectionChanged += (_, _) => notifications++;

        vm.Members.Add(new InstrumentEntry { Instrument = "violin" });
        Assert.Equal(1, notifications);

        vm.Members.RemoveAt(0);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void RoundTrip_PreservesMembers()
    {
        var original = new InstrumentEntry
        {
            Instrument = "wind quintet",
            IsEnsemble = true,
            Members    = new List<InstrumentEntry>
            {
                new() { Instrument = "flute"    },
                new() { Instrument = "oboe"     },
                new() { Instrument = "clarinet", Key = "B-flat" },
                new() { Instrument = "horn"     },
                new() { Instrument = "bassoon"  },
            },
        };

        var vm = new EnsembleEntryEditorViewModel();
        vm.LoadFromEntry(original);

        var roundTripped = new InstrumentEntry { Instrument = "wind quintet", IsEnsemble = true };
        vm.SaveToEntry(roundTripped);

        Assert.Equal(5, roundTripped.Members!.Count);
        Assert.Equal("clarinet", roundTripped.Members[2].Instrument);
        Assert.Equal("B-flat",   roundTripped.Members[2].Key);
    }
}
