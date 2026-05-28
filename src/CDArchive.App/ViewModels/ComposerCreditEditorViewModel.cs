using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>ComposerCreditEditorWindow</c> (H13 small-editors slice 2).
/// Two editable combos bound to composer-names + creative-roles ItemsSources
/// (kept in code-behind). Name required.
/// </summary>
public partial class ComposerCreditEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "";

    public enum SaveValidationError { None, MissingName }

    public void LoadFromCredit(ComposerCredit credit)
    {
        Name = credit.Name ?? "";
        Role = credit.Role ?? "";
    }

    public SaveValidationError SaveToCredit(ComposerCredit credit)
    {
        if (string.IsNullOrWhiteSpace(Name)) return SaveValidationError.MissingName;

        credit.Name = Name.Trim();
        credit.Role = NullIfEmpty(Role);
        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
