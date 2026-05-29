using System.Collections.ObjectModel;
using CDArchive.App.Services;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.App.ViewModels;

/// <summary>Three-state filter controlling which items are shown by provisional status.</summary>
public enum ProvisionalFilter { All, Provisional, Accepted }

public partial class CanonViewModel : ObservableObject
{
    private readonly ICanonDataService _canonDataService;
    private readonly PieceReferenceIndex _refIndex;
    private readonly AlbumsViewModel _albumsVm;
    private readonly TracksViewModel _tracksVm;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CanonViewModel> _logger;

    /// <summary>
    /// Raised after a command mutates VM state (Composers / Pieces collections)
    /// but BEFORE the save's async await completes. <c>CanonView</c> subscribes
    /// to this to refresh badge counts + rebuild the tree synchronously,
    /// matching the pre-fix "mutate → rebuild → suppress → save" order. The
    /// pre-await timing is critical: a rebuild after the await produces a
    /// WPF rendering glitch where TreeViewItem expander triangles end up in
    /// a partially-stale state. H36 retirement preserves this contract.
    /// </summary>
    public event Action? DataMutated;

    /// <summary>
    /// Exposed so <c>CanonView.xaml.cs</c>'s "edit album from Canon" path
    /// can hand it through to <see cref="Views.AlbumEditorWindow"/> for its
    /// playback context menu. Rework H12.
    /// </summary>
    public PlayerViewModel Player { get; }

    // --- Composers ---

    [ObservableProperty]
    private ObservableCollection<CanonComposer> _composers = [];

    [ObservableProperty]
    private CanonComposer? _selectedComposer;

    [ObservableProperty]
    private string _composerFilter = "";

    [ObservableProperty]
    private ProvisionalFilter _composerProvisionalFilter = ProvisionalFilter.All;

    [ObservableProperty]
    private ObservableCollection<CanonComposer> _filteredComposers = [];

    // --- Pieces ---

    [ObservableProperty]
    private ObservableCollection<CanonPiece> _pieces = [];

    [ObservableProperty]
    private CanonPiece? _selectedPiece;

    [ObservableProperty]
    private string _piecesFilter = "";

    [ObservableProperty]
    private ProvisionalFilter _pieceProvisionalFilter = ProvisionalFilter.All;

    [ObservableProperty]
    private ObservableCollection<CanonPiece> _filteredPieces = [];

    [ObservableProperty]
    private ObservableCollection<CanonPiece> _selectedPieceSubpieces = [];

    // --- Pick Lists ---

    [ObservableProperty]
    private CanonPickLists _pickLists = new();

    // --- Status ---

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    public CanonViewModel(
        ICanonDataService canonDataService,
        PieceReferenceIndex refIndex,
        AlbumsViewModel albumsVm,
        TracksViewModel tracksVm,
        PlayerViewModel player,
        IDialogService dialogs,
        ILogger<CanonViewModel>? logger = null)
    {
        _canonDataService = canonDataService;
        _refIndex = refIndex;
        _albumsVm = albumsVm;
        _tracksVm = tracksVm;
        Player = player;
        _dialogs = dialogs;
        _logger = logger ?? NullLogger<CanonViewModel>.Instance;
    }

    /// <summary>
    /// Pulls the current album + loose-track lists for an index rebuild.
    /// Prefers the in-memory copies held by the <see cref="AlbumsViewModel"/>
    /// and <see cref="TracksViewModel"/> singletons (their LoadData has run
    /// at least once); falls back to a fresh DB load only when those VMs
    /// haven't loaded yet (e.g. user opened Canon view as the first thing
    /// without visiting Albums / Tracks). Pre-fix every site duplicated the
    /// fresh-load pair, costing ~2 multi-second loads per save / reject at
    /// the 3,000-CD target. See Rework H9.
    /// </summary>
    private async Task<(IReadOnlyList<CanonAlbum> albums, IReadOnlyList<AlbumTrack> looseTracks)>
        GetContainersForRebuildAsync()
    {
        if (_albumsVm.HasLoaded && _tracksVm.HasLoaded)
            return (_albumsVm.AllAlbums, _tracksVm.LooseTracks);

        _logger.LogDebug(
            "CanonViewModel falling back to fresh DB load for albums/loose-tracks " +
            "(albumsVm.HasLoaded={Albums}, tracksVm.HasLoaded={Loose}); the Albums " +
            "and Tracks views haven't initialised yet.",
            _albumsVm.HasLoaded, _tracksVm.HasLoaded);

        var albums      = await _canonDataService.LoadAlbumsAsync().ConfigureAwait(false);
        var looseTracks = await _canonDataService.LoadLooseTracksAsync().ConfigureAwait(false);
        return (albums, looseTracks);
    }

