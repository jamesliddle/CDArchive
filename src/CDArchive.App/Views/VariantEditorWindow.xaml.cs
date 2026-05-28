using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class VariantEditorWindow : Window
{
    // H13 small-editors slice 2: field state moved to VariantEditorViewModel.
    private readonly VariantEditorViewModel _vm = new();
    private readonly VariantInfo _variant;

    public VariantInfo Variant => _variant;

    public VariantEditorWindow(VariantInfo? existing = null)
    {
        InitializeComponent();
        DataContext = _vm;

        _variant = existing != null
            ? new VariantInfo { Description = existing.Description, LongDescription = existing.LongDescription }
            : new VariantInfo();

        _vm.LoadFromVariant(_variant);

        Loaded += (_, _) => DescriptionBox.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToVariant(_variant);
        if (error == VariantEditorViewModel.SaveValidationError.MissingDescription)
        {
            MessageBox.Show("A description is required.", "Variant",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DescriptionBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
