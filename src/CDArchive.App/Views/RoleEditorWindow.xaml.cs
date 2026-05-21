using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class RoleEditorWindow : Window
{
    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    public RoleEntry Role { get; private set; }

    public RoleEditorWindow(CanonPickLists pickLists, RoleEntry? role = null)
    {
        InitializeComponent();

        VoiceTypeCombo.ItemsSource = pickLists.VoiceTypes;
        Title = role == null ? "Add Role" : "Edit Role";

        Role = role ?? new RoleEntry();

        NameBox.Text         = Role.Name ?? "";
        VoiceTypeCombo.Text  = Role.VoiceType   ?? "";
        DescriptionBox.Text  = Role.Description ?? "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // H19: surface a MessageBox on missing required field instead of
        // silently no-op'ing — matches PerformerEditorWindow / VariantEditorWindow.
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Role name is required.", "Missing field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Role.Name        = NameBox.Text.Trim();
        Role.VoiceType   = NullIfEmpty(VoiceTypeCombo.Text);
        Role.Description = NullIfEmpty(DescriptionBox.Text);

        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
