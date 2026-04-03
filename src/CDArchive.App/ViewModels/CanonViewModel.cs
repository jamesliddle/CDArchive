using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class CanonViewModel : ObservableObject
{
    private readonly ICanonDataService _canonDataService;

    // --- Composers ---

    [ObservableProperty]
    private ObservableCollection<CanonComposer> _composers = [];

    [ObservableProperty]
    private CanonComposer? _selectedComposer;

    [ObservableProperty]
    private string _composerFilter = "";

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

    public CanonViewModel(ICanonDataService canonDataService)
    {
        _canonDataService = canonDataService;
    }

    partial void OnComposerFilterChanged(string value) => ApplyComposerFilter();
    partial void OnPiecesFilterChanged(string value) => ApplyPiecesFilter();

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
            Pieces = new ObservableCollection<CanonPiece>(pieces);
            ApplyPiecesFilter();

            PickLists = await _canonDataService.LoadPickListsAsync();

            StatusMessage = $"Loaded {Composers.Count} composers and {Pieces.Count} pieces.";
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
        var filtered = string.IsNullOrEmpty(filter)
            ? Composers.ToList()
            : Composers.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SortName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

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

        FilteredPieces = new ObservableCollection<CanonPiece>(filtered.ToList());
    }

}
