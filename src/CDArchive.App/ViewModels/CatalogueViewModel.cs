using System.Collections.ObjectModel;
using System.IO;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class CatalogueViewModel : ObservableObject
{
    private readonly ICataloguingService _cataloguingService;
    private readonly IArchiveSettings _settings;
    private readonly CompositeCatalogueReference _reference;

    [ObservableProperty]
    private ObservableCollection<CatalogueEntry> _entries = new();

    [ObservableProperty]
    private CatalogueEntry? _selectedEntry;

    [ObservableProperty]
    private ObservableCollection<AlbumInfo> _albums = new();

    [ObservableProperty]
    private AlbumInfo? _selectedAlbum;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasEntries;

    // Bulk-edit fields applied to all tracks
    [ObservableProperty]
    private string _bulkArtist = "";

    [ObservableProperty]
    private string _bulkAlbum = "";

    [ObservableProperty]
    private string _bulkGenre = "";

    [ObservableProperty]
    private string _bulkYear = "";

    private readonly IArchiveScannerService _scannerService;

    [ObservableProperty]
    private string _referenceSource = "";

    public CatalogueViewModel(ICataloguingService cataloguingService, IArchiveSettings settings,
        IArchiveScannerService scannerService, CompositeCatalogueReference reference)
    {
        _cataloguingService = cataloguingService;
        _settings = settings;
        _scannerService = scannerService;
        _reference = reference;
    }

    [RelayCommand]
    private async Task LoadAlbumsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Scanning archive...";
            var scanned = await _scannerService.ScanArchiveAsync();
            Albums = new ObservableCollection<AlbumInfo>(scanned.OrderBy(a => a.Name));
            StatusMessage = $"Found {Albums.Count} album(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadTagsAsync()
    {
        if (SelectedAlbum == null)
            return;

        try
        {
            IsLoading = true;
            StatusMessage = "Reading tags...";

            var albumPath = SelectedAlbum.FullPath;
            var loaded = await _cataloguingService.ReadAlbumTagsAsync(albumPath);

            Entries = new ObservableCollection<CatalogueEntry>(loaded);
            HasEntries = Entries.Count > 0;

            if (HasEntries)
            {
                // Pre-fill only Album from the folder name.
                // Artist, Genre, and Year are left empty for the user to set,
                // since raw tag values for these fields are almost always wrong
                // in classical CD rips.
                BulkAlbum = Entries[0].Album;
                BulkArtist = "";
                BulkGenre = "";
                BulkYear = "";
            }

            ReferenceSource = _reference.LastSourceUsed ?? "none";
            StatusMessage = $"Loaded {Entries.Count} track(s). Reference: {ReferenceSource}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to read tags: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ApplyBulkFields()
    {
        int? year = int.TryParse(BulkYear, out var y) ? y : null;

        foreach (var entry in Entries)
        {
            if (!string.IsNullOrWhiteSpace(BulkArtist))
                entry.Artist = BulkArtist;
            if (!string.IsNullOrWhiteSpace(BulkAlbum))
                entry.Album = BulkAlbum;
            if (!string.IsNullOrWhiteSpace(BulkGenre))
                entry.Genre = BulkGenre;
            entry.Year = year;

            entry.TrackCount = Entries.Count;
        }

        // Refresh the grid
        var snapshot = Entries.ToList();
        Entries = new ObservableCollection<CatalogueEntry>(snapshot);
        HasEntries = Entries.Count > 0;
        StatusMessage = "Bulk fields applied.";
    }

    [RelayCommand]
    private void HyphenateKeys()
    {
        foreach (var entry in Entries)
        {
            entry.Name = CataloguingRules.HyphenateKeys(entry.Name);
        }

        var snapshot = Entries.ToList();
        Entries = new ObservableCollection<CatalogueEntry>(snapshot);
        StatusMessage = "Key names hyphenated.";
    }

    [RelayCommand]
    private void PadMovementNumbers()
    {
        // Group entries by work (entries sharing the same work prefix before " - ")
        var groups = Entries
            .Select(e =>
            {
                var dashIdx = e.Name.IndexOf(" - ", StringComparison.Ordinal);
                var workTitle = dashIdx >= 0 ? e.Name[..dashIdx] : "";
                var movement = dashIdx >= 0 ? e.Name[(dashIdx + 3)..] : "";
                return (Entry: e, WorkTitle: workTitle, Movement: movement);
            })
            .GroupBy(x => x.WorkTitle)
            .ToList();

        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.Key))
                continue;

            int totalMovements = group.Count();
            foreach (var item in group)
            {
                if (!string.IsNullOrEmpty(item.Movement))
                {
                    var padded = CataloguingRules.PadMovementNumber(item.Movement, totalMovements);
                    item.Entry.Name = $"{item.WorkTitle} - {padded}";
                }
            }
        }

        var snapshot = Entries.ToList();
        Entries = new ObservableCollection<CatalogueEntry>(snapshot);
        StatusMessage = "Movement numbers padded where needed.";
    }

    [RelayCommand]
    private void PadWorkNumbers(string? maxCountText)
    {
        if (!int.TryParse(maxCountText, out var maxCount) || maxCount < 1)
        {
            StatusMessage = "Enter the max count for the composer's catalogue (e.g., 104 for Haydn symphonies).";
            return;
        }

        foreach (var entry in Entries)
        {
            entry.Name = CataloguingRules.PadWorkNumber(entry.Name, maxCount);
        }

        var snapshot = Entries.ToList();
        Entries = new ObservableCollection<CatalogueEntry>(snapshot);
        StatusMessage = $"Work numbers padded for catalogue size {maxCount}.";
    }

    [RelayCommand]
    private async Task WriteTagsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Writing tags...";

            var count = await _cataloguingService.WriteTagsAsync(Entries);

            StatusMessage = $"Tags written to {count} file(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to write tags: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
