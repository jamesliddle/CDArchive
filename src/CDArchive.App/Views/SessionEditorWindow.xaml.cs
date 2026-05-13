using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class SessionEditorWindow : Window
{
    public RecordingSession? Result { get; private set; }

    public SessionEditorWindow(RecordingSession? existing)
    {
        InitializeComponent();

        if (existing != null)
        {
            DatesBox.Text     = existing.Dates    ?? "";
            VenueBox.Text     = existing.Venue    ?? "";
            CityBox.Text      = existing.City     ?? "";
            CountryBox.Text   = existing.Country  ?? "";
            EngineersBox.Text = existing.Engineers != null
                ? string.Join(", ", existing.Engineers) : "";
            ProducersBox.Text = existing.Producers != null
                ? string.Join(", ", existing.Producers) : "";
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var engineers = SplitNames(EngineersBox.Text);
        var producers = SplitNames(ProducersBox.Text);

        Result = new RecordingSession
        {
            Dates     = NullIfEmpty(DatesBox.Text),
            Venue     = NullIfEmpty(VenueBox.Text),
            City      = NullIfEmpty(CityBox.Text),
            Country   = NullIfEmpty(CountryBox.Text),
            Engineers = engineers.Count > 0 ? engineers : null,
            Producers = producers.Count > 0 ? producers : null
        };

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
