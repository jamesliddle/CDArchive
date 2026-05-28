using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class InstrumentEntryEditorWindow : Window
{
    // H13 small-editors slice 2: field state moved to InstrumentEntryEditorViewModel.
    private readonly InstrumentEntryEditorViewModel _vm = new();

    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public InstrumentEntry Entry { get; private set; }

    public InstrumentEntryEditorWindow(CanonPickLists pickLists, InstrumentEntry? entry = null)
    {
        InitializeComponent();
        DataContext = _vm;

        InstrumentCombo.ItemsSource = pickLists.Instruments.Order();
        Title = entry == null ? "Add Instrument" : "Edit Instrument";

        Entry = entry ?? new InstrumentEntry();
        _vm.LoadFromEntry(Entry);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToEntry(Entry);
        if (error == InstrumentEntryEditorViewModel.SaveValidationError.MissingInstrument)
        {
            // H19: surface a MessageBox on missing required field instead of
            // silently no-op'ing.
            MessageBox.Show(this, "Instrument is required.", "Missing field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            InstrumentCombo.Focus();
            return;
        }

        // IsEnsemble and Members are deliberately NOT touched here — this
        // editor is for a single (non-ensemble) instrument; an ensemble's
        // Members are managed by EnsembleEntryEditorWindow.

        DialogResult = true;
    }
}
