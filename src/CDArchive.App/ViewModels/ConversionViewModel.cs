using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class ConversionViewModel : ObservableObject
{
    private readonly IArchiveScannerService _scannerService;
    private readonly IConversionService _conversionService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private ObservableCollection<AlbumInfo> _albums = new();

    [ObservableProperty]
    private AlbumInfo? _selectedAlbum;

    [ObservableProperty]
    private ObservableCollection<ConversionJob> _jobs = new();

    [ObservableProperty]
    private bool _isConverting;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _statusMessage = "";

    public ConversionViewModel(
        IArchiveScannerService scannerService,
        IConversionService conversionService)
    {
        _scannerService = scannerService;
        _conversionService = conversionService;
    }

    partial void OnSelectedAlbumChanged(AlbumInfo? value)
    {
        ConvertCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConvertingChanged(bool value)
    {
        ConvertCommand.NotifyCanExecuteChanged();
    }

    private bool CanConvert => SelectedAlbum != null && !IsConverting;

    [RelayCommand]
    private async Task LoadAlbumsAsync()
    {
        try
        {
            StatusMessage = "Loading albums...";
            var results = await _scannerService.ScanArchiveAsync();
            Albums = new ObservableCollection<AlbumInfo>(results);
            StatusMessage = $"Loaded {Albums.Count} album(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load albums: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        if (SelectedAlbum == null)
            return;

        try
        {
            IsConverting = true;
            Jobs.Clear();
            OverallProgress = 0;
            _cts = new CancellationTokenSource();

            StatusMessage = $"Converting '{SelectedAlbum.Name}'...";

            var progressReporter = new Progress<ConversionJob>(job =>
            {
                var existing = Jobs.FirstOrDefault(j => j.SourceFlacPath == job.SourceFlacPath);
                if (existing != null)
                {
                    var index = Jobs.IndexOf(existing);
                    Jobs[index] = job;
                }
                else
                {
                    Jobs.Add(job);
                }

                if (Jobs.Count > 0)
                {
                    OverallProgress = Jobs.Sum(j => j.ProgressPercent) / Jobs.Count;
                }
            });

            var batch = await _conversionService.ConvertAlbumAsync(SelectedAlbum, progressReporter, _cts.Token);
            OverallProgress = batch.OverallProgress;
            StatusMessage = $"Conversion complete. {batch.CompletedCount}/{batch.TotalCount} succeeded, {batch.FailedCount} failed.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Conversion cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            IsConverting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }
}
