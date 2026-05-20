using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerCreditEditorWindow : Window
{
    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public ComposerCredit Credit { get; private set; }

    public ComposerCreditEditorWindow(
        IEnumerable<string> composerNames,
        IEnumerable<string> creativeRoles,
        ComposerCredit? credit = null)
    {
        InitializeComponent();

        NameCombo.ItemsSource = composerNames;
        RoleCombo.ItemsSource = creativeRoles;

        Title = credit == null ? "Add Other Contributor" : "Edit Other Contributor";

        Credit = credit ?? new ComposerCredit();

        NameCombo.Text = Credit.Name ?? "";
        RoleCombo.Text = Credit.Role ?? "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameCombo.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        Credit.Name = name;
        Credit.Role = string.IsNullOrWhiteSpace(RoleCombo.Text) ? null : RoleCombo.Text.Trim();

        DialogResult = true;
    }
}
