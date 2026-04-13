using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerCreditEditorWindow : Window
{
    public ComposerCredit Credit { get; private set; } = new();

    public ComposerCreditEditorWindow(
        IEnumerable<string> composerNames,
        IEnumerable<string> creativeRoles,
        ComposerCredit? credit = null)
    {
        InitializeComponent();

        NameCombo.ItemsSource = composerNames;
        RoleCombo.ItemsSource = creativeRoles;

        Title = credit == null ? "Add Other Contributor" : "Edit Other Contributor";

        if (credit != null)
        {
            NameCombo.Text = credit.Name;
            RoleCombo.Text = credit.Role ?? "";
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameCombo.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        Credit = new ComposerCredit
        {
            Name = name,
            Role = string.IsNullOrWhiteSpace(RoleCombo.Text) ? null : RoleCombo.Text.Trim(),
        };
        DialogResult = true;
    }
}