    partial void OnComposerFilterChanged(string value) => ApplyComposerFilter();
    partial void OnPiecesFilterChanged(string value) => ApplyPiecesFilter();
    partial void OnComposerProvisionalFilterChanged(ProvisionalFilter value) => ApplyComposerFilter();
    partial void OnPieceProvisionalFilterChanged(ProvisionalFilter value) => ApplyPiecesFilter();

    partial void OnSelectedPieceChanged(CanonPiece? value)
    {
        SelectedPieceSubpieces = value?.Subpieces != null
            ? new ObservableCollection<CanonPiece>(value.Subpieces)
            : [];
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading Canon data...";

            var composers = await _canonDataService.LoadComposersAsync();
            Composers = new ObservableCollection<CanonComposer>(composers);
            ApplyComposerFilter();

            var pieces = await _canonDataService.LoadPiecesAsync();

            // Apply each composer's CatalogPrefixes preference to their pieces. This reorders
            // CatalogInfo in-memory so DisplayTitle leads with the preferred catalog (e.g. Op.
            // before B. for Chopin). The JSON file isn't touched here — a save path (edit piece,
            // edit composer, or the one-off migration script) normalizes the on-disk order.
            var prefsByComposer = composers
                .Where(c => c.CatalogPrefixes is { Count: > 0 })
                .ToDictionary(c => c.Name, c => c.CatalogPrefixes!,
                    StringComparer.OrdinalIgnoreCase);
            if (prefsByComposer.Count > 0)
            {
                foreach (var piece in pieces)
                {
                    if (piece.Composer is { } name &&
                        prefsByComposer.TryGetValue(name, out var prefs))
                    {
                        piece.SortCatalogInfoByPreference(prefs);
                    }
                }
            }

            Pieces = new ObservableCollection<CanonPiece>(pieces);
            ApplyPiecesFilter();

            PickLists = await _canonDataService.LoadPickListsAsync();

            StatusMessage = $"Loaded {Composers.Count} composers and {Pieces.Count} pieces.";

            // Rebuild cross-reference index (pieces × albums + loose tracks) so
            // badge counts include both kinds of container. Non-fatal: data is
            // already loaded; failure here just means stale badge counts.
            try
            {
                var (albums, looseTracks) = await GetContainersForRebuildAsync();
                _refIndex.Rebuild(Pieces, albums, looseTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PieceReferenceIndex rebuild failed after LoadData; album/loose-track badge counts may be stale");
                StatusMessage += " (badge counts may be stale)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveComposersAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Saving composers...";
            await _canonDataService.SaveComposersAsync(Composers.ToList());
            StatusMessage = $"Saved {Composers.Count} composers.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save composers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SavePiecesAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Saving pieces...";
            await _canonDataService.SavePiecesAsync(Pieces.ToList());
            StatusMessage = $"Saved {Pieces.Count} pieces.";
            try
            {
                var (albums, looseTracks) = await GetContainersForRebuildAsync();
                _refIndex.Rebuild(Pieces, albums, looseTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PieceReferenceIndex rebuild failed after SavePieces; badge counts may be stale");
                StatusMessage += " (badge counts may be stale)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save pieces: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SavePickListsAsync()
    {
        try
        {
            await _canonDataService.SavePickListsAsync(PickLists);
            StatusMessage = "Pick lists saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save pick lists: {ex.Message}";
        }
    }

    // (Pre-fix this section had an unwired parameterless DeleteComposerCommand
    // tied to SelectedComposer; retired in the H36/CanonView slice in favour
    // of the new parameterized DeleteComposerCommand below — which the
    // CanonView toolbar handler actually calls, with the active selection
    // from the tree click handler.)

    [RelayCommand]
    private void ApplyComposerFilter()
    {
        var filter = ComposerFilter.Trim();
        IEnumerable<CanonComposer> filtered = string.IsNullOrEmpty(filter)
            ? Composers
            : Composers.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SortName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filtered = ComposerProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => filtered.Where(c => c.IsProvisional),
            ProvisionalFilter.Accepted    => filtered.Where(c => !c.IsProvisional),
            _                             => filtered,
        };

        FilteredComposers = new ObservableCollection<CanonComposer>(
            filtered.OrderBy(c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
                StringComparer.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void ApplyPiecesFilter()
    {
        var textFilter = PiecesFilter.Trim();

        IEnumerable<CanonPiece> filtered = Pieces;

        if (!string.IsNullOrEmpty(textFilter))
        {
            filtered = filtered.Where(p =>
                (p.Composer ?? "").Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.Title ?? "").Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.Form ?? "").Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Summary.Contains(textFilter, StringComparison.OrdinalIgnoreCase));
        }

        filtered = PieceProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => filtered.Where(p => p.IsProvisional),
            ProvisionalFilter.Accepted    => filtered.Where(p => !p.IsProvisional),
            _                             => filtered,
        };

        FilteredPieces = new ObservableCollection<CanonPiece>(filtered.ToList());
    }

    // ── Approval / rejection — Composers ─────────────────────────────────────

    [RelayCommand]
    private async Task ApproveComposerAsync()
    {
        if (SelectedComposer == null) return;
        SelectedComposer.IsProvisional = false;
        await _canonDataService.SaveComposersAsync(Composers.ToList());
        ApplyComposerFilter();
        StatusMessage = $"Approved {SelectedComposer.Name}.";
    }

    /// <summary>
    /// Runs the FK-aware reject cascade for <paramref name="composer"/>: deletes
    /// every piece they own, strips album track refs to those pieces, scrubs
    /// contributor credits naming them, then deletes the composer row. Mutates
    /// the observable collections to reflect the result and returns the counts.
    /// <para>
    /// Does NOT show any dialog — callers are responsible for confirmation and
    /// for surfacing any thrown exception (which leaves the DB in a possibly
    /// partial state; a refresh resyncs). The reason the cascade lives here
    /// rather than inside a RelayCommand is that the only invocation site is
    /// the context-menu handler in <c>CanonView.xaml.cs</c>, which needs to
    /// show its own confirmation dialog and read back the result.
    /// </para>
    /// </summary>
    public async Task<CanonRejectCascade.RejectResult> RejectComposerWithCascadeAsync(CanonComposer composer)
    {
        var composersList = Composers.ToList();
        var piecesList    = Pieces.ToList();

        var piecesToDelete = piecesList
            .Where(p => string.Equals(p.Composer, composer.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        try
        {
            IsLoading = true;
            StatusMessage = $"Deleting {composer.Name}…";

            // Pass the singletons' in-memory lists through the cascade when
            // they're already loaded — the cascade mutates them in place so
            // the rebuild below can reuse them without a second DB load.
            // When the singletons haven't loaded yet, the in-place overload
            // falls back to its own internal fresh load (legacy behaviour).
            // See Rework H9.
            var (albumsForCascade, looseForCascade) = _albumsVm.HasLoaded && _tracksVm.HasLoaded
                ? (_albumsVm.AllAlbums, (List<AlbumTrack>?)_tracksVm.LooseTracks.ToList())
                : (null, null);
            // The cascade returns the (possibly freshly-loaded) lists so the
            // rebuild below has a coherent post-save state regardless of
            // which path the overload took.
            var result = await CanonRejectCascade
                .RejectComposerAsync(_canonDataService, composersList, piecesList,
                                     albumsForCascade, looseForCascade, composer);

            // Reflect the helper's mutations back into the observable collections.
            Composers.Remove(composer);
            if (ReferenceEquals(SelectedComposer, composer)) SelectedComposer = null;
            foreach (var p in piecesToDelete)
            {
                Pieces.Remove(p);
                if (ReferenceEquals(SelectedPiece, p)) SelectedPiece = null;
            }

            // Container counts are stale; rebuild against the freshly-saved
            // data. The cascade has already mutated the singletons' lists in
            // place (or freshly-loaded copies, when the singletons weren't
            // ready) — no second DB load needed here.
            try
            {
                var (albums, looseTracks) = await GetContainersForRebuildAsync();
                _refIndex.Rebuild(Pieces.ToList(), albums, looseTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PieceReferenceIndex rebuild failed after composer reject cascade; badge counts may be stale");
            }

            ApplyComposerFilter();
            ApplyPiecesFilter();
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Approval / rejection — Pieces ─────────────────────────────────────────
    // (Pre-fix this section had an unwired parameterless ApprovePieceCommand
    // tied to SelectedPiece; retired here in favour of the H36
    // ApproveCanonItemCommand below which takes the right-clicked target as a
    // parameter — matches the actual CanonView context-menu invocation site.)

    /// <summary>
    /// Runs the FK-aware reject cascade for <paramref name="piece"/>: strips
    /// every album track ref pointing at it and deletes the piece row. Mirrors
    /// <see cref="RejectComposerWithCascadeAsync"/> — caller handles confirmation
    /// and error surfacing.
    /// </summary>
    public async Task<CanonRejectCascade.RejectResult> RejectPieceWithCascadeAsync(CanonPiece piece)
    {
        try
        {
            IsLoading = true;
            StatusMessage = $"Deleting {piece.DisplayTitle}…";

            var piecesList = Pieces.ToList();
            // See RejectComposerWithCascadeAsync for the Rework H9 rationale.
            var (albumsForCascade, looseForCascade) = _albumsVm.HasLoaded && _tracksVm.HasLoaded
                ? (_albumsVm.AllAlbums, (List<AlbumTrack>?)_tracksVm.LooseTracks.ToList())
                : (null, null);
            var result = await CanonRejectCascade
                .RejectPieceAsync(_canonDataService, piecesList,
                                  albumsForCascade, looseForCascade, piece);

            Pieces.Remove(piece);
            if (ReferenceEquals(SelectedPiece, piece)) SelectedPiece = null;

            try
            {
                var (albums, looseTracks) = await GetContainersForRebuildAsync();
                _refIndex.Rebuild(Pieces.ToList(), albums, looseTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PieceReferenceIndex rebuild failed after piece reject cascade; badge counts may be stale");
            }

            ApplyPiecesFilter();
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Reject is handled via Reject*WithCascadeAsync above. The cascade itself
    // lives in CDArchive.Core.Services.CanonRejectCascade so it's unit-testable.

    // ── H36: Click-handler retirement RelayCommands ──────────────────────────
    //
    // The CanonView slice of H36 surfaces four new RelayCommands so the
    // toolbar Delete buttons + context-menu Approve/Reject items have testable
    // VM-side homes for their confirmation dialog + data mutation + save +
    // status-message logic. The View's click handlers shrink to thin shims
    // that look up the target object and call `Command.ExecuteAsync(target)`.
    //
    // Each command fires <see cref="DataMutated"/> after mutating the VM's
    // observable collections but BEFORE awaiting the save. CanonView
    // subscribes to that event and runs its tree-rebuild + badge-count
    // refresh synchronously — preserving the pre-fix "mutate → rebuild →
    // suppress → save" order (the rendering-glitch fix in OnContextApprove).

    /// <summary>
    /// Toolbar Delete Composer. Confirms via <see cref="IDialogService"/>,
    /// removes the composer from the observable collection, fires
    /// <see cref="DataMutated"/>, saves, sets status.
    /// </summary>
    [RelayCommand]
    private async Task DeleteComposerAsync(CanonComposer? composer)
    {
        if (composer is null) return;

        var name = composer.Name;
        if (!_dialogs.Confirm(
                $"Delete composer \"{name}\"?\n\nThis cannot be undone.",
                "Delete Composer")) return;

        Composers.Remove(composer);
        if (ReferenceEquals(SelectedComposer, composer)) SelectedComposer = null;
        DataMutated?.Invoke();
        await _canonDataService.SaveComposersAsync(Composers.ToList());
        StatusMessage = $"Deleted {name}.";
    }

    /// <summary>
    /// Toolbar Delete Piece. Confirms via <see cref="IDialogService"/>,
    /// removes the piece from the observable collection, fires
    /// <see cref="DataMutated"/>, persists (composers + pieces in one
    /// transaction via SaveBatch), sets status.
    /// </summary>
    [RelayCommand]
    private async Task DeletePieceAsync(CanonPiece? piece)
    {
        if (piece is null) return;

        var title = piece.DisplayTitle;
        if (!_dialogs.Confirm(
                $"Delete \"{title}\"?\n\nThis cannot be undone.",
                "Delete Piece")) return;

        Pieces.Remove(piece);
        if (ReferenceEquals(SelectedPiece, piece)) SelectedPiece = null;
        DataMutated?.Invoke();
        // SaveBatch (composers + pieces in one transaction) matches the
        // pre-fix SaveAllAsync the View invoked for piece deletes.
        await _canonDataService.SaveBatchAsync(
            Composers.ToList(), Pieces.ToList(), null, null, null);
        StatusMessage = $"Deleted: {title}.";
    }

    /// <summary>
    /// Context-menu Approve. The right-clicked item arrives as
    /// <paramref name="target"/>; we dispatch based on type. Flips
    /// <c>IsProvisional</c> to false, fires <see cref="DataMutated"/>, saves
    /// the corresponding subsystem, sets status.
    /// </summary>
    [RelayCommand]
    private async Task ApproveCanonItemAsync(object? target)
    {
        switch (target)
        {
            case ComposerTreeNode node when node.Composer.IsProvisional:
                node.Composer.IsProvisional = false;
                DataMutated?.Invoke();
                await _canonDataService.SaveComposersAsync(Composers.ToList());
                StatusMessage = $"Approved {node.Composer.Name}.";
                break;
            case CanonPiece piece when piece.IsProvisional:
                piece.IsProvisional = false;
                DataMutated?.Invoke();
                await _canonDataService.SavePiecesAsync(Pieces.ToList());
                StatusMessage = $"Approved {piece.DisplayTitle}.";
                break;
        }
    }

    /// <summary>
    /// Context-menu Reject. Dispatches based on <paramref name="target"/>
    /// type and routes through the existing FK-aware
    /// <see cref="RejectComposerWithCascadeAsync"/> /
    /// <see cref="RejectPieceWithCascadeAsync"/> cascade methods. Confirmation
    /// surfaces via <see cref="IDialogService"/>; on cascade failure the
    /// error is shown via <c>ShowError</c>.
    /// </summary>
    [RelayCommand]
    private async Task RejectCanonItemAsync(object? target)
    {
        switch (target)
        {
            case ComposerTreeNode node:
            {
                var name = node.Composer.Name;
                var ownedPiecesCount = Pieces
                    .Count(p => string.Equals(p.Composer, name, StringComparison.OrdinalIgnoreCase));
                var pieceTail = ownedPiecesCount switch
                {
                    0 => "",
                    1 => " and 1 piece",
                    _ => $" and {ownedPiecesCount} pieces",
                };
                if (!_dialogs.Confirm(
                        $"Delete provisional composer '{name}'{pieceTail}?",
                        "Confirm Rejection")) return;

                try
                {
                    var result = await RejectComposerWithCascadeAsync(node.Composer);
                    DataMutated?.Invoke();
                    var headline = result.PiecesDeleted > 0
                        ? $"Rejected and deleted {name} and {result.PiecesDeleted} piece(s)"
                        : $"Rejected and deleted {name}";
                    var extras = new List<string>();
                    if (result.RefsStripped    > 0) extras.Add($"{result.RefsStripped} album track ref(s)");
                    if (result.CreditsStripped > 0) extras.Add($"{result.CreditsStripped} contributor credit(s)");
                    StatusMessage = extras.Count > 0
                        ? $"{headline} (also removed {string.Join(", ", extras)})."
                        : $"{headline}.";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Could not delete {name}: {ex.Message}";
                    _dialogs.ShowError(StatusMessage, "Reject Composer");
                }
                break;
            }
            case CanonPiece piece:
            {
                var title = piece.DisplayTitle;
                if (!_dialogs.Confirm(
                        $"Delete provisional piece '{title}'?",
                        "Confirm Rejection")) return;

                try
                {
                    var result = await RejectPieceWithCascadeAsync(piece);
                    DataMutated?.Invoke();
                    StatusMessage = result.RefsStripped > 0
                        ? $"Rejected and deleted {title} (also removed {result.RefsStripped} album track ref(s))."
                        : $"Rejected and deleted {title}.";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Could not delete {title}: {ex.Message}";
                    _dialogs.ShowError(StatusMessage, "Reject Piece");
                }
                break;
            }
        }
    }
}
