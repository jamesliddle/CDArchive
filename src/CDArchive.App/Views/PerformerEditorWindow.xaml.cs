using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PerformerEditorWindow : Window
{
    // H13 small-editors slice 3: field state moved to PerformerEditorViewModel.

    // Rework H31: hold a working instance and mutate it in place on OK rather
    // than constructing a fresh AlbumPerformer from the editor's three string
    // fields. Pre-fix any field this editor didn't surface (today there are
    // none; tomorrow there might be — and the AlbumPerformerRow already has
    // PersonId / EnsembleId FKs that future model work may expose) was
    // silently dropped on edit. Mutating preserves every non-edited field.
    private readonly PerformerEditorViewModel _vm = new();
    private readonly AlbumPerformer _working;

    public AlbumPerformer? Result { get; private set; }

    public PerformerEditorWindow(AlbumPerformer? existing, IReadOnlyList<string> roles)
    {
        InitializeComponent();
        DataContext = _vm;

        RoleBox.ItemsSource = roles;

        _working = existing ?? new AlbumPerformer();
        _vm.LoadFromPerformer(_working);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToPerformer(_working);
        if (error == PerformerEditorViewModel.SaveValidationError.MissingName)
        {
            MessageBox.Show("Name is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        Result = _working;
        DialogResult = true;
    }
}
