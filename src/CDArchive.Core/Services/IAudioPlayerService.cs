namespace CDArchive.Core.Services;

/// <summary>
/// Playback state of <see cref="IAudioPlayerService"/>.
///
/// Transitions:
///   • Empty  → Stopped         on Load(path)
///   • Stopped/Paused → Playing on Play()
///   • Playing → Paused         on Pause()
///   • any (with track) → Stopped on Stop()
///   • Playing → Stopped        on natural track end (also raises PlaybackEnded)
/// </summary>
public enum PlayerState
{
    Empty,
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Single-track audio playback service backing the player UI. Plays exactly one
/// file at a time; switching tracks is "Load(newPath) → Play()". Queue / next-track
/// logic lives in the player ViewModel, not here.
///
/// All events are raised on the synchronization context that was current when the
/// service was constructed (i.e. the WPF UI thread under normal DI). Tests get
/// inline event raising when no sync context is present.
/// </summary>
public interface IAudioPlayerService : IDisposable
{
    PlayerState State { get; }

    /// <summary>Current playback position. <see cref="TimeSpan.Zero"/> when no track is loaded.</summary>
    TimeSpan Position { get; }

    /// <summary>Total duration of the loaded track, or <see cref="TimeSpan.Zero"/> when none.</summary>
    TimeSpan Duration { get; }

    /// <summary>Path of the currently loaded file, or null when in <see cref="PlayerState.Empty"/>.</summary>
    string? CurrentFilePath { get; }

    /// <summary>
    /// Output volume in [0.0, 1.0]. Setter applies to the active output (if
    /// any) and is retained so a subsequent <see cref="Load"/> picks up the
    /// last-set value. Out-of-range values are clamped.
    /// </summary>
    float Volume { get; set; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised periodically while playing as <see cref="Position"/> advances.</summary>
    event EventHandler? PositionChanged;

    /// <summary>Raised after <see cref="Load"/> as soon as <see cref="Duration"/> is known.</summary>
    event EventHandler? DurationKnown;

    /// <summary>
    /// Raised when playback reaches its natural end with no gapless next track
    /// queued (end of the queued run, or stop-after-current). Used by the player
    /// VM to advance gaplessly-impossible cases / stop.
    /// </summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// Raised when the output transitions gaplessly from the current track to a
    /// track previously supplied via <see cref="QueueNext"/>. By the time this
    /// fires, <see cref="Position"/> / <see cref="Duration"/> /
    /// <see cref="CurrentFilePath"/> already reflect the new track. The player VM
    /// uses this to update the now-playing display and queue the following track.
    /// </summary>
    event EventHandler? TrackTransitioned;

    /// <summary>
    /// Pre-loads <paramref name="filePath"/> so playback continues into it
    /// <em>gaplessly</em> when the current track ends (no device teardown). The
    /// next track must share the current output format (sample rate / channels /
    /// bit depth); returns false when it doesn't, the file is missing, or nothing
    /// is currently loaded — in which case the caller falls back to a normal
    /// <see cref="Load"/> at end of track. Pass <c>null</c> to clear a previously
    /// queued next track. Replaces any already-queued next.
    /// </summary>
    bool QueueNext(string? filePath);

    /// <summary>
    /// Load a track from <paramref name="filePath"/>. Stops any current playback,
    /// transitions to <see cref="PlayerState.Stopped"/> at position 0, and raises
    /// <see cref="DurationKnown"/>. Throws on missing file or unsupported format.
    /// </summary>
    void Load(string filePath);

    void Play();
    void Pause();
    void Stop();

    /// <summary>
    /// Seek to the given absolute position. Clamped to [0, Duration]. No-op when
    /// no track is loaded. Does not change <see cref="State"/>.
    /// </summary>
    void Seek(TimeSpan position);
}
