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

    /// <summary>
    /// Format the music player prefers when both FLAC and MP3 exist for a
    /// track. Falls back to the other format if the preferred one is missing.
    /// </summary>
    [ObservableProperty]
    private PreferredAudioFormat _preferredAudioFormat = PreferredAudioFormat.Flac;

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
        PreferredAudioFormat = _settings.PreferredAudioFormat;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _settings.ArchiveRootPath = ArchiveRootPath;
            _settings.FfmpegPath = FfmpegPath;
            _settings.Mp3Bitrate = Mp3Bitrate;
            _settings.PreferredAudioFormat = PreferredAudioFormat;
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
