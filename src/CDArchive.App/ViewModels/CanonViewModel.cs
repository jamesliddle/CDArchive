using System.Collections.ObjectModel;
using System.Windows;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>Three-state filter controlling which items are shown by provisional status.</summary>
public enum ProvisionalFilter { All, Provisional, Accepted }

public partial class CanonViewModel : ObservableObject
{
    private readonly ICanonDataService _canonDataService;
    private readonly PieceReferenceIndex _refIndex;

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

    public CanonViewModel(ICanonDataService canonDataService, PieceReferenceIndex refIndex)
    {
        _canonDataService = canonDataService;
        _refIndex = refIndex;
    }

    partial void OnComposerFilterChanged(string value) => ApplyComposerFilter();
    partial void OnPiecesFilterChanged(string value) => ApplyPiecesFilter();
    partial void OnComposerProvisionalFilterChanged(ProvisionalFilter value) => ApplyComposerFilter();
    partial void OnPieceProvisionalFilterChanged(ProvisionalFilter value) => ApplyPiecesFilter();

    partial void OnSelectedComposerChanged(CanonComposer? value)
    {
        // No longer need to filter pieces by composer (Composers tab no longer shows pieces)
    }

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
            // badge counts include both kinds of container.
            try
            {
                var albums      = await _canonDataService.LoadAlbumsAsync();
                var looseTracks = await _canonDataService.LoadLooseTracksAsync();
                _refIndex.Rebuild(Pieces, albums, looseTracks);
            }
            catch { /* non-fatal */ }
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
                var albums      = await _canonDataService.LoadAlbumsAsync();
                var looseTracks = await _canonDataService.LoadLooseTracksAsync();
                _refIndex.Rebuild(Pieces, albums, looseTracks);
            }
            catch { /* non-fatal */ }
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

    [RelayCommand]
    private void DeleteComposer()
    {
        if (SelectedComposer == null) return;
        var name = SelectedComposer.Name;
        Composers.Remove(SelectedComposer);
        ApplyComposerFilter();
        SelectedComposer = null;
        StatusMessage = $"Deleted {name}. Save to persist.";
    }

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

            var result = await CanonRejectCascade
                .RejectComposerAsync(_canonDataService, composersList, piecesList, composer);

            // Reflect the helper's mutations back into the observable collections.
            Composers.Remove(composer);
            if (ReferenceEquals(SelectedComposer, composer)) SelectedComposer = null;
            foreach (var p in piecesToDelete)
            {
                Pieces.Remove(p);
                if (ReferenceEquals(SelectedPiece, p)) SelectedPiece = null;
            }

            // Container counts are stale; rebuild against the freshly-saved data.
            try
            {
                var freshAlbums      = await _canonDataService.LoadAlbumsAsync();
                var freshLooseTracks = await _canonDataService.LoadLooseTracksAsync();
                _refIndex.Rebuild(Pieces.ToList(), freshAlbums, freshLooseTracks);
            }
            catch { /* non-fatal */ }

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

    [RelayCommand]
    private async Task ApprovePieceAsync()
    {
        if (SelectedPiece == null) return;
        SelectedPiece.IsProvisional = false;
        await _canonDataService.SavePiecesAsync(Pieces.ToList());
        ApplyPiecesFilter();
        StatusMessage = $"Approved {SelectedPiece.DisplayTitle}.";
    }

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
            var result = await CanonRejectCascade
                .RejectPieceAsync(_canonDataService, piecesList, piece);

            Pieces.Remove(piece);
            if (ReferenceEquals(SelectedPiece, piece)) SelectedPiece = null;

            try
            {
                var freshAlbums      = await _canonDataService.LoadAlbumsAsync();
                var freshLooseTracks = await _canonDataService.LoadLooseTracksAsync();
                _refIndex.Rebuild(Pieces.ToList(), freshAlbums, freshLooseTracks);
            }
            catch { /* non-fatal */ }

            ApplyPiecesFilter();
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Reject is handled via Reject*WithCascadeAsync above, invoked from the
    // CanonView context menu. The cascade itself lives in
    // CDArchive.Core.Services.CanonRejectCascade so it's unit-testable.
}
