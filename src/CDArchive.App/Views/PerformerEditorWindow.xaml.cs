using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PerformerEditorWindow : Window
{
    // Rework H31: hold a working instance and mutate it in place on OK rather
    // than constructing a fresh AlbumPerformer from the editor's three string
    // fields. Pre-fix any field this editor didn't surface (today there are
    // none; tomorrow there might be — and the AlbumPerformerRow already has
    // PersonId / EnsembleId FKs that future model work may expose) was
    // silently dropped on edit. Mutating preserves every non-edited field.
    private readonly AlbumPerformer _working;

    public AlbumPerformer? Result { get; private set; }

    public PerformerEditorWindow(AlbumPerformer? existing, IReadOnlyList<string> roles)
    {
        InitializeComponent();

        RoleBox.ItemsSource = roles;

        _working = existing ?? new AlbumPerformer();

        NameBox.Text        = _working.Name        ?? "";
        RoleBox.Text        = _working.Role        ?? "";
        InstrumentBox.Text  = _working.Instrument  ?? "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        _working.Name       = name;
        _working.Role       = NullIfEmpty(RoleBox.Text);
        _working.Instrument = NullIfEmpty(InstrumentBox.Text);

        Result = _working;
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
