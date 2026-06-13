using System.Collections.ObjectModel;
using CDArchive.App.Helpers;
using CDArchive.App.Services;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.App.ViewModels;

/// <summary>
/// VM for the cross-album Tracks list. Sources its album list from the shared
/// <see cref="AlbumsViewModel"/> singleton so edits made in either view remain
/// coherent (the same <see cref="CanonAlbum"/> / <see cref="AlbumTrack"/>
/// instances back both UIs). Save also delegates to the shared VM so the
/// PieceReferenceIndex rebuild stays centralised.
/// </summary>
public partial class TracksViewModel : ObservableObject
{
    private readonly AlbumsViewModel _albumsVm;
    private readonly ICanonDataService _svc;
    private readonly PieceReferenceIndex _refIndex;
    private readonly IDialogService _dialogs;
    private readonly ILogger<TracksViewModel> _logger;

    // Loose tracks (singletons with no owning album). Loaded alongside albums
    // and folded into the row list. Persisted separately via SaveLooseTracksAsync.
    private List<AlbumTrack> _looseTracks = [];

    // Flat unfiltered list; Rows is the sorted + filtered view.
    private List<AlbumTrackRow> _allRows = [];

    [ObservableProperty] private ObservableCollection<AlbumTrackRow> _rows = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private ProvisionalFilter _provisionalFilter = ProvisionalFilter.All;
    [ObservableProperty] private bool _onlyNeedsVariant;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";

    // Current sort state; the view updates these before calling ApplyFilter().
    // Default groups by album, then disc-then-track within each album.
    public string SortColumn    { get; set; } = "AlbumTitle";
    public bool   SortAscending { get; set; } = true;

    /// <summary>
    /// Exposed so <c>TracksView.xaml.cs</c>'s "edit album" path can hand it
    /// through to <see cref="Views.AlbumEditorWindow"/> for its playback
    /// context menu. Rework H12.
    /// </summary>
    public PlayerViewModel Player { get; }

    public TracksViewModel(
        AlbumsViewModel albumsVm,
        ICanonDataService svc,
        PieceReferenceIndex refIndex,
        PlayerViewModel player,
        IDialogService dialogs,
        ILogger<TracksViewModel>? logger = null)
    {
        _albumsVm = albumsVm;
        _svc      = svc;
        _refIndex = refIndex;
        Player    = player;
        _dialogs  = dialogs;
        _logger   = logger ?? NullLogger<TracksViewModel>.Instance;
    }

    /// <summary>Public read-only access to the loose-track list (for tests and the view).</summary>
    public IReadOnlyList<AlbumTrack> LooseTracks => _looseTracks;

    /// <summary>
    /// True once <see cref="LoadDataAsync"/> has completed successfully at
    /// least once. <c>CanonViewModel</c> uses this to decide whether
    /// <see cref="LooseTracks"/> reflects the current DB state or whether
    /// a fresh load is needed — see Rework H9.
    /// </summary>
    public bool HasLoaded { get; private set; }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            // Reuse the shared album list when populated; otherwise prime it.
            if (_albumsVm.AllAlbums.Count == 0)
                await _albumsVm.LoadDataCommand.ExecuteAsync(null);

            // Loose tracks aren't held by AlbumsViewModel — fetch separately.
            _looseTracks = await _svc.LoadLooseTracksAsync();
            HasLoaded = true;

            RebuildRows();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading tracks: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-flatten albums + loose tracks into per-track rows. Loose tracks come
    /// with null Album/Disc; the row's display rules handle the empty columns.
    /// </summary>
    public void RebuildRows()
    {
        var albumRows = _albumsVm.AllAlbums
            .SelectMany(album => album.Discs.SelectMany(disc =>
                disc.Tracks.Select(track => new AlbumTrackRow(
                    album, disc, track, FormatPiece(track), NeedsVariant(track)))));

        var looseRows = _looseTracks
            .Select(track => new AlbumTrackRow(
                album: null, disc: null, track, FormatPiece(track), NeedsVariant(track)));

        _allRows = albumRows.Concat(looseRows).ToList();
    }

    /// <summary>
    /// True when any of the track's refs points at a piece / version / movement
    /// that defines variants but identifies none. Computed once per row build
    /// against the live resolver (no per-keystroke re-resolution).
    /// </summary>
    private bool NeedsVariant(AlbumTrack track)
        => track.PieceRefs is { Count: > 0 }
        && track.PieceRefs.Any(r => _refIndex.NeedsVariantIdentification(r));

