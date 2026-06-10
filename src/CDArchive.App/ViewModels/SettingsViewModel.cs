using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IArchiveSettings _settings;
    // Optional — when null (e.g. App.Tests light DI graph) the connectivity
    // diagnostic just reports "not wired" rather than throwing.
    private readonly IMusicBrainzImportEnricher? _mbEnricher;

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

    /// <summary>
    /// When true, the player stops at the end of the current track instead of
    /// auto-advancing to the next track on the album. Defaults false.
    /// </summary>
    [ObservableProperty]
    private bool _stopAfterCurrentTrack;

    /// <summary>
    /// When true, the player caption shows a third line with the full path of
    /// the file currently playing. Defaults false.
    /// </summary>
    [ObservableProperty]
    private bool _showPlayingFilePath;

    /// <summary>Seconds the player skips forward per seek-forward press (1–60).</summary>
    [ObservableProperty]
    private int _seekForwardSeconds = 10;

    /// <summary>Seconds the player skips back per seek-back press (1–60).</summary>
    [ObservableProperty]
    private int _seekBackwardSeconds = 10;

    /// <summary>Seconds into the track before Previous restarts instead of going
    /// to the previous track (0–60).</summary>
    [ObservableProperty]
    private int _previousRestartThresholdSeconds = 2;

    /// <summary>1–60, for the seek-duration dropdowns.</summary>
    public static IReadOnlyList<int> SeekRange { get; } = Enumerable.Range(1, 60).ToList();

    /// <summary>0–60, for the restart-threshold dropdown.</summary>
    public static IReadOnlyList<int> RestartThresholdRange { get; } = Enumerable.Range(0, 61).ToList();

    // ── MusicBrainz import enrichment (slice 6) ──────────────────────────────

    [ObservableProperty] private bool _enableMusicBrainzImportEnrichment;
    [ObservableProperty] private int  _musicBrainzCandidatesPerProposal = 3;
    [ObservableProperty] private bool _applyMbAlbumMetadata          = true;
    [ObservableProperty] private bool _applyMbPerformerCredits       = true;
    [ObservableProperty] private bool _applyMbRecordingSessions      = true;
    [ObservableProperty] private bool _applyMbCanonicalWorkStructure = true;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Result of the most-recent connectivity test. Drives a
    /// Visibility-bound message panel under the test button.</summary>
    [ObservableProperty]
    private string _mbConnectivityStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestMbConnectivityCommand))]
    private bool _isTestingMbConnectivity;

    /// <summary>CanExecute for <see cref="TestMbConnectivityCommand"/> —
    /// disables the button while a test is in flight to prevent piling
    /// up concurrent MB requests.</summary>
    private bool CanTestMbConnectivity() => !IsTestingMbConnectivity;

    public event Action? BrowseArchivePathRequested;
    public event Action? BrowseFfmpegPathRequested;

    // Optional — when present, a settings save pings the player so the
    // "show playing file path" toggle takes effect on the current track
    // immediately rather than only on the next one.
    private readonly PlayerViewModel? _player;

    public SettingsViewModel(
        IArchiveSettings settings,
        IMusicBrainzImportEnricher? mbEnricher = null,
        PlayerViewModel? player = null)
    {
        _settings = settings;
        _mbEnricher = mbEnricher;
        _player = player;

        ArchiveRootPath = _settings.ArchiveRootPath;
        FfmpegPath = _settings.FfmpegPath;
        Mp3Bitrate = _settings.Mp3Bitrate;
        PreferredAudioFormat = _settings.PreferredAudioFormat;
        StopAfterCurrentTrack = _settings.StopAfterCurrentTrack;
        ShowPlayingFilePath = _settings.ShowPlayingFilePath;
        SeekForwardSeconds = _settings.SeekForwardSeconds;
        SeekBackwardSeconds = _settings.SeekBackwardSeconds;
        PreviousRestartThresholdSeconds = _settings.PreviousRestartThresholdSeconds;

        EnableMusicBrainzImportEnrichment = _settings.EnableMusicBrainzImportEnrichment;
        MusicBrainzCandidatesPerProposal  = _settings.MusicBrainzCandidatesPerProposal;
        ApplyMbAlbumMetadata          = _settings.ApplyMbAlbumMetadata;
        ApplyMbPerformerCredits       = _settings.ApplyMbPerformerCredits;
        ApplyMbRecordingSessions      = _settings.ApplyMbRecordingSessions;
        ApplyMbCanonicalWorkStructure = _settings.ApplyMbCanonicalWorkStructure;
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
            _settings.StopAfterCurrentTrack = StopAfterCurrentTrack;
            _settings.ShowPlayingFilePath = ShowPlayingFilePath;
            _settings.SeekForwardSeconds = SeekForwardSeconds;
            _settings.SeekBackwardSeconds = SeekBackwardSeconds;
            _settings.PreviousRestartThresholdSeconds = PreviousRestartThresholdSeconds;

            _settings.EnableMusicBrainzImportEnrichment = EnableMusicBrainzImportEnrichment;
            _settings.MusicBrainzCandidatesPerProposal  = MusicBrainzCandidatesPerProposal;
            _settings.ApplyMbAlbumMetadata          = ApplyMbAlbumMetadata;
            _settings.ApplyMbPerformerCredits       = ApplyMbPerformerCredits;
            _settings.ApplyMbRecordingSessions      = ApplyMbRecordingSessions;
            _settings.ApplyMbCanonicalWorkStructure = ApplyMbCanonicalWorkStructure;

            _settings.Save();

            // Apply player settings (caption line, seek-step glyphs, restart
            // threshold) to the currently-playing track now, not just the next.
            _player?.OnSettingsChanged();

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

    /// <summary>
    /// Diagnostic: fire a real MB release-search ("Beethoven Symphony 9")
    /// and report the outcome. Helps rule out connectivity / User-Agent /
    /// proxy issues when the user reports "no matches" in the import flow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestMbConnectivity))]
    private async Task TestMbConnectivityAsync()
    {
        if (_mbEnricher is null)
        {
            MbConnectivityStatus = "MusicBrainz enricher not registered (DI not wired in this build).";
            return;
        }

        IsTestingMbConnectivity = true;
        MbConnectivityStatus = "Querying MusicBrainz…";

        try
        {
            // Canonical safe query — well-known classical release that
            // MB definitely has many candidates for.
            var hits = await _mbEnricher.SearchReleasesAsync(
                "Beethoven Symphony 9",
                "Karajan",
                Array.Empty<TimeSpan>(),
                limit: 3,
                CancellationToken.None);

            if (hits.Count == 0)
            {
                MbConnectivityStatus =
                    "Reached MusicBrainz but got 0 hits for the test query. "
                    + "The request itself succeeded (no connectivity / User-Agent "
                    + "problem). See the rolling log for the exact query: "
                    + "%LocalAppData%\\CDArchive\\logs\\cdarchive-YYYYMMDD.log";
                return;
            }

            var top = hits[0];
            MbConnectivityStatus =
                $"OK — got {hits.Count} hit(s). Top: \"{top.Title}\""
                + (top.ArtistCredit is { Length: > 0 } ? $" — {top.ArtistCredit}" : "")
                + $" (MBID {top.MbReleaseId[..8]}…, confidence {top.Confidence:P0}).";
        }
        catch (Exception ex)
        {
            // Catches HttpRequestException, TaskCanceledException, JsonException.
            // The user sees a clear failure message + can check the log for the
            // full stack trace via the MusicBrainzReference logger.
            MbConnectivityStatus =
                $"FAILED — {ex.GetType().Name}: {ex.Message}. "
                + "See %LocalAppData%\\CDArchive\\logs for details.";
        }
        finally
        {
            IsTestingMbConnectivity = false;
        }
    }
}
