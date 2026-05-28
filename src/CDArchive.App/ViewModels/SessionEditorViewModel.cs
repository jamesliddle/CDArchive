using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>SessionEditorWindow</c> (H13 small-editors slice 3).
/// Six fields: 4 free-text (Dates, Venue, City, Country) + 2 lists displayed
/// as comma-separated strings (Engineers, Producers). No required fields —
/// the editor commits whatever is there (a fully blank session is allowed,
/// though arguably useless).
/// </summary>
public partial class SessionEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _dates     = "";
    [ObservableProperty] private string _venue     = "";
    [ObservableProperty] private string _city      = "";
    [ObservableProperty] private string _country   = "";

    /// <summary>Comma-separated engineer names. Round-trips via
    /// <see cref="SplitNames"/> / <c>string.Join(", ", ...)</c>.</summary>
    [ObservableProperty] private string _engineers = "";

    /// <summary>Comma-separated producer names.</summary>
    [ObservableProperty] private string _producers = "";

    public void LoadFromSession(RecordingSession session)
    {
        Dates     = session.Dates    ?? "";
        Venue     = session.Venue    ?? "";
        City      = session.City     ?? "";
        Country   = session.Country  ?? "";
        Engineers = session.Engineers != null ? string.Join(", ", session.Engineers) : "";
        Producers = session.Producers != null ? string.Join(", ", session.Producers) : "";
    }

    public void SaveToSession(RecordingSession session)
    {
        var engineers = SplitNames(Engineers);
        var producers = SplitNames(Producers);

        session.Dates     = NullIfEmpty(Dates);
        session.Venue     = NullIfEmpty(Venue);
        session.City      = NullIfEmpty(City);
        session.Country   = NullIfEmpty(Country);
        session.Engineers = engineers.Count > 0 ? engineers : null;
        session.Producers = producers.Count > 0 ? producers : null;
    }

    /// <summary>
    /// Split a comma-separated names string into a clean list (trimmed,
    /// empty entries discarded). Public so the editor's tests can drive it
    /// directly without a Save round-trip.
    /// </summary>
    public static List<string> SplitNames(string text) =>
        text.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
