using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class InstrumentEntryEditorWindow : Window
{
    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public InstrumentEntry Entry { get; private set; }

    public InstrumentEntryEditorWindow(CanonPickLists pickLists, InstrumentEntry? entry = null)
    {
        InitializeComponent();

        InstrumentCombo.ItemsSource = pickLists.Instruments.Order();
        Title = entry == null ? "Add Instrument" : "Edit Instrument";

        Entry = entry ?? new InstrumentEntry();

        InstrumentCombo.Text = Entry.Instrument ?? "";
        PartNumberBox.Text   = Entry.PartNumber?.ToString() ?? "";
        KeyBox.Text          = Entry.Key       ?? "";
        AlternateBox.Text    = Entry.Alternate ?? "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InstrumentCombo.Text)) return;

        Entry.Instrument = InstrumentCombo.Text.Trim();
        Entry.PartNumber = int.TryParse(PartNumberBox.Text.Trim(), out var p) ? p : null;
        Entry.Key        = NullIfEmpty(KeyBox.Text);
        Entry.Alternate  = NullIfEmpty(AlternateBox.Text);
        // IsEnsemble and Members are deliberately NOT touched here — this
        // editor is for a single (non-ensemble) instrument; an ensemble's
        // Members are managed by EnsembleEntryEditorWindow.

        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
