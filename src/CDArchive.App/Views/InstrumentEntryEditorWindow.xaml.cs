using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class InstrumentEntryEditorWindow : Window
{
    public InstrumentEntry Entry { get; private set; } = new();

    public InstrumentEntryEditorWindow(CanonPickLists pickLists, InstrumentEntry? entry = null)
    {
        InitializeComponent();

        InstrumentCombo.ItemsSource = pickLists.Instruments.Order();
        Title = entry == null ? "Add Instrument" : "Edit Instrument";

        if (entry != null)
        {
            InstrumentCombo.Text = entry.Instrument;
            PartNumberBox.Text  = entry.PartNumber?.ToString() ?? "";
            KeyBox.Text         = entry.Key ?? "";
            AlternateBox.Text   = entry.Alternate ?? "";
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InstrumentCombo.Text)) return;

        Entry = new InstrumentEntry
        {
            Instrument  = InstrumentCombo.Text.Trim(),
            PartNumber  = int.TryParse(PartNumberBox.Text.Trim(), out var p) ? p : null,
            Key         = NullIfEmpty(KeyBox.Text),
            Alternate   = NullIfEmpty(AlternateBox.Text),
        };
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
