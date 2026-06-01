using System.Windows;
using System.Windows.Controls;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Views;

/// <summary>
/// Header-less list of albums (and loose tracks) that reference a Canon
/// piece / subpiece / version / composer. Each top-level row is one album
/// (multiple track refs collapse into a single expandable row whose children
/// show the per-track references) or one loose track.
/// </summary>
public partial class PieceAlbumsWindow : Window
{
    private readonly PlayerViewModel _player;

    /// <summary>The album the caller should open. Null when the user
    /// clicked Play (the window already drove playback) or Cancel.</summary>
    public CanonAlbum? SelectedAlbum { get; private set; }

    /// <summary>True when the user clicked Play — the window has already
    /// started playback via the injected <see cref="PlayerViewModel"/>;
    /// the caller should not open the editor.</summary>
    public bool PlayRequested { get; private set; }

    public PieceAlbumsWindow(string headerLabel, IReadOnlyList<PieceAlbumHit> hits, PlayerViewModel player)
    {
        InitializeComponent();
        _player = player;
        HeaderText.Text = headerLabel;

        var nodes = BuildNodes(hits);
        HitTree.ItemsSource = nodes;

        int albumCount = nodes.OfType<AlbumNode>().Count();
        int looseCount = nodes.OfType<LooseTrackNode>().Count();
        StatusText.Text = albumCount > 0 && looseCount > 0
            ? $"{albumCount} album(s), {looseCount} loose track(s)"
            : albumCount > 0
                ? $"{albumCount} album(s)"
                : $"{looseCount} loose track(s)";
    }

    private static IReadOnlyList<object> BuildNodes(IReadOnlyList<PieceAlbumHit> hits)
    {
        var nodes = new List<object>();
        var albumNodesByAlbum = new Dictionary<CanonAlbum, AlbumNode>();

        foreach (var hit in hits)
        {
            if (hit.Album is CanonAlbum album)
            {
                if (!albumNodesByAlbum.TryGetValue(album, out var node))
                {
                    node = new AlbumNode(album);
                    albumNodesByAlbum[album] = node;
                    nodes.Add(node);
                }
                node.TrackHits.Add(new TrackHitNode(hit, album));
            }
            else
            {
                nodes.Add(new LooseTrackNode(hit));
            }
        }

        return nodes;
    }

