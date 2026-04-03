using System.Collections.ObjectModel;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public record MissingConversionItem(string AlbumName, string DiscFolder, List<string> MissingFiles);

public partial class ConversionStatusViewModel : ObservableObject
{
    private readonly IConversionStatusService _conversionStatusService;

    [ObservableProperty]
    private ObservableCollection<MissingConversionItem> _missingItems = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "";

    public ConversionStatusViewModel(IConversionStatusService conversionStatusService)
    {
        _conversionStatusService = conversionStatusService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Scanning for missing conversions...";

            var results = await _conversionStatusService.GetMissingConversionsAsync();

            var items = results.Select(r => new MissingConversionItem(
                r.Album.Name,
                r.Disc.FolderName,
                r.MissingMp3s
            ));

            MissingItems = new ObservableCollection<MissingConversionItem>(items);
            StatusMessage = $"Found {MissingItems.Count} disc(s) with missing conversions.";
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
