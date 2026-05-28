using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>InstrumentEntryEditorWindow</c> (H13 small-editors slice 2).
/// Instrument is an editable combo bound to <c>pickLists.Instruments</c>
/// (ItemsSource stays in code-behind). PartNumber stored as string in VM,
/// parsed to int? at save time. Instrument required.
///
/// <para><b>Note</b>: this editor edits a single (non-ensemble) instrument
/// only — IsEnsemble and Members are deliberately NOT touched here (managed
/// by <c>EnsembleEntryEditorWindow</c>).</para>
/// </summary>
public partial class InstrumentEntryEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _instrument = "";
    [ObservableProperty] private string _partNumber = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _alternate = "";

    public enum SaveValidationError { None, MissingInstrument }

    public void LoadFromEntry(InstrumentEntry entry)
    {
        Instrument = entry.Instrument            ?? "";
        PartNumber = entry.PartNumber?.ToString() ?? "";
        Key        = entry.Key                    ?? "";
        Alternate  = entry.Alternate              ?? "";
    }

    public SaveValidationError SaveToEntry(InstrumentEntry entry)
    {
        if (string.IsNullOrWhiteSpace(Instrument)) return SaveValidationError.MissingInstrument;

        entry.Instrument = Instrument.Trim();
        entry.PartNumber = int.TryParse(PartNumber.Trim(), out var p) ? p : null;
        entry.Key        = NullIfEmpty(Key);
        entry.Alternate  = NullIfEmpty(Alternate);
        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
