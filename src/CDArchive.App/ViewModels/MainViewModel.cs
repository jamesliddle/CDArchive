using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly CanonViewModel _canonViewModel;
    private readonly AlbumsViewModel _albumsViewModel;
    private readonly TracksViewModel _tracksViewModel;
    private readonly ItunesImportViewModel _itunesImportViewModel;
    private readonly ImportExportViewModel _importExportViewModel;
    private readonly PickListsViewModel _pickListsViewModel;
    private readonly PlayerViewModel _playerViewModel;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentViewTitle = "Composers and Authors";

    [ObservableProperty]
    private bool _isCanonViewActive = true;

    public CanonViewModel CanonViewModel => _canonViewModel;
    public PlayerViewModel PlayerViewModel => _playerViewModel;

    public MainViewModel(
        SettingsViewModel settingsViewModel,
        CanonViewModel canonViewModel,
        AlbumsViewModel albumsViewModel,
        TracksViewModel tracksViewModel,
        ItunesImportViewModel itunesImportViewModel,
        ImportExportViewModel importExportViewModel,
        PickListsViewModel pickListsViewModel,
        PlayerViewModel playerViewModel)
    {
        _settingsViewModel = settingsViewModel;
        _canonViewModel = canonViewModel;
        _albumsViewModel = albumsViewModel;
        _tracksViewModel = tracksViewModel;
        _itunesImportViewModel = itunesImportViewModel;
        _importExportViewModel = importExportViewModel;
        _pickListsViewModel = pickListsViewModel;
        _playerViewModel = playerViewModel;

        // CanonView is always-alive in MainWindow; IsCanonViewActive=true (default) shows it on startup.
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
    private void NavigateToAlbums()
    {
        IsCanonViewActive = false;
        CurrentView = _albumsViewModel;
        CurrentViewTitle = "Albums";
        _ = _albumsViewModel.LoadDataCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToTracks()
    {
        IsCanonViewActive = false;
        CurrentView = _tracksViewModel;
        CurrentViewTitle = "Tracks";
        _ = _tracksViewModel.LoadDataCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToItunesImport()
    {
        IsCanonViewActive = false;
        CurrentView = _itunesImportViewModel;
        CurrentViewTitle = "iTunes Library";
        _ = _itunesImportViewModel.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToImportExport()
    {
        IsCanonViewActive = false;
        CurrentView = _importExportViewModel;
        CurrentViewTitle = "JSON Import / Export";
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
