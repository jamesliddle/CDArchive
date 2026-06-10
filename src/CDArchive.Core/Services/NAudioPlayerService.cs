using NAudio.Wave;

namespace CDArchive.Core.Services;

/// <summary>
/// <see cref="IAudioPlayerService"/> backed by NAudio, with <see cref="WaveOutEvent"/>
/// for output.
///
/// <para>
/// Decoding is format-specific so both duration AND seeking are accurate:
/// <list type="bullet">
///   <item>MP3 → <see cref="Mp3FileReader"/>, which sums actual frame
///         durations (accurate length) and seeks frame-accurately.
///         <see cref="MediaFoundationReader"/> only *estimates* MP3 length and
///         undershoots VBR / encoder-delay files.</item>
///   <item>FLAC / WAV / everything else → <see cref="MediaFoundationReader"/>
///         (native on Windows 10 1709+ / 11) wrapped in
///         <see cref="DecodeSeekStream"/>. MF's FLAC *seek* is coarse and
///         undershoots the requested position by up to ~5s while still
///         reporting the requested time — so after a seek the slider raced
///         ahead of the audio and hit -0:00 with seconds of music left. The
///         wrapper makes seeks sample-accurate by decoding forward to the
///         target instead of trusting MF's native seek.</item>
/// </list>
/// </para>
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

    // Output buffer latency, ms. WaveOutEvent reads ahead from the reader to
    // keep this many ms of audio buffered, so the reader's CurrentTime (our
    // reported Position) leads the audible position by roughly this much. The
    // NAudio default is 300ms, which made the progress slider reach the end
    // ~300ms before the track finished. 100ms is comfortably glitch-free for
    // local file decode while keeping the visual lead small.
    private const int OutputLatencyMs = 100;

    // The output device is fed by a GaplessProvider that holds the current track
    // plus an optional pre-loaded next track, so auto-advance hands over without
    // tearing down + reopening the device (which caused an audible gap). The
    // device runs continuously until the provider has no more tracks.
    private GaplessProvider? _provider;
    private WaveOutEvent? _output;
    private string? _queuedPath;   // path queued via QueueNext, promoted on transition
    private bool _stoppedByUser;
    private bool _disposed;

    internal bool IsDisposed => _disposed;

    public PlayerState State { get; private set; } = PlayerState.Empty;
    public string? CurrentFilePath { get; private set; }

    // The reader's clock is trustworthy: MP3 seeks frame-accurately, and FLAC is
    // wrapped in DecodeSeekStream so its CurrentTime-seek lands exactly. The
    // provider forwards to whichever track is currently playing.
    public TimeSpan Position => _provider?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _provider?.TotalTime ?? TimeSpan.Zero;

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
    public event EventHandler? TrackTransitioned;

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

        var reader = OpenReader(filePath);
        _provider = new GaplessProvider(reader, OnTrackTransitioned);
        _output = new WaveOutEvent { DesiredLatency = OutputLatencyMs };
        _output.Init(_provider);
        _output.Volume = _volume;
        _output.PlaybackStopped += OnPlaybackStoppedFromNAudio;

        CurrentFilePath = filePath;
        _queuedPath     = null;
        _stoppedByUser  = false;
        SetState(PlayerState.Stopped);
        Raise(DurationKnown);
    }

    /// <summary>
    /// Frame-accurate reader for MP3 (native accurate seek). FLAC / WAV /
    /// anything else goes through <see cref="MediaFoundationReader"/> wrapped in
    /// <see cref="DecodeSeekStream"/> so seeking is sample-accurate (MF's FLAC
    /// seek is coarse — see class doc). Both throw on unsupported / corrupt input.
    /// </summary>
    private static WaveStream OpenReader(string filePath) =>
        IsMp3(filePath)
            ? new Mp3FileReader(filePath)
            : new DecodeSeekStream(() => new MediaFoundationReader(filePath));

    public bool QueueNext(string? filePath)
    {
        if (_provider is null) return false;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _provider.SetNext(null);
            _queuedPath = null;
            return true;
        }
        if (!File.Exists(filePath)) return false;

        WaveStream reader;
        try { reader = OpenReader(filePath); }
        catch { return false; }

        if (!_provider.SetNext(reader))
        {
            reader.Dispose(); // format mismatch — provider didn't take ownership
            return false;
        }
        _queuedPath = filePath;
        return true;
    }

    /// <summary>
    /// Invoked by the provider (on the audio thread) when it hands over to the
    /// queued next track. Position / Duration already reflect the new track;
    /// surface the change so the VM updates its display and re-queues.
    /// </summary>
    private void OnTrackTransitioned()
    {
        CurrentFilePath = _queuedPath;
        _queuedPath = null;
        Raise(TrackTransitioned);
        Raise(DurationKnown); // new track → new duration; VM resets the slider
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
        if (_provider is null) return;
        // The provider clamps to the current track's duration and routes the
        // seek to whichever track is playing. For FLAC this lands the audio
        // exactly via DecodeSeekStream; for MP3 it's a native frame-accurate seek.
        _provider.Seek(position);
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
            _provider?.Seek(TimeSpan.Zero);
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

    private static bool IsMp3(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".mp3", StringComparison.OrdinalIgnoreCase);

    private void DisposeStream()
    {
        // Dispose the output FIRST so its read thread stops before the provider
        // (and its readers) go away.
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStoppedFromNAudio;
            _output.Dispose();
            _output = null;
        }
        if (_provider is not null)
        {
            _provider.Dispose();
            _provider = null;
        }
        CurrentFilePath = null;
        _queuedPath = null;
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

/// <summary>
/// An <see cref="IWaveProvider"/> that plays a <em>current</em> track and, when
/// it ends, hands over seamlessly to an optional pre-loaded <em>next</em> track
/// — so the output device never stops between tracks (gapless auto-advance).
/// The next track must share the current track's wave format.
///
/// <para>
/// Threading: <see cref="Read"/> runs on the audio output thread and is the only
/// place the current reader is consumed and swapped. <see cref="SetNext"/> (any
/// thread) and the position accessors / <see cref="Seek"/> (UI thread) take a
/// lock that's also held around the swap in <see cref="Read"/>, so the current
/// reader is never disposed out from under a UI-thread read. The decode read
/// itself runs outside the lock so a slow read can't block the UI.
/// </para>
/// </summary>
internal sealed class GaplessProvider : IWaveProvider
{
    private readonly WaveFormat _format;
    private readonly Action _onTransition;
    private readonly object _lock = new();

    private WaveStream _current;
    private WaveStream? _next;

    public GaplessProvider(WaveStream current, Action onTransition)
    {
        _current = current;
        _format = current.WaveFormat;
        _onTransition = onTransition;
    }

    public WaveFormat WaveFormat => _format;

    /// <summary>
    /// Sets (or clears, when null) the pre-loaded next track. Returns false when
    /// the candidate's format doesn't match the session — the caller keeps
    /// ownership and should dispose it. Replaces any previously-queued next.
    /// </summary>
    public bool SetNext(WaveStream? next)
    {
        if (next is not null && !FormatsMatch(next.WaveFormat, _format))
            return false;
        lock (_lock)
        {
            _next?.Dispose();
            _next = next;
        }
        return true;
    }

    public TimeSpan CurrentTime { get { lock (_lock) return _current.CurrentTime; } }
    public TimeSpan TotalTime  { get { lock (_lock) return _current.TotalTime; } }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            var total = _current.TotalTime;
            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                        : position > total ? total
                        : position;
            _current.CurrentTime = clamped;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            // Read outside the lock — a DecodeSeekStream read can be slow and
            // must not block UI-thread position reads. Only this (audio) thread
            // writes _current, via PromoteNext below, so the field is consistent.
            int n = _current.Read(buffer, offset + total, count - total);
            if (n > 0) { total += n; continue; }
            if (!PromoteNext()) break; // current ended and no next: end of session
        }
        return total;
    }

    private bool PromoteNext()
    {
        lock (_lock)
        {
            if (_next is null) return false;
            _current.Dispose();
            _current = _next;
            _next = null;
        }
        _onTransition(); // notify outside the lock (it just posts to the UI thread)
        return true;
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate &&
        a.Channels == b.Channels &&
        a.BitsPerSample == b.BitsPerSample &&
        a.Encoding == b.Encoding;

    public void Dispose()
    {
        lock (_lock)
        {
            _current.Dispose();
            _next?.Dispose();
            _next = null;
        }
    }
}

