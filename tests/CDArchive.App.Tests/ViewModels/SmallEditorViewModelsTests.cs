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
}
