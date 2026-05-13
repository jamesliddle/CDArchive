using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PerformerEditorWindow : Window
{
    public AlbumPerformer? Result { get; private set; }

    public PerformerEditorWindow(AlbumPerformer? existing, IReadOnlyList<string> roles)
    {
        InitializeComponent();

        RoleBox.ItemsSource = roles;

        if (existing != null)
        {
            NameBox.Text        = existing.Name        ?? "";
            RoleBox.Text        = existing.Role        ?? "";
            InstrumentBox.Text  = existing.Instrument  ?? "";
        }
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

        var role       = string.IsNullOrWhiteSpace(RoleBox.Text)       ? null : RoleBox.Text.Trim();
        var instrument = string.IsNullOrWhiteSpace(InstrumentBox.Text)  ? null : InstrumentBox.Text.Trim();

        Result = new AlbumPerformer
        {
            Name       = name,
            Role       = role,
            Instrument = instrument
        };

        DialogResult = true;
    }
}