/// <summary>
/// A <see cref="WaveStream"/> wrapper that makes seeking sample-accurate by
/// <em>decoding</em> to the requested position rather than relying on the inner
/// reader's native seek. Built for <see cref="MediaFoundationReader"/> + FLAC,
/// whose seek is coarse (it can land several seconds before the requested
/// position while still reporting the requested time).
///
/// <para>
/// Linear decoding is sample-accurate, so:
/// <list type="bullet">
///   <item><b>Forward</b> seeks read-and-discard from the current position up
///         to the target — cheap (proportional to the jump distance).</item>
///   <item><b>Backward</b> seeks rebuild the inner reader from the start (via
///         the supplied factory) and decode forward to the target.</item>
/// </list>
/// </para>
///
/// <para>
/// Threading: the inner reader is only ever touched inside <see cref="Read"/>
/// (the audio output thread). A pending seek is requested from any thread by
/// setting <see cref="Position"/> and is <em>applied lazily</em> at the top of
/// the next <see cref="Read"/>, so the inner reader is never read concurrently.
/// <see cref="WaveFormat"/> and <see cref="Length"/> are cached at construction
/// so callers on other threads never touch the inner reader (which is briefly
/// disposed + recreated during a backward seek).
/// </para>
/// </summary>
internal sealed class DecodeSeekStream : WaveStream
{
    private readonly Func<WaveStream> _openFresh;
    private readonly WaveFormat _waveFormat;
    private readonly long _length;
    private readonly byte[] _discardBuffer;
    private readonly object _lock = new();

