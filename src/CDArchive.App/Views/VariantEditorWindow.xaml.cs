using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class VariantEditorWindow : Window
{
    private readonly VariantInfo _variant;

    public VariantInfo Variant => _variant;

    public VariantEditorWindow(VariantInfo? existing = null)
    {
        InitializeComponent();

        _variant = existing != null
            ? new VariantInfo { Description = existing.Description, LongDescription = existing.LongDescription }
            : new VariantInfo();

        DescriptionBox.Text     = _variant.Description;
        LongDescriptionBox.Text = _variant.LongDescription ?? "";

        Loaded += (_, _) => DescriptionBox.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var desc = DescriptionBox.Text.Trim();
        if (string.IsNullOrEmpty(desc))
        {
            MessageBox.Show("A description is required.", "Variant", MessageBoxButton.OK, MessageBoxImage.Warning);
            DescriptionBox.Focus();
            return;
        }

        _variant.Description     = desc;
        _variant.LongDescription = string.IsNullOrWhiteSpace(LongDescriptionBox.Text)
            ? null
            : LongDescriptionBox.Text.Trim();

        DialogResult = true;
    }
}
