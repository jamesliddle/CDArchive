using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class ValidationViewModel : ObservableObject
{
    private readonly IArchiveScannerService _scannerService;

    [ObservableProperty]
    private ObservableCollection<ValidationResult> _results = new();

    [ObservableProperty]
    private bool _isValidating;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _statusMessage = "";

    public ValidationViewModel(IArchiveScannerService scannerService)
    {
        _scannerService = scannerService;
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        try
        {
            IsValidating = true;
            Progress = 0;
            StatusMessage = "Validating archive...";

            var progressReporter = new Progress<int>(value => Progress = value);

            var validationResults = await _scannerService.ValidateArchiveAsync(progressReporter);

            Results = new ObservableCollection<ValidationResult>(validationResults);
            StatusMessage = $"Validation complete. {Results.Count} album(s) checked.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Validation failed: {ex.Message}";
        }
        finally
        {
            IsValidating = false;
        }
    }
}
