using System.Collections.ObjectModel;
using System.Globalization;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class ArchiveBrowserViewModel : ObservableObject
{
    private readonly IArchiveScannerService _scannerService;

    [ObservableProperty]
    private ObservableCollection<AlbumInfo> _albums = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "";

    public ArchiveBrowserViewModel(IArchiveScannerService scannerService)
    {
        _scannerService = scannerService;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning archive...";

            var results = await _scannerService.ScanArchiveAsync();

            var comparer = CultureInfo.InvariantCulture.CompareInfo;
            Albums = new ObservableCollection<AlbumInfo>(results.OrderBy(a => a.Name, Comparer<string>.Create((x, y) =>
                comparer.Compare(x, y, CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase))));
            StatusMessage = $"Found {Albums.Count} album(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }
}
