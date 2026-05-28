using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerCreditEditorWindow : Window
{
    // H13 small-editors slice 2: field state moved to ComposerCreditEditorViewModel.
    private readonly ComposerCreditEditorViewModel _vm = new();

    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public ComposerCredit Credit { get; private set; }

    public ComposerCreditEditorWindow(
        IEnumerable<string> composerNames,
        IEnumerable<string> creativeRoles,
        ComposerCredit? credit = null)
    {
        InitializeComponent();
        DataContext = _vm;

        NameCombo.ItemsSource = composerNames;
        RoleCombo.ItemsSource = creativeRoles;

        Title = credit == null ? "Add Other Contributor" : "Edit Other Contributor";

        Credit = credit ?? new ComposerCredit();
        _vm.LoadFromCredit(Credit);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToCredit(Credit);
        if (error == ComposerCreditEditorViewModel.SaveValidationError.MissingName)
        {
            // H19: surface a MessageBox on missing required field instead of
            // silently no-op'ing.
            MessageBox.Show(this, "Contributor name is required.", "Missing field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameCombo.Focus();
            return;
        }

        DialogResult = true;
    }
}