    // ── Selection / commands ─────────────────────────────────────────────────

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var album = ResolveAlbum(e.NewValue);
        var loose = e.NewValue as LooseTrackNode;
        OpenButton.IsEnabled = album != null;
        PlayButton.IsEnabled = album != null || loose != null;
    }

    private static CanonAlbum? ResolveAlbum(object? item) => item switch
    {
        AlbumNode a     => a.Album,
        TrackHitNode t  => t.Album,
        _               => null,
    };

    private void OnOpenClick(object sender, RoutedEventArgs e) => CommitOpen();

    private void OnTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only treat a double-click as Open when the selection is something
        // we can open — avoids hijacking the click that toggles a TreeViewItem's
        // expand state via the framework's default double-click behaviour.
        if (ResolveAlbum(HitTree.SelectedItem) is null) return;
        CommitOpen();
        e.Handled = true;
    }

    private void CommitOpen()
    {
        var album = ResolveAlbum(HitTree.SelectedItem);
        if (album is null) return;
        SelectedAlbum = album;
        DialogResult = true;
        Close();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        var selected = HitTree.SelectedItem;
        PlayRequestResult result;
        string label;

        if (selected is LooseTrackNode loose)
        {
            result = _player.PlayLooseTrack(loose.Hit.Track);
            label  = "loose track";
        }
        else if (selected is TrackHitNode track && track.Hit.Disc is AlbumDisc disc)
        {
            // Selected a specific referenced track — start playback there
            // rather than from the album's first track.
            result = _player.PlayFromTrack(track.Album, disc, track.Hit.Track);
            label  = track.Album.DisplayTitle;
        }
        else if (ResolveAlbum(selected) is CanonAlbum album)
        {
            result = _player.PlayAlbum(album);
            label  = album.DisplayTitle;
        }
        else
        {
            return;
        }

        if (result == PlayRequestResult.Playing)
        {
            PlayRequested = true;
            DialogResult  = true;
            Close();
            return;
        }

        var reason = result switch
        {
            PlayRequestResult.NoAudioFile =>
                "No audio file could be located. Check the album's Archive Folder " +
                "field (Albums → double-click → Details tab) or the track's audio overrides.",
            PlayRequestResult.AlbumHasNoTracks => "This album has no tracks.",
            _                                  => result.ToString(),
        };
        MessageBox.Show($"Can't play \"{label}\":\n\n{reason}",
            "Playback", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Node classes (public for the XAML DataTemplate DataType="..." refs) ──

    /// <summary>Album row: expandable, children are <see cref="TrackHitNode"/>.</summary>
    public sealed class AlbumNode
    {
        public CanonAlbum Album { get; }
        public List<TrackHitNode> TrackHits { get; } = new();
        public string Display { get; }

        public AlbumNode(CanonAlbum album)
        {
            Album = album;

            var title = album.DisplayTitle ?? "";
            string label = string.IsNullOrWhiteSpace(album.Label)
                ? (album.CatalogueNumber ?? "")
                : $"{album.Label} {album.CatalogueNumber}".TrimEnd();
            Display = string.IsNullOrWhiteSpace(label) ? title : $"{title} — {label}";
        }
    }

    /// <summary>Track-reference row, child of an <see cref="AlbumNode"/>.</summary>
    public sealed class TrackHitNode
    {
        public PieceAlbumHit Hit { get; }
        public CanonAlbum Album { get; }
        public string Display { get; }

        public TrackHitNode(PieceAlbumHit hit, CanonAlbum album)
        {
            Hit   = hit;
            Album = album;

            // Disc prefix: omit for single-disc albums.
            bool multiDisc = album.Discs.Count > 1;
            var sb = new System.Text.StringBuilder();
            if (multiDisc && hit.Disc is AlbumDisc disc)
                sb.Append("Disc: ").Append(disc.DiscNumber).Append("  ");
            sb.Append("Track: ").Append(hit.Track.TrackNumber).Append("  ");
            sb.Append(FormatPieceRef(hit.Ref));
            Display = sb.ToString();
        }
    }

    /// <summary>Loose-track row: not under an album.</summary>
    public sealed class LooseTrackNode
    {
        public PieceAlbumHit Hit { get; }
        public string Display { get; }

        public LooseTrackNode(PieceAlbumHit hit)
        {
            Hit = hit;
            var piece = FormatPieceRef(hit.Ref);
            var performers = hit.Track.Performers is { Count: > 0 } perfs
                ? string.Join(", ", perfs.Select(p => p.DisplayName))
                : "";
            Display = string.IsNullOrWhiteSpace(performers)
                ? piece
                : $"{piece} — {performers}";
        }
    }

    /// <summary>
    /// Formats one <see cref="TrackPieceRef"/> as
    /// <c>Composer: PieceDisplayTitle[ - SubpiecePath]</c>. The piece's
    /// catalogue-bearing title is obtained from the resolver when available;
    /// falls back to the ref's raw <c>PieceTitle</c> otherwise.
    /// </summary>
    private static string FormatPieceRef(TrackPieceRef r)
    {
        // Probe with no subpath so we get the top-level (catalogue-bearing)
        // title, then append the subpath ourselves with "-" separators.
        var idx = PieceReferenceIndex.Current;
        string title = r.PieceTitle ?? "";
        if (idx != null)
        {
            var probe = new TrackPieceRef
            {
                Composer   = r.Composer   ?? "",
                PieceTitle = r.PieceTitle ?? "",
            };
            var resolved = idx.TryResolve(probe);
            if (resolved.HasValue) title = resolved.Value.Piece.DisplayTitle;
        }

        if (r.SubpiecePath is { Count: > 0 } path)
        {
            var joined = string.Join(" - ", path);
            title = title.Length > 0 ? $"{title} - {joined}" : joined;
        }

        var composer = r.Composer ?? "";
        if (composer.Length == 0) return title;
        return title.Length == 0 ? composer : $"{composer}: {title}";
    }
}
