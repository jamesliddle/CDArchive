using System.Collections.ObjectModel;
using CDArchive.App.Services;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.App.ViewModels;

public partial class AlbumsViewModel : ObservableObject
{
    private readonly ICanonDataService _svc;
    private readonly PieceReferenceIndex _refIndex;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AlbumsViewModel> _logger;

    /// <summary>
    /// Exposed so the Albums view's code-behind can hand it through to
    /// <see cref="Views.AlbumEditorWindow"/> — the editor's right-click
    /// "Play track" / "Play from here" actions need it. Pre-fix the
    /// editor pulled it via the App.ServiceProvider static (Rework H12).
    /// </summary>
    public PlayerViewModel Player { get; }

    // Full unfiltered list; Albums is the sorted+filtered view.
    private List<CanonAlbum> _allAlbums = [];

    [ObservableProperty] private ObservableCollection<CanonAlbum> _albums = [];
    [ObservableProperty] private CanonAlbum? _selectedAlbum;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private ProvisionalFilter _provisionalFilter = ProvisionalFilter.All;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";

    // Current sort state; the view updates these before calling ApplyFilter().
    public string SortColumn    { get; set; } = "DisplayTitle";
    public bool   SortAscending { get; set; } = true;

    public AlbumsViewModel(
        ICanonDataService svc,
        PieceReferenceIndex refIndex,
        PlayerViewModel player,
        IDialogService dialogs,
        ILogger<AlbumsViewModel>? logger = null)
    {
        _svc = svc;
        _refIndex = refIndex;
        Player = player;
        _dialogs = dialogs;
        _logger = logger ?? NullLogger<AlbumsViewModel>.Instance;
    }

    // ── Data access ──────────────────────────────────────────────────────────

    /// <summary>Exposes the full loaded list for operations that need it (e.g. save after edit).</summary>
    public List<CanonAlbum> AllAlbums => _allAlbums;

    /// <summary>
    /// True once <see cref="LoadDataAsync"/> has completed successfully at
    /// least once. Other VMs (notably <c>CanonViewModel</c>) use this to
    /// decide whether they can pull from <see cref="AllAlbums"/> or whether
    /// they need to trigger a fresh DB load themselves — see Rework H9.
    /// </summary>
    public bool HasLoaded { get; private set; }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            _allAlbums = await _svc.LoadAlbumsAsync();
            HasLoaded = true;
            ApplyFilter();
            // Refresh cross-reference index so Canon badges reflect loaded albums.
            // Reuse the piece list already cached in the index — loading a fresh
            // list here would create new CanonPiece instances, invalidating the
            // reference-identity dictionary keys the Canon tree holds (badges
            // would all read 0 until the next Canon reload).
            try
            {
                // Load loose tracks alongside albums so badge counts include both
                // kinds of container. RebuildContainers reuses the cached piece
                // list from the last full Rebuild (CanonViewModel.LoadDataAsync),
                // so badge dictionary keys stay reference-equal.
                var looseTracks = await _svc.LoadLooseTracksAsync();
                _refIndex.RebuildContainers(_allAlbums, looseTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PieceReferenceIndex container rebuild failed after LoadAlbums; badge counts may be stale");
            }
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
        try
        {
            _refIndex.RebuildAlbums(_allAlbums);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PieceReferenceIndex album rebuild failed after SaveAlbums; badge counts may be stale");
        }
    }

    // ── Filtering ────────────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnProvisionalFilterChanged(ProvisionalFilter value) => ApplyFilter();

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

