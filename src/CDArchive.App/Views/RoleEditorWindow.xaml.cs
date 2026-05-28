using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class RoleEditorWindow : Window
{
    // H13 small-editors slice 2: field state moved to RoleEditorViewModel.
    private readonly RoleEditorViewModel _vm = new();

    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public RoleEntry Role { get; private set; }

    public RoleEditorWindow(CanonPickLists pickLists, RoleEntry? role = null)
    {
        InitializeComponent();
        DataContext = _vm;

        VoiceTypeCombo.ItemsSource = pickLists.VoiceTypes;
        Title = role == null ? "Add Role" : "Edit Role";

        Role = role ?? new RoleEntry();
        _vm.LoadFromRole(Role);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToRole(Role);
        if (error == RoleEditorViewModel.SaveValidationError.MissingName)
        {
            // H19: surface a MessageBox on missing required field instead of
            // silently no-op'ing.
            MessageBox.Show(this, "Role name is required.", "Missing field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
