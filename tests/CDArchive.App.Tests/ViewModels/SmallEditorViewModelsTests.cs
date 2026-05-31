using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 small-editors (slice 2): VMs for VariantEditor / RoleEditor /
/// ComposerCreditEditor / InstrumentEntryEditor. Each is a small dialog VM
/// with Load/Save methods + a <c>SaveValidationError</c> enum for the
/// required-field validation feedback.
/// </summary>
public class SmallEditorViewModelsTests
{
    // ── VariantEditorViewModel ───────────────────────────────────────────────

    [Fact]
    public void Variant_LoadAndSave_RoundTrip()
    {
        var src = new VariantInfo { Description = "var.1", LongDescription = "long" };
        var vm = new VariantEditorViewModel();
        vm.LoadFromVariant(src);

        Assert.Equal("var.1", vm.Description);
        Assert.Equal("long",  vm.LongDescription);

        var dest = new VariantInfo();
        Assert.Equal(VariantEditorViewModel.SaveValidationError.None, vm.SaveToVariant(dest));
        Assert.Equal("var.1", dest.Description);
        Assert.Equal("long",  dest.LongDescription);
    }

    [Fact]
    public void Variant_NullLongDescription_LoadsAsEmpty()
    {
        var vm = new VariantEditorViewModel();
        vm.LoadFromVariant(new VariantInfo { Description = "x" });
        Assert.Equal("", vm.LongDescription);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Variant_MissingDescription_ReturnsValidationError_LeavesVariantUnmutated(string desc)
    {
        var variant = new VariantInfo { Description = "original" };
        var vm = new VariantEditorViewModel { Description = desc };

        Assert.Equal(VariantEditorViewModel.SaveValidationError.MissingDescription,
            vm.SaveToVariant(variant));
        Assert.Equal("original", variant.Description);   // unmutated
    }

    [Fact]
    public void Variant_EmptyLongDescription_NormalisesToNull()
    {
        var variant = new VariantInfo { LongDescription = "old" };
        var vm = new VariantEditorViewModel { Description = "valid", LongDescription = "" };

        vm.SaveToVariant(variant);
        Assert.Null(variant.LongDescription);
    }

    // ── RoleEditorViewModel ──────────────────────────────────────────────────

    [Fact]
    public void Role_LoadAndSave_RoundTrip()
    {
        var src = new RoleEntry { Name = "Florestan", VoiceType = "tenor", Description = "A prisoner" };
        var vm = new RoleEditorViewModel();
        vm.LoadFromRole(src);

        Assert.Equal("Florestan",   vm.Name);
        Assert.Equal("tenor",       vm.VoiceType);
        Assert.Equal("A prisoner",  vm.Description);

        var dest = new RoleEntry();
        Assert.Equal(RoleEditorViewModel.SaveValidationError.None, vm.SaveToRole(dest));
        Assert.Equal("Florestan",   dest.Name);
        Assert.Equal("tenor",       dest.VoiceType);
        Assert.Equal("A prisoner",  dest.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Role_MissingName_ReturnsValidationError_LeavesRoleUnmutated(string name)
    {
        var role = new RoleEntry { Name = "original" };
        var vm = new RoleEditorViewModel { Name = name };

        Assert.Equal(RoleEditorViewModel.SaveValidationError.MissingName, vm.SaveToRole(role));
        Assert.Equal("original", role.Name);
    }

    [Fact]
    public void Role_EmptyOptionalFields_NormaliseToNull()
    {
        var role = new RoleEntry { VoiceType = "old", Description = "old" };
        var vm = new RoleEditorViewModel { Name = "valid" };

        vm.SaveToRole(role);
        Assert.Null(role.VoiceType);
        Assert.Null(role.Description);
    }

    // ── ComposerCreditEditorViewModel ────────────────────────────────────────

    [Fact]
    public void ComposerCredit_LoadAndSave_RoundTrip()
    {
        var src = new ComposerCredit { Name = "Schubert, Franz", Role = "completed by" };
        var vm = new ComposerCreditEditorViewModel();
        vm.LoadFromCredit(src);

        Assert.Equal("Schubert, Franz", vm.Name);
        Assert.Equal("completed by",    vm.Role);

        var dest = new ComposerCredit();
        Assert.Equal(ComposerCreditEditorViewModel.SaveValidationError.None,
            vm.SaveToCredit(dest));
        Assert.Equal("Schubert, Franz", dest.Name);
        Assert.Equal("completed by",    dest.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposerCredit_MissingName_ReturnsValidationError_LeavesCreditUnmutated(string name)
    {
        var credit = new ComposerCredit { Name = "original" };
        var vm = new ComposerCreditEditorViewModel { Name = name };

        Assert.Equal(ComposerCreditEditorViewModel.SaveValidationError.MissingName,
            vm.SaveToCredit(credit));
        Assert.Equal("original", credit.Name);
    }

    [Fact]
    public void ComposerCredit_EmptyRole_NormalisesToNull()
    {
        var credit = new ComposerCredit { Role = "old" };
        var vm = new ComposerCreditEditorViewModel { Name = "valid", Role = "" };

        vm.SaveToCredit(credit);
        Assert.Null(credit.Role);
    }

    // ── InstrumentEntryEditorViewModel ───────────────────────────────────────

    [Fact]
    public void Instrument_LoadAndSave_RoundTrip()
    {
        var src = new InstrumentEntry
        {
            Instrument = "violin",
            PartNumber = 1,
            Key        = "A",
            Alternate  = "viola",
        };
        var vm = new InstrumentEntryEditorViewModel();
        vm.LoadFromEntry(src);

        Assert.Equal("violin", vm.Instrument);
        Assert.Equal("1",      vm.PartNumber);
        Assert.Equal("A",      vm.Key);
        Assert.Equal("viola",  vm.Alternate);

        var dest = new InstrumentEntry();
        Assert.Equal(InstrumentEntryEditorViewModel.SaveValidationError.None,
            vm.SaveToEntry(dest));
        Assert.Equal("violin", dest.Instrument);
        Assert.Equal(1,        dest.PartNumber);
        Assert.Equal("A",      dest.Key);
        Assert.Equal("viola",  dest.Alternate);
    }

    [Fact]
    public void Instrument_NullPartNumber_LoadsAsEmpty()
    {
        var vm = new InstrumentEntryEditorViewModel();
        vm.LoadFromEntry(new InstrumentEntry { Instrument = "x" });
        Assert.Equal("", vm.PartNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Instrument_MissingInstrument_ReturnsValidationError_LeavesEntryUnmutated(string name)
    {
        var entry = new InstrumentEntry { Instrument = "original" };
        var vm = new InstrumentEntryEditorViewModel { Instrument = name };

        Assert.Equal(InstrumentEntryEditorViewModel.SaveValidationError.MissingInstrument,
            vm.SaveToEntry(entry));
        Assert.Equal("original", entry.Instrument);
    }

    [Theory]
    [InlineData("3",   3)]
    [InlineData("",    null)]
    [InlineData("abc", null)]
    public void Instrument_PartNumber_ParsedToInt_OrNull(string input, int? expected)
    {
        var entry = new InstrumentEntry();
        var vm = new InstrumentEntryEditorViewModel { Instrument = "x", PartNumber = input };

        vm.SaveToEntry(entry);
        Assert.Equal(expected, entry.PartNumber);
    }

    [Fact]
    public void Instrument_DoesNotTouchIsEnsembleOrMembers()
    {
        // Critical: this editor edits a single (non-ensemble) instrument
        // only. IsEnsemble + Members are managed by EnsembleEntryEditorWindow.
        // Pre-fix H31 retirement preserved this contract.
        var entry = new InstrumentEntry
        {
            Instrument = "old",
            IsEnsemble = true,
            Members    = new List<InstrumentEntry>
            {
                new() { Instrument = "violin 1" },
                new() { Instrument = "violin 2" },
            },
        };
        var vm = new InstrumentEntryEditorViewModel { Instrument = "violin" };

        vm.SaveToEntry(entry);

        Assert.True(entry.IsEnsemble);                         // untouched
        Assert.Equal(2, entry.Members?.Count ?? 0);            // untouched
    }

    // ── PerformerEditorViewModel ─────────────────────────────────────────────

    [Fact]
    public void Performer_LoadAndSave_RoundTrip()
    {
        var src = new AlbumPerformer { Name = "Karajan, Herbert von", Role = "Conductor", Instrument = "" };
        var vm = new PerformerEditorViewModel();
        vm.LoadFromPerformer(src);

        Assert.Equal("Karajan, Herbert von", vm.Name);
        Assert.Equal("Conductor",            vm.Role);
        Assert.Equal("",                     vm.Instrument);

        var dest = new AlbumPerformer();
        Assert.Equal(PerformerEditorViewModel.SaveValidationError.None,
            vm.SaveToPerformer(dest));
        Assert.Equal("Karajan, Herbert von", dest.Name);
        Assert.Equal("Conductor",            dest.Role);
        Assert.Null(dest.Instrument);                  // empty → null
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Performer_MissingName_ReturnsValidationError_LeavesPerformerUnmutated(string name)
    {
        var performer = new AlbumPerformer { Name = "original" };
        var vm = new PerformerEditorViewModel { Name = name };

        Assert.Equal(PerformerEditorViewModel.SaveValidationError.MissingName,
            vm.SaveToPerformer(performer));
        Assert.Equal("original", performer.Name);
    }

    [Fact]
    public void Performer_NullOptionalFields_LoadAsEmpty()
    {
        var vm = new PerformerEditorViewModel();
        vm.LoadFromPerformer(new AlbumPerformer { Name = "X" });

        Assert.Equal("", vm.Role);
        Assert.Equal("", vm.Instrument);
    }

    [Fact]
    public void Performer_MutatesInPlace_PreservesUnsurfacedFields_H31()
    {
        // H31 mutate-in-place contract: any field this editor doesn't surface
        // (e.g. PersonId / EnsembleId if those are exposed at the model level)
        // must survive a round-trip. We test by setting a recognised optional
        // field that's also not edited by the editor — the editor only
        // surfaces Name / Role / Instrument. AlbumPerformer has no such
        // field today; this test documents the contract — if a new field is
        // ever added to AlbumPerformer, extend this assertion.
        var performer = new AlbumPerformer
        {
            Name       = "x",
            Role       = "Conductor",
            Instrument = "Piano",
        };

        var vm = new PerformerEditorViewModel();
        vm.LoadFromPerformer(performer);
        vm.Name = "Karajan";

        var result = vm.SaveToPerformer(performer);
        Assert.Equal(PerformerEditorViewModel.SaveValidationError.None, result);

        // Mutate-in-place: same reference returned.
        Assert.Equal("Karajan",   performer.Name);
        Assert.Equal("Conductor", performer.Role);    // round-tripped
        Assert.Equal("Piano",     performer.Instrument);
    }

    // ── SessionEditorViewModel ───────────────────────────────────────────────

    [Fact]
    public void Session_LoadAndSave_RoundTrip()
    {
        var src = new RecordingSession
        {
            Dates     = "1962-Jan",
            Venue     = "Jesus-Christus-Kirche",
            City      = "Berlin",
            State     = "Berlin",
            Country   = "Germany",
            Engineers = new List<string> { "Karl-Heinz Schneider", "Otto Gerdes" },
            Producers = new List<string> { "John Culshaw" },
        };

        var vm = new SessionEditorViewModel();
        vm.LoadFromSession(src);

        Assert.Equal("1962-Jan",                                    vm.Dates);
        Assert.Equal("Jesus-Christus-Kirche",                       vm.Venue);
        Assert.Equal("Berlin",                                      vm.City);
        Assert.Equal("Berlin",                                      vm.State);
        Assert.Equal("Germany",                                     vm.Country);
        Assert.Equal(new[] { "Karl-Heinz Schneider", "Otto Gerdes" }, vm.Engineers);
        Assert.Equal(new[] { "John Culshaw" },                       vm.Producers);

        var dest = new RecordingSession();
        vm.SaveToSession(dest);

        Assert.Equal("1962-Jan",              dest.Dates);
        Assert.Equal("Jesus-Christus-Kirche", dest.Venue);
        Assert.Equal("Berlin",                dest.City);
        Assert.Equal("Berlin",                dest.State);
        Assert.Equal("Germany",               dest.Country);
        Assert.Equal(2, dest.Engineers?.Count ?? 0);
        Assert.Equal("Karl-Heinz Schneider", dest.Engineers![0]);
        Assert.Equal("Otto Gerdes",          dest.Engineers[1]);
        Assert.Single(dest.Producers!);
        Assert.Equal("John Culshaw", dest.Producers![0]);
    }

    [Fact]
    public void Session_NullListsLoadAsEmpty()
    {
        var src = new RecordingSession();   // all null
        var vm = new SessionEditorViewModel();
        vm.LoadFromSession(src);

        Assert.Equal("", vm.Dates);
        Assert.Equal("", vm.Venue);
        Assert.Equal("", vm.City);
        Assert.Equal("", vm.State);
        Assert.Equal("", vm.Country);
        Assert.Empty(vm.Engineers);
        Assert.Empty(vm.Producers);
    }

    [Fact]
    public void Session_EmptyFields_PersistAsNull()
    {
        var dest = new RecordingSession
        {
            Dates = "OLD", Venue = "OLD",
            Engineers = new List<string> { "OLD" },
            Producers = new List<string> { "OLD" },
        };
        var vm = new SessionEditorViewModel();

        vm.SaveToSession(dest);

        Assert.Null(dest.Dates);
        Assert.Null(dest.Venue);
        Assert.Null(dest.City);
        Assert.Null(dest.State);
        Assert.Null(dest.Country);
        Assert.Null(dest.Engineers);
        Assert.Null(dest.Producers);
    }

    [Fact]
    public void Session_LoadReplacesPriorListsRatherThanAppending()
    {
        // Defensive: a SessionEditor reused across edits must not accumulate
        // engineers / producers from previous sessions. LoadFromSession should
        // Clear() the ObservableCollections before re-populating.
        var vm = new SessionEditorViewModel();
        vm.LoadFromSession(new RecordingSession
        {
            Engineers = new List<string> { "Alice", "Bob" },
            Producers = new List<string> { "Pat" },
        });

        vm.LoadFromSession(new RecordingSession
        {
            Engineers = new List<string> { "Carol" },
        });

        Assert.Equal(new[] { "Carol" }, vm.Engineers);
        Assert.Empty(vm.Producers);
    }

    [Fact]
    public void Session_StateField_RoundTrips()
    {
        var src = new RecordingSession { State = "Bavaria" };
        var vm = new SessionEditorViewModel();
        vm.LoadFromSession(src);
        Assert.Equal("Bavaria", vm.State);

        vm.State = "  California  ";
        var dest = new RecordingSession();
        vm.SaveToSession(dest);
        Assert.Equal("California", dest.State);   // trimmed on save
    }

    [Theory]
    [InlineData("",                          0)]
    [InlineData("   ",                        0)]
    [InlineData("Alice",                      1)]
    [InlineData("Alice, Bob",                 2)]
    [InlineData("  Alice  ,  Bob  ",          2)]   // trim each
    [InlineData("Alice,,Bob",                 2)]   // skip empty
    [InlineData("Alice, , Bob",               2)]
    [InlineData(",,,",                        0)]   // all empty
    public void Session_SplitNames_HandlesEdgeCases(string input, int expectedCount)
    {
        var result = SessionEditorViewModel.SplitNames(input);
        Assert.Equal(expectedCount, result.Count);
        foreach (var s in result)
        {
            Assert.Equal(s.Trim(), s);      // entries are trimmed
            Assert.NotEmpty(s);             // no empty entries
        }
    }

    [Fact]
    public void Session_NoValidationRequired_EmptySessionPersists()
    {
        // Unlike most other small editors, SessionEditor has no required
        // fields — a fully blank session is allowed (though arguably useless).
        var dest = new RecordingSession();
        var vm = new SessionEditorViewModel();
        // All fields empty in VM.

        vm.SaveToSession(dest);
        // All persisted as null; no exception.

        Assert.Null(dest.Dates);
        Assert.Null(dest.Engineers);
    }
}
