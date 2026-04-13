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
    private readonly ImportExportViewModel _importExportViewModel;
    private readonly PickListsViewModel _pickListsViewModel;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentViewTitle = "Composers and Authors";

    [ObservableProperty]
    private bool _isCanonViewActive = true;

    public CanonViewModel CanonViewModel => _canonViewModel;

    public MainViewModel(
        NewAlbumViewModel newAlbumViewModel,
        ArchiveBrowserViewModel archiveBrowserViewModel,
        ValidationViewModel validationViewModel,
        ConversionViewModel conversionViewModel,
        ConversionStatusViewModel conversionStatusViewModel,
        SettingsViewModel settingsViewModel,
        CatalogueViewModel catalogueViewModel,
        CanonViewModel canonViewModel,
        ImportExportViewModel importExportViewModel,
        PickListsViewModel pickListsViewModel)
    {
        _newAlbumViewModel = newAlbumViewModel;
        _archiveBrowserViewModel = archiveBrowserViewModel;
        _validationViewModel = validationViewModel;
        _conversionViewModel = conversionViewModel;
        _conversionStatusViewModel = conversionStatusViewModel;
        _settingsViewModel = settingsViewModel;
        _catalogueViewModel = catalogueViewModel;
        _canonViewModel = canonViewModel;
        _importExportViewModel = importExportViewModel;
        _pickListsViewModel = pickListsViewModel;

        // CanonView is always-alive in MainWindow; IsCanonViewActive=true (default) shows it on startup.
    }

    [RelayCommand]
    private void NavigateToNewAlbum()
    {
        IsCanonViewActive = false;
        CurrentView = _newAlbumViewModel;
        CurrentViewTitle = "New Album";
    }

    [RelayCommand]
    private void NavigateToArchiveBrowser()
    {
        IsCanonViewActive = false;
        CurrentView = _archiveBrowserViewModel;
        CurrentViewTitle = "Archive Browser";
    }

    [RelayCommand]
    private void NavigateToValidation()
    {
        IsCanonViewActive = false;
        CurrentView = _validationViewModel;
        CurrentViewTitle = "Validation";
    }

    [RelayCommand]
    private void NavigateToConversion()
    {
        IsCanonViewActive = false;
        CurrentView = _conversionViewModel;
        CurrentViewTitle = "Conversion";
    }

    [RelayCommand]
    private void NavigateToConversionStatus()
    {
        IsCanonViewActive = false;
        CurrentView = _conversionStatusViewModel;
        CurrentViewTitle = "Conversion Status";
    }

    [RelayCommand]
    private void NavigateToCatalogue()
    {
        IsCanonViewActive = false;
        CurrentView = _catalogueViewModel;
        CurrentViewTitle = "Catalogue";
    }

    [RelayCommand]
    private void NavigateToCanon()
    {
        IsCanonViewActive = true;
        CurrentView = null;
        CurrentViewTitle = "Composers and Authors";
        _ = _canonViewModel.LoadDataCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        IsCanonViewActive = false;
        CurrentView = _settingsViewModel;
        CurrentViewTitle = "Settings";
    }

    [RelayCommand]
    private void NavigateToImportExport()
    {
        IsCanonViewActive = false;
        CurrentView = _importExportViewModel;
        CurrentViewTitle = "Import / Export";
    }

    [RelayCommand]
    private void NavigateToPickLists()
    {
        IsCanonViewActive = false;
        CurrentView = _pickListsViewModel;
        CurrentViewTitle = "Pick Lists";
        _ = _pickListsViewModel.LoadAsync();
    }
}