    /// <summary>Backwards-compatible alias — callers that built only album rows still work.</summary>
    public void RebuildRowsFromAlbums() => RebuildRows();

    /// <summary>
    /// Builds the single-column piece label for a track: the resolved top-level
    /// piece's <see cref="CanonPiece.DisplayTitle"/> (with catalogue) joined to
    /// the ref's <see cref="TrackPieceRef.SubpiecePath"/> with <c>" › "</c>.
    /// Multi-ref tracks join their per-ref labels with <c>" / "</c>.
    /// Falls back to <see cref="AlbumTrack.Description"/> for uncatalogued tracks,
    /// or to the raw <see cref="TrackPieceRef.PieceTitle"/> when the resolver
    /// can't find the piece (broken ref).
    /// </summary>
    private string FormatPiece(AlbumTrack track)
    {
        if (!track.IsCatalogued) return track.Description ?? "";

        var parts = new List<string>(track.PieceRefs!.Count);
        foreach (var r in track.PieceRefs!)
        {
            // Probe the resolver with the no-subpath form so it returns the
            // top-level piece (TryResolve returns the leaf when SubpiecePath is
            // walked). We want the catalogue-bearing top-level title, then we
            // append our own subpath manually.
            var probe = new TrackPieceRef
            {
                Composer   = r.Composer   ?? "",
                PieceTitle = r.PieceTitle ?? "",
            };
            var resolved = _refIndex.TryResolve(probe);

            var title = resolved.HasValue
                ? resolved.Value.Piece.DisplayTitle
                : r.PieceTitle ?? "";

            if (r.SubpiecePath is { Count: > 0 } path)
            {
                title = title.Length > 0
                    ? $"{title} › {string.Join(" › ", path)}"
                    : string.Join(" › ", path);
            }

            parts.Add(title);
        }
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Persists albums + loose tracks atomically (single transaction via
    /// <see cref="ICanonDataService.SaveBatchAsync"/>) and then rebuilds the
    /// PieceReferenceIndex. Was previously two separate awaited saves with no
    /// shared transaction — a mid-sequence failure on the loose-track write
    /// would leave the album changes committed while loose tracks reverted to
    /// disk. Callers that only changed one can still use the targeted methods
    /// below.
    /// </summary>
    public async Task SaveAsync(CanonPickLists? pickLists = null)
    {
        await _svc.SaveBatchAsync(
            albums: _albumsVm.AllAlbums,
            looseTracks: _looseTracks,
            pickLists: pickLists);
        // Re-rebuild the index so any track-piece ref or loose-track changes
        // are reflected. RebuildContainers uses the in-memory album list so a
        // failed save above would skip this branch anyway.
        try
        {
            _refIndex.RebuildContainers(_albumsVm.AllAlbums, _looseTracks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PieceReferenceIndex container rebuild failed after Tracks save; badge counts may be stale");
        }
    }

    /// <summary>
    /// Persists only the loose tracks. Used by the "New Track" / loose-track
    /// edit flows that don't touch any album. When <paramref name="pickLists"/>
    /// is supplied, pick lists land in the same transaction — used after
    /// editor flows that may have mutated them (e.g. a novel Instrument
    /// value from the Performer editor).
    /// </summary>
    public async Task SaveLooseTracksAsync(CanonPickLists? pickLists = null)
    {
        if (pickLists is null)
            await _svc.SaveLooseTracksAsync(_looseTracks);
        else
            await _svc.SaveBatchAsync(looseTracks: _looseTracks, pickLists: pickLists);

        try
        {
            _refIndex.RebuildContainers(_albumsVm.AllAlbums, _looseTracks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PieceReferenceIndex container rebuild failed after loose-track save; badge counts may be stale");
        }
    }

    /// <summary>
    /// Adds a freshly-edited loose track to the in-memory list. Caller is
    /// responsible for invoking <see cref="SaveLooseTracksAsync"/> afterward.
    /// </summary>
    public void AddLooseTrack(AlbumTrack track) => _looseTracks.Add(track);

    // ── Approve / Reject ─────────────────────────────────────────────────────

    /// <summary>
    /// Clears <c>IsProvisional</c> on each row's underlying track, then saves
    /// only the affected stores (albums vs loose-tracks) so an approve on a
    /// few loose tracks doesn't churn the full albums table. Returns the
    /// number of tracks whose state actually changed.
    /// </summary>
    public async Task<int> ApproveRowsAsync(IReadOnlyList<AlbumTrackRow> rows)
    {
        var tracks = rows.Select(r => r.Track).ToList();
        var changed = TrackCascade.Approve(tracks);
        if (changed == 0) return 0;

        var touchedAlbum = rows.Any(r => r.Album is not null);
        var touchedLoose = rows.Any(r => r.Album is null);

        if (touchedAlbum) await _albumsVm.SaveAsync();
        if (touchedLoose) await _svc.SaveLooseTracksAsync(_looseTracks);

        return changed;
    }

    /// <summary>
    /// Removes each row's track from its container (disc for album-bound,
    /// loose-tracks list for loose), then saves the affected stores. The
    /// orphan-delete pass on each save deletes the underlying DB row.
    /// Returns the total number of tracks removed.
    /// </summary>
    public async Task<int> RejectRowsAsync(IReadOnlyList<AlbumTrackRow> rows)
    {
        var entries = rows.Select(r => (r.Track, r.Disc));
        var result  = TrackCascade.Reject(entries, _looseTracks);
        if (result.Total == 0) return 0;

        if (result.AlbumBoundRemoved > 0) await _albumsVm.SaveAsync();
        if (result.LooseRemoved      > 0) await _svc.SaveLooseTracksAsync(_looseTracks);

        return result.Total;
    }

    /// <summary>
    /// Multi-select Approve command surfaced via H36 (TracksView slice). XAML
    /// binds the context-menu MenuItem's <c>CommandParameter</c> to the
    /// ListView's <c>SelectedItems</c>; the command filters to AlbumTrackRow
    /// entries that are still provisional, delegates to the existing
    /// <see cref="ApproveRowsAsync"/>, rebuilds the row list, applies the
    /// filter, and updates status. Errors routed through <see cref="IDialogService"/>.
    /// </summary>
    [RelayCommand]
    private async Task ApproveTracksAsync(System.Collections.IList? selection)
    {
        var rows = (selection ?? Array.Empty<object>())
            .OfType<AlbumTrackRow>()
            .ToList();
        if (rows.Count == 0) return;

        try
        {
            var changed = await ApproveRowsAsync(rows);
            RebuildRows();
            ApplyFilter();
            StatusMessage = changed > 0
                ? $"Approved {changed} track(s)."
                : "No provisional tracks in selection — nothing to approve.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApproveTracks failed");
            StatusMessage = $"Could not approve: {ex.Message}";
            _dialogs.ShowError(StatusMessage, "Approve Tracks");
        }
    }

    /// <summary>
    /// Multi-select Reject command surfaced via H36 (TracksView slice). Builds
    /// a confirmation summary that breaks out album-bound vs loose so the user
    /// understands what's about to happen, prompts via
    /// <see cref="IDialogService.Confirm"/>, then delegates to the existing
    /// <see cref="RejectRowsAsync"/> + rebuild + filter + status. Errors routed
    /// through the dialog service.
    /// </summary>
    [RelayCommand]
    private async Task RejectTracksAsync(System.Collections.IList? selection)
    {
        var rows = (selection ?? Array.Empty<object>())
            .OfType<AlbumTrackRow>()
            .ToList();
        if (rows.Count == 0) return;

        var albumBound = rows.Count(r => r.Album is not null);
        var loose      = rows.Count(r => r.Album is null);
        var detail = (albumBound, loose) switch
        {
            ( > 0, > 0) => $"{albumBound} album track(s) and {loose} loose track(s)",
            ( > 0, _ )  => $"{albumBound} album track(s)",
            ( _,  > 0)  => $"{loose} loose track(s)",
            _           => $"{rows.Count} track(s)",
        };

        var prompt = rows.Count == 1
            ? $"Delete '{rows[0].Piece}'?\n\nThis cannot be undone."
            : $"Delete {detail}?\n\nThis cannot be undone.";

        if (!_dialogs.Confirm(prompt, "Reject Tracks")) return;

        try
        {
            var removed = await RejectRowsAsync(rows);
            RebuildRows();
            ApplyFilter();
            StatusMessage = $"Rejected and deleted {removed} track(s).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RejectTracks failed");
            StatusMessage = $"Could not reject: {ex.Message}";
            _dialogs.ShowError(StatusMessage, "Reject Tracks");
        }
    }

    /// <summary>Editor-data loader passthrough.</summary>
    public Task<(IReadOnlyList<CanonPiece> Pieces, CanonPickLists PickLists)> LoadEditorDataAsync()
        => _albumsVm.LoadEditorDataAsync();

    /// <summary>Replaces an album in <see cref="AlbumsViewModel.AllAlbums"/> with the editor's clone.</summary>
    public void ReplaceAlbum(CanonAlbum oldAlbum, CanonAlbum newAlbum)
    {
        var idx = _albumsVm.AllAlbums.IndexOf(oldAlbum);
        if (idx >= 0) _albumsVm.AllAlbums[idx] = newAlbum;
        else          _albumsVm.AllAlbums.Add(newAlbum);
    }

    // ── Filtering / sort ─────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnProvisionalFilterChanged(ProvisionalFilter value) => ApplyFilter();
    partial void OnOnlyNeedsVariantChanged(bool value) => ApplyFilter();

    public void ApplyFilter()
    {
        var filter = FilterText.Trim();

        IEnumerable<AlbumTrackRow> filtered = string.IsNullOrEmpty(filter)
            ? _allRows
            : _allRows.Where(r =>
                Contains(r.Piece, filter)            ||
                Contains(r.Composer, filter)         ||
                Contains(r.AlbumTitle, filter)       ||
                Contains(r.PerformerSummary, filter) ||
                Contains(r.Track.Description, filter));

        filtered = ProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => filtered.Where(r => r.IsProvisional),
            ProvisionalFilter.Accepted    => filtered.Where(r => !r.IsProvisional),
            _                             => filtered,
        };

        if (OnlyNeedsVariant)
            filtered = filtered.Where(r => r.NeedsVariantIdentification);

        // M7: reset in place — ApplyFilterAndSort fires on every keystroke
        // in the filter textbox.
        Rows.Reset(ApplySort(filtered));

        StatusMessage = filter.Length > 0 || ProvisionalFilter != ProvisionalFilter.All || OnlyNeedsVariant
            ? $"{Rows.Count} of {_allRows.Count} track(s)"
            : $"{_allRows.Count} track(s)";
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Sort by <see cref="SortColumn"/>, with Album→Disc→Track as the universal
    /// tiebreaker so equal-key rows stay grouped by their parent album.
    /// </summary>
    private IEnumerable<AlbumTrackRow> ApplySort(IEnumerable<AlbumTrackRow> source)
    {
        bool asc = SortAscending;

        IOrderedEnumerable<AlbumTrackRow> primary = SortColumn switch
        {
            "DiscSort"         => asc ? source.OrderBy(r => r.DiscSort)              : source.OrderByDescending(r => r.DiscSort),
            "TrackNumber"      => asc ? source.OrderBy(r => r.TrackNumber)           : source.OrderByDescending(r => r.TrackNumber),
            "Piece"            => asc ? source.OrderBy(r => r.Piece, OIC)            : source.OrderByDescending(r => r.Piece, OIC),
            "Duration"         => asc ? source.OrderBy(r => DurationSeconds(r.Duration)) : source.OrderByDescending(r => DurationSeconds(r.Duration)),
            "Composer"         => asc ? source.OrderBy(r => r.Composer, OIC)         : source.OrderByDescending(r => r.Composer, OIC),
            "PerformerSummary" => asc ? source.OrderBy(r => r.PerformerSummary, OIC) : source.OrderByDescending(r => r.PerformerSummary, OIC),
            _                  => asc ? source.OrderBy(r => r.AlbumTitle, OIC)       : source.OrderByDescending(r => r.AlbumTitle, OIC),
        };

        // Universal tiebreaker: keep tracks inside an album in disc/track order.
        return primary.ThenBy(r => r.AlbumTitle, OIC)
                      .ThenBy(r => r.DiscSort)
                      .ThenBy(r => r.TrackNumber);
    }

    /// <summary>
    /// Parses "m:ss" / "mm:ss" / "h:mm:ss" duration strings to total seconds for
    /// numeric ordering. Bad / empty input sorts as 0.
    /// </summary>
    private static int DurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split(':');
        if (parts.Length == 0 || parts.Length > 3) return 0;
        int total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;
}
