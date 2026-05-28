using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>PerformerEditorWindow</c> (H13 small-editors slice 3).
/// Three fields with Name required. Role is an editable combo bound to a
/// roles ItemsSource (stays in code-behind).
/// </summary>
public partial class PerformerEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _instrument = "";

    public enum SaveValidationError { None, MissingName }

    public void LoadFromPerformer(AlbumPerformer performer)
    {
        Name       = performer.Name       ?? "";
        Role       = performer.Role       ?? "";
        Instrument = performer.Instrument ?? "";
    }

    public SaveValidationError SaveToPerformer(AlbumPerformer performer)
    {
        if (string.IsNullOrWhiteSpace(Name)) return SaveValidationError.MissingName;

        performer.Name       = Name.Trim();
        performer.Role       = NullIfEmpty(Role);
        performer.Instrument = NullIfEmpty(Instrument);
        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
