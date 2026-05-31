using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>SessionEditorWindow</c>.
///
/// <para>Five free-text fields: Dates, Venue, City, State, Country.
/// Engineers and Producers are <see cref="ObservableCollection{T}"/>s of
/// individual name strings, edited via the editor's Add / Edit / Remove /
/// Up / Down buttons — pre-refactor they were comma-separated strings, but
/// the user wanted proper list editing.</para>
///
/// <para>No required fields — the editor commits whatever is there (a fully
/// blank session is allowed, though arguably useless).</para>
/// </summary>
public partial class SessionEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _dates     = "";
    [ObservableProperty] private string _venue     = "";
    [ObservableProperty] private string _city      = "";
    [ObservableProperty] private string _state     = "";
    [ObservableProperty] private string _country   = "";

    /// <summary>Engineer names, one per entry. Bound to the editor's
    /// Engineers ListBox; the editor's Add / Edit / Remove / Up / Down
    /// buttons mutate this collection in place.</summary>
    public ObservableCollection<string> Engineers { get; } = [];

    /// <summary>Producer names. Same shape as <see cref="Engineers"/>.</summary>
    public ObservableCollection<string> Producers { get; } = [];

    public void LoadFromSession(RecordingSession session)
    {
        Dates     = session.Dates    ?? "";
        Venue     = session.Venue    ?? "";
        City      = session.City     ?? "";
        State     = session.State    ?? "";
        Country   = session.Country  ?? "";

        Engineers.Clear();
        if (session.Engineers is not null)
            foreach (var e in session.Engineers) Engineers.Add(e);

        Producers.Clear();
        if (session.Producers is not null)
            foreach (var p in session.Producers) Producers.Add(p);
    }

    public void SaveToSession(RecordingSession session)
    {
        session.Dates     = NullIfEmpty(Dates);
        session.Venue     = NullIfEmpty(Venue);
        session.City      = NullIfEmpty(City);
        session.State     = NullIfEmpty(State);
        session.Country   = NullIfEmpty(Country);
        session.Engineers = Engineers.Count > 0 ? Engineers.ToList() : null;
        session.Producers = Producers.Count > 0 ? Producers.ToList() : null;
    }

    /// <summary>
    /// Split a comma-separated names string into a clean list (trimmed,
    /// empty entries discarded). Kept public for back-compat with any
    /// caller that still wants the legacy parse — e.g. paste-handling for
    /// the entry TextBox when the user pastes a comma-list.
    /// </summary>
    public static List<string> SplitNames(string text) =>
        text.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