    private WaveStream _inner;       // touched only on the Read (audio) thread
    private long? _pendingSeekBytes; // guarded by _lock
    private long _reportedPosition;  // guarded by _lock

    public DecodeSeekStream(Func<WaveStream> openFresh)
    {
        _openFresh = openFresh;
        _inner = openFresh();
        _waveFormat = _inner.WaveFormat;
        _length = _inner.Length;
        // ~200ms discard chunk.
        _discardBuffer = new byte[Math.Max(_waveFormat.AverageBytesPerSecond / 5, _waveFormat.BlockAlign)];
    }

    public override WaveFormat WaveFormat => _waveFormat;
    public override long Length => _length;

    public override long Position
    {
        get { lock (_lock) return _pendingSeekBytes ?? _reportedPosition; }
        set
        {
            var clamped = Math.Clamp(value, 0, _length);
            // Snap to a block boundary so the inner PCM stream stays aligned.
            clamped -= clamped % _waveFormat.BlockAlign;
            lock (_lock)
            {
                _pendingSeekBytes = clamped;
                _reportedPosition = clamped; // report the target right away
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ApplyPendingSeek();
        int n = _inner.Read(buffer, offset, count);
        lock (_lock) _reportedPosition = _inner.Position;
        return n;
    }

    private void ApplyPendingSeek()
    {
        long target;
        lock (_lock)
        {
            if (_pendingSeekBytes is not { } pending) return;
            target = pending;
            _pendingSeekBytes = null;
        }

        // Backward → only sample-accurate path is to decode from the start.
        if (target < _inner.Position)
        {
            var fresh = _openFresh();
            _inner.Dispose();
            _inner = fresh;
        }

        // Decode-and-discard forward to the target.
        long toDiscard = target - _inner.Position;
        while (toDiscard > 0)
        {
            int chunk = (int)Math.Min(_discardBuffer.Length, toDiscard);
            int got = _inner.Read(_discardBuffer, 0, chunk);
            if (got <= 0) break; // EOF — can't go further
            toDiscard -= got;
        }

        lock (_lock) _reportedPosition = _inner.Position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
