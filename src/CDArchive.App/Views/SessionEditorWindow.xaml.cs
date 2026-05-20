using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class SessionEditorWindow : Window
{
    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    private readonly RecordingSession _working;

    public RecordingSession? Result { get; private set; }

    public SessionEditorWindow(RecordingSession? existing)
    {
        InitializeComponent();

        _working = existing ?? new RecordingSession();

        DatesBox.Text     = _working.Dates    ?? "";
        VenueBox.Text     = _working.Venue    ?? "";
        CityBox.Text      = _working.City     ?? "";
        CountryBox.Text   = _working.Country  ?? "";
        EngineersBox.Text = _working.Engineers != null
            ? string.Join(", ", _working.Engineers) : "";
        ProducersBox.Text = _working.Producers != null
            ? string.Join(", ", _working.Producers) : "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var engineers = SplitNames(EngineersBox.Text);
        var producers = SplitNames(ProducersBox.Text);

        _working.Dates     = NullIfEmpty(DatesBox.Text);
        _working.Venue     = NullIfEmpty(VenueBox.Text);
        _working.City      = NullIfEmpty(CityBox.Text);
        _working.Country   = NullIfEmpty(CountryBox.Text);
        _working.Engineers = engineers.Count > 0 ? engineers : null;
        _working.Producers = producers.Count > 0 ? producers : null;

        Result = _working;
        DialogResult = true;
    }

    private static List<string> SplitNames(string text) =>
        text.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
