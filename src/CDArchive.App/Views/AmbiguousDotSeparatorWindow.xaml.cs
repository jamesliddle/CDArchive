using System.Collections.ObjectModel;
using System.Windows;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Views;

/// <summary>
/// Dialog shown during iTunes import when one or more selected tracks have
/// names containing the ambiguous <c>". "</c> separator within a non-leaf
/// segment. The user picks per-row whether each instance means a subpiece
/// hierarchy (Verdi Requiem-style "Dies irae. Tuba mirum") or a
/// form-and-tempo combination (instrumental-sonata-style "Scherzando.
/// Allegretto, ma non troppo"). The default for every row is
/// <c>FormAndTempo</c> because the user reported the existing implicit
/// behaviour (always treat as subpiece hierarchy) was wrong for the
/// dominant instrumental case in their library.
/// </summary>
public partial class AmbiguousDotSeparatorWindow : Window
{
    public ObservableCollection<Row> Items { get; }

    public AmbiguousDotSeparatorWindow(IReadOnlyList<(ItunesTrack Track, IReadOnlyList<ItunesImportInference.AmbiguousSegment> Segments)> ambiguous)
    {
        InitializeComponent();
        Items = new ObservableCollection<Row>(ambiguous.Select(a => new Row(a.Track, a.Segments)));
        DataContext = this;
    }

    /// <summary>
    /// Returns the user's chosen interpretation per iTunes TrackId.
    /// The Importer plumbs this through <c>ItunesImporter.Import</c>'s
    /// <c>dotInterpretations</c> parameter.
    /// </summary>
    public IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation> Result =>
        Items.ToDictionary(r => r.Track.TrackId, r => r.Choice switch
        {
            "SubpieceHierarchy" => ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy,
            _                   => ItunesImportInference.DotSeparatorInterpretation.FormAndTempo,
        });

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    public sealed class Row
    {
        public ItunesTrack Track { get; }
        public string TrackName => Track.Name;
        public string AmbiguousDisplay { get; }

        /// <summary>
        /// String-typed so the XAML <c>SelectedValuePath="Tag"</c> binding
        /// matches the ComboBoxItem Tag values literally. The window
        /// converts to <see cref="ItunesImportInference.DotSeparatorInterpretation"/>
        /// in <see cref="AmbiguousDotSeparatorWindow.Result"/>.
        /// </summary>
        public string Choice { get; set; } = "FormAndTempo";

        public Row(ItunesTrack track, IReadOnlyList<ItunesImportInference.AmbiguousSegment> segments)
        {
            Track = track;
            AmbiguousDisplay = string.Join("; ", segments.Select(s => s.Segment));
        }
    }
}
