using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class RoleEditorWindow : Window
{
    public RoleEntry Role { get; private set; } = new();

    public RoleEditorWindow(CanonPickLists pickLists, RoleEntry? role = null)
    {
        InitializeComponent();

        VoiceTypeCombo.ItemsSource = pickLists.VoiceTypes;
        Title = role == null ? "Add Role" : "Edit Role";

        if (role != null)
        {
            NameBox.Text         = role.Name;
            VoiceTypeCombo.Text  = role.VoiceType ?? "";
            DescriptionBox.Text  = role.Description ?? "";
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return;

        Role = new RoleEntry
        {
            Name        = NameBox.Text.Trim(),
            VoiceType   = NullIfEmpty(VoiceTypeCombo.Text),
            Description = NullIfEmpty(DescriptionBox.Text),
        };
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
