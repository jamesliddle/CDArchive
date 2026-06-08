using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>MarkerEditorWindow</c> (H13 small-editors slice 5 —
/// the last of the H13 small-editor migrations).
///
/// <para>Five fields: Kind (enum, combo) + Value + BarNumber (int? via string)
/// + Number (int? via string) + Description. No required-field validation —
/// OK always succeeds.</para>
///
/// <para>Mutate-in-place contract (H31): the marker's stable
/// <see cref="MusicalMarker.Id"/> is preserved across edits. SaveToMarker
/// writes back to the supplied instance rather than constructing a fresh one.</para>
/// </summary>
public partial class MarkerEditorViewModel : ObservableObject
{
    /// <summary>Marker kind enum. Bound to ComboBox.SelectedValue via
    /// SelectedValuePath="Kind" against <see cref="KindOptions"/>.</summary>
    [ObservableProperty] private MarkerKind _kind;

    [ObservableProperty] private string _value = "";

    /// <summary>Bar number. Stored as string in the VM (TextBox binding);
    /// parsed to int? at save time.</summary>
    [ObservableProperty] private string _barNumber = "";

    /// <summary>Marker number (e.g. for Rehearsal Mark "5"). Stored as string;
    /// parsed to int? at save time.</summary>
    [ObservableProperty] private string _number = "";

    [ObservableProperty] private string _description = "";

    /// <summary>
    /// Backing list for the Kind combo. Each option pairs the enum value
    /// with a friendlier display label. Exposed as a read-only collection so
    /// the XAML can bind <c>ItemsSource</c> + <c>DisplayMemberPath="Label"</c>
    /// + <c>SelectedValuePath="Kind"</c>.
    /// </summary>
    public IReadOnlyList<KindOption> KindOptions { get; } =
        Enum.GetValues<MarkerKind>().Select(k => new KindOption(k, FormatKind(k))).ToList();

    /// <summary>Display label for a marker kind.</summary>
    public static string FormatKind(MarkerKind kind) => kind switch
    {
        MarkerKind.Tempo         => "Tempo indication",
        MarkerKind.RehearsalMark => "Rehearsal mark",
        MarkerKind.BarNumber     => "Bar number",
        MarkerKind.Section       => "Section label",
        _                        => kind.ToString(),
    };

    /// <summary>
    /// Populate from an existing marker (or a fresh one for the "new" path).
    /// </summary>
    public void LoadFromMarker(MusicalMarker marker)
    {
        Kind        = marker.Kind;
        Value       = marker.Value     ?? "";
        BarNumber   = marker.BarNumber?.ToString() ?? "";
        Number      = marker.Number?.ToString()    ?? "";
        Description = marker.Description ?? "";
    }

    /// <summary>
    /// Write VM state back to the supplied marker in place — preserves
    /// <see cref="MusicalMarker.Id"/> per the H31 mutate-in-place contract.
    /// Integer fields parse from string (failed parse → null); empty strings
    /// normalise to null.
    /// </summary>
    public void SaveToMarker(MusicalMarker marker)
    {
        marker.Kind        = Kind;
        marker.Value       = NullIfEmpty(Value);
        marker.BarNumber   = int.TryParse(BarNumber.Trim(), out var bar) ? bar : null;
        marker.Number      = int.TryParse(Number.Trim(),    out var num) ? num : null;
        marker.Description = NullIfEmpty(Description);
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Pairs a <see cref="MarkerKind"/> enum value with its friendly display
/// label. Used by <see cref="MarkerEditorViewModel.KindOptions"/> to drive
/// the editor's Kind combo with SelectedValue binding.
/// </summary>
public record KindOption(MarkerKind Kind, string Label);
