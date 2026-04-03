using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IArchiveSettings _settings;

    [ObservableProperty]
    private string _archiveRootPath = "";

    [ObservableProperty]
    private string _ffmpegPath = "";

    [ObservableProperty]
    private int _mp3Bitrate = 320;

    [ObservableProperty]
    private string _statusMessage = "";

    public event Action? BrowseArchivePathRequested;
    public event Action? BrowseFfmpegPathRequested;

    public SettingsViewModel(IArchiveSettings settings)
    {
        _settings = settings;

        ArchiveRootPath = _settings.ArchiveRootPath;
        FfmpegPath = _settings.FfmpegPath;
        Mp3Bitrate = _settings.Mp3Bitrate;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _settings.ArchiveRootPath = ArchiveRootPath;
            _settings.FfmpegPath = FfmpegPath;
            _settings.Mp3Bitrate = Mp3Bitrate;
            _settings.Save();
            StatusMessage = "Settings saved successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowseArchivePath()
    {
        BrowseArchivePathRequested?.Invoke();
    }

    [RelayCommand]
    private void BrowseFfmpegPath()
    {
        BrowseFfmpegPathRequested?.Invoke();
    }
}