        filtered = ProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => filtered.Where(a => a.IsProvisional),
            ProvisionalFilter.Accepted    => filtered.Where(a => !a.IsProvisional),
            _                             => filtered,
        };

        var sorted = ApplySort(filtered).ToList();

        Albums = new ObservableCollection<CanonAlbum>(sorted);

        StatusMessage = filter.Length > 0 || ProvisionalFilter != ProvisionalFilter.All
            ? $"{Albums.Count} of {_allAlbums.Count} album(s)"
            : $"{_allAlbums.Count} album(s)";
    }

    // ── Approval / rejection ─────────────────────────────────────────────────

    /// <summary>Approves the given album: clears IsProvisional and persists.</summary>
    public async Task ApproveAlbumAsync(CanonAlbum album)
    {
        album.IsProvisional = false;
        await SaveAsync();
        ApplyFilter();
        StatusMessage = $"Approved {album.DisplayTitle}.";
    }

    /// <summary>
    /// Multi-select Approve command surfaced via H36 — XAML binds the
    /// context-menu MenuItem's <c>CommandParameter</c> to the ListView's
    /// <c>SelectedItems</c>. Only items still flagged provisional are
    /// flipped to approved; rows that are already approved are skipped.
    /// </summary>
    [RelayCommand]
    private async Task ApproveAlbumsAsync(System.Collections.IList? selection)
    {
        var albums = (selection ?? Array.Empty<object>())
            .OfType<CanonAlbum>()
            .Where(a => a.IsProvisional)
            .ToList();
        if (albums.Count == 0) return;

        foreach (var a in albums)
            a.IsProvisional = false;
        await SaveAsync();
        ApplyFilter();
        StatusMessage = albums.Count == 1
            ? $"Approved {albums[0].DisplayTitle}."
            : $"Approved {albums.Count} album(s).";
    }

    /// <summary>
    /// Rejects the given album: prompts for confirmation, removes it from the
    /// in-memory list, and persists (which deletes the row via the SaveAlbumsAsync
    /// orphan-cleanup pass).
    /// </summary>
    public async Task RejectAlbumAsync(CanonAlbum album)
    {
        var title = album.DisplayTitle;
        if (!_dialogs.Confirm($"Delete provisional album '{title}'?", "Confirm Rejection"))
            return;
        _allAlbums.Remove(album);
        await SaveAsync();
        ApplyFilter();
        StatusMessage = $"Rejected and deleted {title}.";
    }

    /// <summary>
    /// Multi-select Reject command surfaced via H36. Filters to provisional
    /// rows (the context menu only enables the item when a provisional row
    /// is selected, but this guards against stale selections); prompts once
    /// with a count-aware message; removes all confirmed rows then saves.
    /// </summary>
    [RelayCommand]
    private async Task RejectAlbumsAsync(System.Collections.IList? selection)
    {
        var albums = (selection ?? Array.Empty<object>())
            .OfType<CanonAlbum>()
            .Where(a => a.IsProvisional)
            .ToList();
        if (albums.Count == 0) return;

        var prompt = albums.Count == 1
            ? $"Delete provisional album '{albums[0].DisplayTitle}'?"
            : $"Delete {albums.Count} provisional album(s)?";
        if (!_dialogs.Confirm(prompt, "Confirm Rejection")) return;

        foreach (var a in albums)
            _allAlbums.Remove(a);
        await SaveAsync();
        ApplyFilter();
        StatusMessage = albums.Count == 1
            ? $"Rejected and deleted {albums[0].DisplayTitle}."
            : $"Rejected and deleted {albums.Count} album(s).";
    }

    /// <summary>
    /// Validates every <see cref="TrackPieceRef"/> across all loaded albums
    /// and presents a human-readable report via <see cref="IDialogService"/>.
    /// Surfaced as a RelayCommand via H36 so the toolbar button binds to
    /// <c>Command="{Binding CheckReferencesCommand}"</c> instead of routing
    /// through code-behind.
    /// </summary>
    [RelayCommand]
    private async Task CheckReferencesAsync()
    {
        if (_allAlbums.Count == 0)
        {
            _dialogs.ShowInfo("No albums loaded.", "Check References");
            return;
        }

        var report = await RunConsistencyCheckAsync();
        // The report's first word is "All" when the check passes ("All N
        // album refs resolved cleanly."). Anything else means at least one
        // issue — show as a warning-styled dialog.
        if (report.StartsWith("All", StringComparison.Ordinal))
            _dialogs.ShowInfo(report, "Check Album References");
        else
            _dialogs.ShowError(report, "Check Album References");
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
