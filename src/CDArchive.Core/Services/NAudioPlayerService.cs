using NAudio.Wave;

namespace CDArchive.Core.Services;

/// <summary>
/// <see cref="IAudioPlayerService"/> backed by NAudio. Uses
/// <see cref="MediaFoundationReader"/> for decoding (handles MP3 and FLAC
/// natively on Windows 10 1709+ and Windows 11) and <see cref="WaveOutEvent"/>
/// for output.
///
/// Threading: NAudio raises <see cref="WaveOutEvent.PlaybackStopped"/> on a
/// pool thread. The constructor takes an explicit <see cref="SynchronizationContext"/>
/// (the WPF dispatcher's, supplied by DI from <c>App.OnStartup</c> where the
/// UI thread is guaranteed); all public events are Posted through it so
/// consumers see them on the expected thread. Position polling runs on a
/// <see cref="System.Threading.Timer"/> and uses the same dispatch.
///
/// The earlier design captured <c>SynchronizationContext.Current</c> in the
/// ctor body — that worked only because the DI container happens to build
/// singletons on the UI thread today. A future background-thread resolve
/// (startup warm-up, headless mode, a test harness) would silently capture
/// null or a worker-thread context and break WPF binding marshalling. Making
/// the capture explicit at the App layer pins the dependency where the
/// UI-thread invariant is enforceable. See Rework C2.
///
/// Passing <c>null</c> (or omitting the parameter) means "raise events on
/// whatever thread NAudio gives us" — the test-mode behaviour used by
/// fixtures with no dispatcher.
/// </summary>
public sealed class NAudioPlayerService : IAudioPlayerService
{
    private readonly SynchronizationContext? _sync;
    private readonly System.Threading.Timer _positionTimer;

    private MediaFoundationReader? _reader;
    private WaveOutEvent? _output;
    private bool _stoppedByUser;
    private bool _disposed;

    internal bool IsDisposed => _disposed;

    public PlayerState State { get; private set; } = PlayerState.Empty;
    public string? CurrentFilePath { get; private set; }

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    private float _volume = 1.0f;
    /// <summary>
    /// Output volume. Applied to <c>WaveOutEvent.Volume</c> when an output
    /// exists; held in the backing field so the next <see cref="Load"/> picks
    /// up the same level. Clamped to [0, 1].
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_output is not null) _output.Volume = _volume;
        }
    }

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? DurationKnown;
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// Constructs the player with an explicit <see cref="SynchronizationContext"/>
    /// (typically the WPF dispatcher's). When non-null and distinct from the
    /// caller's current context, all public events are Posted through it so
    /// consumers receive them on the captured thread. Pass <c>null</c> (or
    /// omit) to raise events inline on whatever thread NAudio uses — the
    /// test-mode behaviour.
    /// </summary>
    public NAudioPlayerService(SynchronizationContext? sync = null)
    {
        _sync = sync;
        _positionTimer = new System.Threading.Timer(
            _ => Raise(PositionChanged), state: null,
            dueTime: Timeout.Infinite, period: Timeout.Infinite);
    }

    public void Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be non-empty.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found.", filePath);

        DisposeStream();

        // MediaFoundationReader throws on unsupported formats; let the caller see it.
        _reader = new MediaFoundationReader(filePath);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.Volume = _volume;
        _output.PlaybackStopped += OnPlaybackStoppedFromNAudio;

        CurrentFilePath = filePath;
        _stoppedByUser  = false;
        SetState(PlayerState.Stopped);
        Raise(DurationKnown);
    }

    public void Play()
    {
        if (_output is null || State == PlayerState.Playing) return;
        _output.Play();
        StartPositionTimer();
        SetState(PlayerState.Playing);
    }

    public void Pause()
    {
        if (_output is null || State != PlayerState.Playing) return;
        _output.Pause();
        StopPositionTimer();
        SetState(PlayerState.Paused);
    }

    public void Stop()
    {
        if (_output is null || State == PlayerState.Empty || State == PlayerState.Stopped) return;
        _stoppedByUser = true;
        _output.Stop();
        StopPositionTimer();
        Seek(TimeSpan.Zero);
        SetState(PlayerState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                    : position > _reader.TotalTime ? _reader.TotalTime
                    : position;
        _reader.CurrentTime = clamped;
        Raise(PositionChanged);
    }

    private void OnPlaybackStoppedFromNAudio(object? sender, StoppedEventArgs e)
    {
        // Fires on a pool thread for both natural-end and user-stop. Distinguish
        // by the flag set in Stop(); reset it once consumed.
        bool naturalEnd = !_stoppedByUser;
        _stoppedByUser = false;
        StopPositionTimer();

        if (naturalEnd)
        {
            // Reset position so the next Play() doesn't start at the end.
            if (_reader is not null) _reader.CurrentTime = TimeSpan.Zero;
            SetState(PlayerState.Stopped);
            Raise(PlaybackEnded);
        }
    }

    private void SetState(PlayerState s)
    {
        if (State == s) return;
        State = s;
        Raise(StateChanged);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        if (_sync is not null && _sync != SynchronizationContext.Current)
            _sync.Post(_ => handler.Invoke(this, EventArgs.Empty), null);
        else
            handler.Invoke(this, EventArgs.Empty);
    }

    private void StartPositionTimer() =>
        _positionTimer.Change(dueTime: 100, period: 100);

    private void StopPositionTimer() =>
        _positionTimer.Change(dueTime: Timeout.Infinite, period: Timeout.Infinite);

    private void DisposeStream()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStoppedFromNAudio;
            _output.Dispose();
            _output = null;
        }
        if (_reader is not null)
        {
            _reader.Dispose();
            _reader = null;
        }
        CurrentFilePath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPositionTimer();
        _positionTimer.Dispose();
        DisposeStream();
        SetState(PlayerState.Empty);
    }
}
