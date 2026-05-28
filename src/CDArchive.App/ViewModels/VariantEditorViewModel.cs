using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>VariantEditorWindow</c> (H13 small-editors slice 2).
/// Two text fields with Description required.
/// </summary>
public partial class VariantEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _longDescription = "";

    public enum SaveValidationError { None, MissingDescription }

    public void LoadFromVariant(VariantInfo variant)
    {
        Description     = variant.Description     ?? "";
        LongDescription = variant.LongDescription ?? "";
    }

    public SaveValidationError SaveToVariant(VariantInfo variant)
    {
        if (string.IsNullOrWhiteSpace(Description)) return SaveValidationError.MissingDescription;

        variant.Description     = Description.Trim();
        variant.LongDescription = NullIfEmpty(LongDescription);
        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
