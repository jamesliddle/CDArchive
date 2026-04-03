using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NewAlbumViewModel _newAlbumViewModel;
    private readonly ArchiveBrowserViewModel _archiveBrowserViewModel;
    private readonly ValidationViewModel _validationViewModel;
    private readonly ConversionViewModel _conversionViewModel;
    private readonly ConversionStatusViewModel _conversionStatusViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly CatalogueViewModel _catalogueViewModel;
    private readonly CanonViewModel _canonViewModel;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentViewTitle = "New Album";

    public MainViewModel(
        NewAlbumViewModel newAlbumViewModel,
        ArchiveBrowserViewModel archiveBrowserViewModel,
        ValidationViewModel validationViewModel,
        ConversionViewModel conversionViewModel,
        ConversionStatusViewModel conversionStatusViewModel,
        SettingsViewModel settingsViewModel,
        CatalogueViewModel catalogueViewModel,
        CanonViewModel canonViewModel)
    {
        _newAlbumViewModel = newAlbumViewModel;
        _archiveBrowserViewModel = archiveBrowserViewModel;
        _validationViewModel = validationViewModel;
        _conversionViewModel = conversionViewModel;
        _conversionStatusViewModel = conversionStatusViewModel;
        _settingsViewModel = settingsViewModel;
        _catalogueViewModel = catalogueViewModel;
        _canonViewModel = canonViewModel;

        CurrentView = _newAlbumViewModel;
    }

    [RelayCommand]
    private void NavigateToNewAlbum()
    {
        CurrentView = _newAlbumViewModel;
        CurrentViewTitle = "New Album";
    }

    [RelayCommand]
    private void NavigateToArchiveBrowser()
    {
        CurrentView = _archiveBrowserViewModel;
        CurrentViewTitle = "Archive Browser";
    }

    [RelayCommand]
    private void NavigateToValidation()
    {
        CurrentView = _validationViewModel;
        CurrentViewTitle = "Validation";
    }

    [RelayCommand]
    private void NavigateToConversion()
    {
        CurrentView = _conversionViewModel;
        CurrentViewTitle = "Conversion";
    }

    [RelayCommand]
    private void NavigateToConversionStatus()
    {
        CurrentView = _conversionStatusViewModel;
        CurrentViewTitle = "Conversion Status";
    }

    [RelayCommand]
    private void NavigateToCatalogue()
    {
        CurrentView = _catalogueViewModel;
        CurrentViewTitle = "Catalogue";
    }

    [RelayCommand]
    private void NavigateToCanon()
    {
        CurrentView = _canonViewModel;
        CurrentViewTitle = "Composers";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = _settingsViewModel;
        CurrentViewTitle = "Settings";
    }
}
