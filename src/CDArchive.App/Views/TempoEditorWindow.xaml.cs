using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class TempoEditorWindow : Window
{
    private readonly TempoInfo _tempo;

    public TempoInfo Tempo => _tempo;

    /// <summary>
    /// Create a new tempo with a suggested number.
    /// </summary>
    public TempoEditorWindow(int suggestedNumber)
    {
        InitializeComponent();
        _tempo = new TempoInfo { Number = suggestedNumber };
        Title = "New Tempo";
        NumberBox.Text = suggestedNumber.ToString();
        DescriptionBox.Text = "";
    }

    /// <summary>
    /// Edit an existing tempo in place.
    /// </summary>
    public TempoEditorWindow(TempoInfo tempo)
    {
        InitializeComponent();
        _tempo = tempo;
        Title = "Edit Tempo";
        NumberBox.Text = tempo.Number.ToString();
        DescriptionBox.Text = tempo.Description ?? "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _tempo.Number = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : 1;
        _tempo.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text)
            ? null
            : DescriptionBox.Text.Trim();
        DialogResult = true;
    }
}
