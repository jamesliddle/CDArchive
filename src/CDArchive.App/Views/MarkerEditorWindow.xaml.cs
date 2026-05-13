using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

/// <summary>
/// Modal editor for a single <see cref="MusicalMarker"/>. Used by
/// <see cref="PieceEditorWindow"/>'s Markers list (and any future caller that
/// needs to author a marker).
/// <para>
/// The marker's <see cref="MusicalMarker.Id"/> is treated as immutable — only
/// SQLite assigns it on first save. Editing here updates the user-facing
/// fields (kind / value / bar / number / description) and leaves the id alone,
/// preserving any track refs anchored to it.
/// </para>
/// </summary>
public partial class MarkerEditorWindow : Window
{
    private readonly MusicalMarker _marker;

    public MusicalMarker Marker => _marker;

    /// <summary>Create a new marker (Id stays 0 until SQLite assigns one).</summary>
    public MarkerEditorWindow()
    {
        InitializeComponent();
        _marker = new MusicalMarker();
        Title = "New Marker";
        InitKindCombo(MarkerKind.Tempo);
    }

    /// <summary>Edit an existing marker in place.</summary>
    public MarkerEditorWindow(MusicalMarker marker)
    {
        InitializeComponent();
        _marker = marker;
        Title = "Edit Marker";
        InitKindCombo(marker.Kind);
        ValueBox.Text       = marker.Value ?? "";
        BarNumberBox.Text   = marker.BarNumber?.ToString() ?? "";
        NumberBox.Text      = marker.Number?.ToString() ?? "";
        DescriptionBox.Text = marker.Description ?? "";
    }

    private void InitKindCombo(MarkerKind selected)
    {
        // Bind both the underlying enum value and a friendlier display label.
        // ComboBox uses the `ToString()` / SelectedValue plumbing — we keep it
        // simple and store enum values directly on the items.
        KindCombo.Items.Clear();
        foreach (MarkerKind kind in Enum.GetValues(typeof(MarkerKind)))
        {
            KindCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = FormatKind(kind),
                Tag = kind,
                IsSelected = kind == selected,
            });
        }
    }

    private static string FormatKind(MarkerKind kind) => kind switch
    {
        MarkerKind.Tempo         => "Tempo indication",
        MarkerKind.FirstLine     => "First line",
        MarkerKind.RehearsalMark => "Rehearsal mark",
        MarkerKind.BarNumber     => "Bar number",
        MarkerKind.Section       => "Section label",
        _                        => kind.ToString(),
    };

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (KindCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is MarkerKind kind)
            _marker.Kind = kind;

        _marker.Value       = NullIfEmpty(ValueBox.Text);
        _marker.BarNumber   = int.TryParse(BarNumberBox.Text.Trim(), out var bar) ? bar : null;
        _marker.Number      = int.TryParse(NumberBox.Text.Trim(),    out var num) ? num : null;
        _marker.Description = NullIfEmpty(DescriptionBox.Text);

        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
