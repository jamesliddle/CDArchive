using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for the persistent player bar. Singleton so playback state
/// survives navigation between views (matches the iTunes "transport bar"
/// metaphor — the bar is always visible, always reflects the current track).
///
/// Threading: <see cref="IAudioPlayerService"/> already marshals its events
/// back to the captured sync context (the WPF UI thread under normal DI), so
/// this VM can just react to events synchronously.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private const int SeekIncrementSeconds = 10;

    private readonly IAudioPlayerService _player;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackInfoLine))]
    private string? _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackInfoLine))]
    private string? _composer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackInfoLine))]
    private string? _album;

    /// <summary>
    /// Single-line track info: "Title — Composer — Album", omitting empty parts.
    /// Falls back to "No track loaded" when nothing has been loaded yet.
    /// </summary>
    public string TrackInfoLine
    {
        get
        {
            var parts = new[] { Title, Composer, Album }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var joined = string.Join(" — ", parts);
            return string.IsNullOrEmpty(joined) ? "No track loaded" : joined;
        }
    }

    /// <summary>true when the player has a track loaded; drives bar enabled/greyed state.</summary>
    [ObservableProperty]
    private bool _isTrackLoaded;

    /// <summary>true when the player is actively playing; drives the play/pause icon.</summary>
    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Total duration of the loaded track, in seconds — drives Slider.Maximum.</summary>
    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private string _elapsedDisplay = "0:00";

    [ObservableProperty]
    private string _remainingDisplay = "0:00";

    private double _sliderValue;
    /// <summary>
    /// Two-way bound to the progress Slider's Value. Updated by playback (via
    /// <see cref="OnPositionChanged"/>) and by user input (via the slider's
    /// binding). The setter keeps the elapsed/remaining displays in sync so
    /// dragging the thumb gives live feedback before the seek lands.
    /// </summary>
    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            if (SetProperty(ref _sliderValue, value))
                UpdateTimeDisplays(value);
        }
    }

    /// <summary>True between <see cref="BeginScrub"/> and <see cref="EndScrub"/>; suppresses playback-driven slider updates while the user is dragging.</summary>
    public bool IsScrubbing { get; private set; }

    public PlayerViewModel(IAudioPlayerService player)
    {
        _player = player;
        _player.StateChanged   += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationKnown  += OnDurationKnown;
        _player.PlaybackEnded  += OnPlaybackEnded;
    }

    // ── Public API used by entry points (Albums view, etc.) ──────────────────

    /// <summary>
    /// Load and immediately play <paramref name="filePath"/>. The display fields
    /// (Title / Composer / Album) are set from the optional arguments; pass null
    /// to leave them blank.
    /// </summary>
    public void LoadAndPlay(string filePath, string? title, string? composer, string? album)
    {
        Title    = title;
        Composer = composer;
        Album    = album;
        _player.Load(filePath);
        _player.Play();
    }

    // ── Scrub coordination ───────────────────────────────────────────────────

    /// <summary>Called by the view when the user starts dragging the progress thumb.</summary>
    public void BeginScrub() => IsScrubbing = true;

    /// <summary>Called by the view when the user releases the thumb (or clicks the track) at <paramref name="seconds"/>.</summary>
    public void EndScrub(double seconds)
    {
        IsScrubbing = false;
        _player.Seek(TimeSpan.FromSeconds(seconds));
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SeekBack()
    {
        var target = _player.Position - TimeSpan.FromSeconds(SeekIncrementSeconds);
        _player.Seek(target);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player.State == PlayerState.Playing) _player.Pause();
        else _player.Play();
    }

    [RelayCommand]
    private void SeekForward()
    {
        var target = _player.Position + TimeSpan.FromSeconds(SeekIncrementSeconds);
        _player.Seek(target);
    }

    // ── Player event handlers ────────────────────────────────────────────────

    private void OnStateChanged(object? sender, EventArgs e)
    {
        IsTrackLoaded = _player.State != PlayerState.Empty;
        IsPlaying     = _player.State == PlayerState.Playing;
    }

    private void OnDurationKnown(object? sender, EventArgs e)
    {
        DurationSeconds = _player.Duration.TotalSeconds;
        // Reset the slider + displays for the freshly-loaded track.
        SliderValue = 0;
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        if (IsScrubbing) return;
        SliderValue = _player.Position.TotalSeconds;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        // No queue model yet — step 4 will wire auto-advance here.
        SliderValue = 0;
    }

    private void UpdateTimeDisplays(double sliderSeconds)
    {
        var elapsed   = TimeSpan.FromSeconds(Math.Max(0, sliderSeconds));
        var remaining = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds - sliderSeconds));
        ElapsedDisplay   = Format(elapsed);
        RemainingDisplay = "-" + Format(remaining);
    }

    /// <summary>m:ss for under an hour; h:mm:ss otherwise.</summary>
    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    public void Dispose()
    {
        _player.StateChanged    -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.DurationKnown   -= OnDurationKnown;
        _player.PlaybackEnded   -= OnPlaybackEnded;
    }
}
