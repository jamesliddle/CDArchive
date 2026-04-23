using System.Collections.ObjectModel;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class AlbumsViewModel : ObservableObject
{
    private readonly ICanonDataService _svc;
    private readonly PieceReferenceIndex _refIndex;

    // Full unfiltered list; Albums is the sorted+filtered view.
    private List<CanonAlbum> _allAlbums = [];

    [ObservableProperty] private ObservableCollection<CanonAlbum> _albums = [];
    [ObservableProperty] private CanonAlbum? _selectedAlbum;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";

    // Current sort state; the view updates these before calling ApplyFilter().
    public string SortColumn    { get; set; } = "Label";
    public bool   SortAscending { get; set; } = true;

    public AlbumsViewModel(ICanonDataService svc, PieceReferenceIndex refIndex)
    {
        _svc = svc;
        _refIndex = refIndex;
    }

    // ── Data access ──────────────────────────────────────────────────────────

    /// <summary>Exposes the full loaded list for operations that need it (e.g. save after edit).</summary>
    public List<CanonAlbum> AllAlbums => _allAlbums;

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            _allAlbums = await _svc.LoadAlbumsAsync();
            ApplyFilter();
            // Refresh cross-reference index so Canon badges reflect loaded albums.
            // Reuse the piece list already cached in the index — loading a fresh
            // list here would create new CanonPiece instances, invalidating the
            // reference-identity dictionary keys the Canon tree holds (badges
            // would all read 0 until the next Canon reload).
            try { _refIndex.RebuildAlbums(_allAlbums); } catch { /* non-fatal */ }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading albums: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SaveAsync()
    {
        await _svc.SaveAlbumsAsync(_allAlbums);
        // Track → piece links may have changed; rebuild the cross-reference index
        // using the cached piece instances (see comment in LoadDataAsync).
        try { _refIndex.RebuildAlbums(_allAlbums); } catch { /* non-fatal */ }
    }

    // ── Filtering ────────────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        var filter = FilterText.Trim();

        IEnumerable<CanonAlbum> filtered = string.IsNullOrEmpty(filter)
            ? _allAlbums
            : _allAlbums.Where(a =>
                (a.Title?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)           ||
                (a.Subtitle?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)        ||
                (a.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)           ||
                (a.CatalogueNumber?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Performers?.Any(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false));

        var sorted = ApplySort(filtered).ToList();

        Albums = new ObservableCollection<CanonAlbum>(sorted);

        StatusMessage = filter.Length > 0
            ? $"{Albums.Count} of {_allAlbums.Count} album(s)"
            : $"{_allAlbums.Count} album(s)";
    }

    private IEnumerable<CanonAlbum> ApplySort(IEnumerable<CanonAlbum> source)
    {
        bool asc = SortAscending;
        return SortColumn switch
        {
            "DisplayTitle" => asc
                ? source.OrderBy(a => a.DisplayTitle     ?? "", OIC).ThenBy(a => a.Label ?? "", OIC)
                : source.OrderByDescending(a => a.DisplayTitle ?? "", OIC).ThenBy(a => a.Label ?? "", OIC),

            "CatalogueNumber" => asc
                ? source.OrderBy(a => a.Label ?? "", OIC).ThenBy(a => a.CatalogueNumber ?? "", OIC)
                : source.OrderBy(a => a.Label ?? "", OIC).ThenByDescending(a => a.CatalogueNumber ?? "", OIC),

            "PerformerSummary" => asc
                ? source.OrderBy(a => a.PerformerSummary ?? "", OIC).ThenBy(a => a.DisplayTitle ?? "", OIC)
                : source.OrderByDescending(a => a.PerformerSummary ?? "", OIC).ThenBy(a => a.DisplayTitle ?? "", OIC),

            "SparsCode" => asc
                ? source.OrderBy(a => a.SparsCode ?? "").ThenBy(a => a.Label ?? "", OIC)
                : source.OrderByDescending(a => a.SparsCode ?? "").ThenBy(a => a.Label ?? "", OIC),

            "DiscCount" => asc
                ? source.OrderBy(a => a.DiscCount).ThenBy(a => a.Label ?? "", OIC)
                : source.OrderByDescending(a => a.DiscCount).ThenBy(a => a.Label ?? "", OIC),

            "TotalTrackCount" => asc
                ? source.OrderBy(a => a.TotalTrackCount).ThenBy(a => a.Label ?? "", OIC)
                : source.OrderByDescending(a => a.TotalTrackCount).ThenBy(a => a.Label ?? "", OIC),

            // Default / "Label": Label → CatalogueNumber → Title
            _ => asc
                ? source.OrderBy(a => a.Label ?? "", OIC)
                        .ThenBy(a => a.CatalogueNumber ?? "", OIC)
                        .ThenBy(a => a.DisplayTitle ?? "", OIC)
                : source.OrderByDescending(a => a.Label ?? "", OIC)
                        .ThenBy(a => a.CatalogueNumber ?? "", OIC)
                        .ThenBy(a => a.DisplayTitle ?? "", OIC),
        };
    }

    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    // ── Editor data loader ────────────────────────────────────────────────────

    /// <summary>
    /// Loads the data needed to open an album editor: all Canon pieces and the
    /// current pick lists.  Called from AlbumsView before opening AlbumEditorWindow.
    /// </summary>
    public async Task<(IReadOnlyList<CanonPiece> Pieces, CanonPickLists PickLists)> LoadEditorDataAsync()
    {
        var pieces    = await _svc.LoadPiecesAsync();
        var pickLists = await _svc.LoadPickListsAsync();
        return (pieces, pickLists);
    }

    // ── Consistency check ────────────────────────────────────────────────────

    /// <summary>
    /// Loads the current Canon piece list and validates every
    /// <see cref="TrackPieceRef"/> across all albums.
    /// Returns a human-readable report string (empty = no issues found).
    /// </summary>
    public async Task<string> RunConsistencyCheckAsync()
    {
        var pieces = await _svc.LoadPiecesAsync();
        var broken = AlbumConsistencyChecker.FindBrokenRefs(_allAlbums, pieces);

        if (broken.Count == 0)
            return $"All references are valid ({_allAlbums.Count} album(s) checked).";

        var lines = broken
            .Select(b =>
            {
                var disc    = b.Disc.VolumeNumber.HasValue
                    ? $"Vol {b.Disc.VolumeNumber} Disc {b.Disc.DiscNumber}"
                    : $"Disc {b.Disc.DiscNumber}";
                var label   = string.IsNullOrWhiteSpace(b.Album.Label)
                    ? b.Album.DisplayTitle
                    : $"{b.Album.Label} {b.Album.CatalogueNumber}";
                return $"  {label} – {disc} Track {b.Track.TrackNumber}: {b.Reason}";
            });

        return $"{broken.Count} broken reference(s):\n\n" + string.Join("\n", lines);
    }
}
