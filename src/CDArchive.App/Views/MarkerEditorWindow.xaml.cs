using System.Windows;
using CDArchive.App.ViewModels;
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
    // H13 small-editors slice 5 (final): field state moved to MarkerEditorViewModel.
    private readonly MarkerEditorViewModel _vm = new();
    private readonly MusicalMarker _marker;

    public MusicalMarker Marker => _marker;

    /// <summary>Create a new marker (Id stays 0 until SQLite assigns one).</summary>
    public MarkerEditorWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _marker = new MusicalMarker();
        Title = "New Marker";
        _vm.LoadFromMarker(_marker);   // sets Kind=Tempo (default) + empty strings
    }

    /// <summary>Edit an existing marker in place.</summary>
    public MarkerEditorWindow(MusicalMarker marker)
    {
        InitializeComponent();
        DataContext = _vm;
        _marker = marker;
        Title = "Edit Marker";
        _vm.LoadFromMarker(marker);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // No required-field validation — OK always succeeds. Save mutates the
        // existing marker in place (H31 contract — preserves stable Id).
        _vm.SaveToMarker(_marker);
        DialogResult = true;
    }
}
