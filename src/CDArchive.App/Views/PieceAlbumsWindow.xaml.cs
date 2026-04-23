using System.Windows;
using System.Windows.Controls;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

/// <summary>
/// Read-only list of album tracks that reference a Canon piece/subpiece/version/composer.
/// The caller opens the selected album via <see cref="SelectedAlbum"/> after
/// <see cref="ShowDialog"/> returns true (either from the Open Album button or
/// a double-click on a row).
/// </summary>
public partial class PieceAlbumsWindow : Window
{
    public CanonAlbum? SelectedAlbum { get; private set; }

    public PieceAlbumsWindow(string headerLabel, IReadOnlyList<PieceAlbumHit> hits)
    {
        InitializeComponent();
        HeaderText.Text = headerLabel;
        StatusText.Text = $"{hits.Count} track reference(s)";
        HitList.ItemsSource = hits.Select(h => new Row(h)).ToList();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => OpenButton.IsEnabled = HitList.SelectedItem is Row;

    private void OnOpenClick(object sender, RoutedEventArgs e) => Commit();
    private void OnListDoubleClick(object sender, RoutedEventArgs e)
    {
        if (HitList.SelectedItem is Row) Commit();
    }

    private void Commit()
    {
        if (HitList.SelectedItem is Row r)
        {
            SelectedAlbum = r.Hit.Album;
            DialogResult = true;
            Close();
        }
    }

    // Display projection for the grid; keeps the raw Hit around for the callback.
    private sealed class Row
    {
        public PieceAlbumHit Hit { get; }
        public Row(PieceAlbumHit h) { Hit = h; }

        public string AlbumTitle      => Hit.Album.DisplayTitle;
        public string LabelCatalogue  =>
            string.IsNullOrWhiteSpace(Hit.Album.Label)
                ? (Hit.Album.CatalogueNumber ?? "")
                : $"{Hit.Album.Label} {Hit.Album.CatalogueNumber}".TrimEnd();
        public string DiscLabel       => Hit.Disc.VolumeNumber.HasValue
            ? $"V{Hit.Disc.VolumeNumber} D{Hit.Disc.DiscNumber}"
            : $"Disc {Hit.Disc.DiscNumber}";
        public int    TrackNumber     => Hit.Track.TrackNumber;
        public string RefSummary      => Hit.Ref.DisplaySummary;
    }
}
