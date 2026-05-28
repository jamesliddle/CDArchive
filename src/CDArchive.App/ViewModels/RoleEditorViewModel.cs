using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>RoleEditorWindow</c> (H13 small-editors slice 2).
/// Three fields with Name required. VoiceType is an editable combo bound to
/// <c>pickLists.VoiceTypes</c> — ItemsSource stays in code-behind.
/// </summary>
public partial class RoleEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _voiceType = "";
    [ObservableProperty] private string _description = "";

    public enum SaveValidationError { None, MissingName }

    public void LoadFromRole(RoleEntry role)
    {
        Name        = role.Name        ?? "";
        VoiceType   = role.VoiceType   ?? "";
        Description = role.Description ?? "";
    }

    public SaveValidationError SaveToRole(RoleEntry role)
    {
        if (string.IsNullOrWhiteSpace(Name)) return SaveValidationError.MissingName;

        role.Name        = Name.Trim();
        role.VoiceType   = NullIfEmpty(VoiceType);
        role.Description = NullIfEmpty(Description);
        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
