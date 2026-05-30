using System.IO;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>
/// Outcome of a public play request — callers (Albums view, editor) use this
/// to decide whether to show a "no audio file" message.
/// </summary>
public enum PlayRequestResult
{
    Playing,
    NoAudioFile,
    AlbumHasNoTracks,
    TrackNotInAlbum,
}

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
    private readonly IArchiveAudioLocator _locator;
    private readonly IArchiveSettings _settings;

    // Playback context: which album we're playing and the flattened
    // (disc-ordered) sequence of its tracks plus our position in it. Empty
    // until the first successful Play*().
    private CanonAlbum? _currentAlbum;
    private List<TrackEntry> _currentSequence = new();
    private int _currentIndex;

    internal readonly record struct TrackEntry(AlbumDisc Disc, AlbumTrack Track);

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

    /// <summary>
    /// User-controlled sticky toggle. When true, <see cref="OnPlaybackEnded"/>
    /// suppresses auto-advance — the player stops at the end of the current
    /// track instead of moving to the next. Stays on until the user toggles
    /// it off (matches iTunes' "Stop After Current" menu semantics).
    /// </summary>
    [ObservableProperty]
    private bool _stopAfterCurrent;

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

    /// <summary>
    /// True between <see cref="BeginScrub"/> and <see cref="EndScrub"/>;
    /// suppresses playback-driven slider updates while the user is dragging.
    /// Promoted to an <c>[ObservableProperty]</c> (M1) so any binding /
    /// test / future scrub indicator can observe the change rather than
    /// having to poll. The setter stays effectively private — only the
    /// view-side scrub coordination methods below mutate it.
    /// </summary>
    [ObservableProperty]
    private bool _isScrubbing;

    private float _volume;
    /// <summary>
    /// Two-way bound to the volume slider. The setter clamps to [0, 1],
    /// pushes to the audio engine, and persists to <see cref="IArchiveSettings"/>
    /// so the level survives app restarts.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (SetProperty(ref _volume, clamped))
            {
                _player.Volume = clamped;
                _settings.PlayerVolume = clamped;
                _settings.Save();
            }
        }
    }

    public PlayerViewModel(IAudioPlayerService player, IArchiveAudioLocator locator, IArchiveSettings settings)
    {
        _player   = player;
        _locator  = locator;
        _settings = settings;

        // Restore persisted volume before the user can move the slider; the
        // engine carries it forward to every track Loaded later.
        _volume         = Math.Clamp(_settings.PlayerVolume, 0f, 1f);
        _player.Volume  = _volume;

        _player.StateChanged   += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationKnown  += OnDurationKnown;
        _player.PlaybackEnded  += OnPlaybackEnded;
    }

    // ── Public API used by entry points (Albums view, editor track list) ─────

    /// <summary>
    /// Play <paramref name="album"/> starting from its first track. Auto-advances
    /// across discs in (VolumeNumber, DiscNumber, TrackNumber) order until the
    /// album ends. Skips tracks whose audio file can't be located.
    /// </summary>
    public PlayRequestResult PlayAlbum(CanonAlbum album)
    {
        var seq = BuildSequence(album);
        if (seq.Count == 0) return PlayRequestResult.AlbumHasNoTracks;
        return StartAlbum(album, seq, startIndex: 0, requireExactStart: false);
    }

    /// <summary>
    /// Play <paramref name="album"/> starting from <paramref name="track"/> on
    /// <paramref name="disc"/>. The requested track must have a resolvable audio
    /// file — if not, returns <see cref="PlayRequestResult.NoAudioFile"/> and
    /// playback does not start. Subsequent tracks that fail to resolve are
    /// silently skipped on auto-advance.
    /// </summary>
    public PlayRequestResult PlayFromTrack(CanonAlbum album, AlbumDisc disc, AlbumTrack track)
    {
        var seq = BuildSequence(album);
        int idx = -1;
        for (int i = 0; i < seq.Count; i++)
            if (ReferenceEquals(seq[i].Disc, disc) && ReferenceEquals(seq[i].Track, track))
            { idx = i; break; }
        if (idx < 0) return PlayRequestResult.TrackNotInAlbum;
        return StartAlbum(album, seq, idx, requireExactStart: true);
    }

    /// <summary>
    /// Play a single track without auto-advance. Useful when the user wants
    /// to audition one track without listening to the rest of the album.
    /// </summary>
    public PlayRequestResult PlaySingleTrack(CanonAlbum album, AlbumDisc disc, AlbumTrack track)
    {
        var result = PlayFromTrack(album, disc, track);
        if (result == PlayRequestResult.Playing)
        {
            // Wipe the context so OnPlaybackEnded doesn't advance.
            _currentAlbum    = null;
            _currentSequence = new();
        }
        return result;
    }

    /// <summary>
    /// Play a loose track (one with no parent album). The locator's convention
    /// path needs an album/disc to compute folders, so loose tracks rely
    /// entirely on the per-track override paths (<see cref="AlbumTrack.FlacPath"/>
    /// / <see cref="AlbumTrack.Mp3Path"/>). Preferred-format ordering matches
    /// <see cref="IArchiveAudioLocator"/>. No auto-advance.
    /// </summary>
    public PlayRequestResult PlayLooseTrack(AlbumTrack track)
    {
        var prefer = _settings.PreferredAudioFormat;
        var first  = prefer == PreferredAudioFormat.Flac ? track.FlacPath : track.Mp3Path;
        var second = prefer == PreferredAudioFormat.Flac ? track.Mp3Path  : track.FlacPath;

        string? path = null;
        if (!string.IsNullOrWhiteSpace(first)  && File.Exists(first))  path = first;
        else if (!string.IsNullOrWhiteSpace(second) && File.Exists(second)) path = second;
        if (path is null) return PlayRequestResult.NoAudioFile;

        // Single-shot playback — no auto-advance for loose tracks.
        _currentAlbum    = null;
        _currentSequence = new();

        var firstRef = track.PieceRefs?.FirstOrDefault();
        Title    = track.DisplaySummary;
        Composer = firstRef?.Composer;
        Album    = null;

        _player.Load(path);
        _player.Play();
        return PlayRequestResult.Playing;
    }

    private PlayRequestResult StartAlbum(
        CanonAlbum album, List<TrackEntry> seq, int startIndex, bool requireExactStart)
    {
        _currentAlbum    = album;
        _currentSequence = seq;
        return TryPlayAt(startIndex, requireExactStart);
    }

    /// <summary>
    /// Tries to play the track at <paramref name="index"/>. When
    /// <paramref name="requireExact"/> is true and the locator misses, returns
    /// <see cref="PlayRequestResult.NoAudioFile"/> without falling through.
    /// Otherwise (auto-advance) skips forward until a playable track is found
    /// or the album ends.
    /// </summary>
    private PlayRequestResult TryPlayAt(int index, bool requireExact)
    {
        if (_currentAlbum is null) return PlayRequestResult.AlbumHasNoTracks;

        for (int i = index; i < _currentSequence.Count; i++)
        {
            var entry = _currentSequence[i];
            var hit = _locator.Resolve(_currentAlbum, entry.Disc, entry.Track);
            if (hit is null)
            {
                if (requireExact) return PlayRequestResult.NoAudioFile;
                continue;
            }
            _currentIndex = i;
            ApplyDisplay(_currentAlbum, entry);
            _player.Load(hit.Value.Path);
            _player.Play();
            return PlayRequestResult.Playing;
        }
        return PlayRequestResult.NoAudioFile;
    }

    private void ApplyDisplay(CanonAlbum album, TrackEntry entry)
    {
        // Composer comes from the first PieceRef; uncatalogued tracks have none.
        var firstRef = entry.Track.PieceRefs?.FirstOrDefault();
        Title    = entry.Track.DisplaySummary;
        Composer = firstRef?.Composer;
        Album    = album.DisplayTitle;
    }

    internal static List<TrackEntry> BuildSequence(CanonAlbum album)
    {
        var list = new List<TrackEntry>();
        // Null-volume discs sort LAST so a mixed album that combines Vol 1 +
        // Vol 2 + a bonus disc without a volume doesn't interleave the bonus
        // disc ahead of Vol 1. Pre-fix `?? 0` collapsed nulls into the same
        // bucket as "Vol 0" (an unlikely but possible legitimate value),
        // putting them ahead of every numbered volume. See Rework M8.
        foreach (var disc in album.Discs
                     .OrderBy(d => d.VolumeNumber ?? int.MaxValue)
                     .ThenBy(d => d.DiscNumber))
        {
            foreach (var t in disc.Tracks.OrderBy(t => t.TrackNumber))
                list.Add(new TrackEntry(disc, t));
        }
        return list;
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
        SliderValue = 0;
        // Auto-advance through the album. Tracks that fail to resolve are
        // skipped silently; on end-of-album the player just stops at the
        // last successful track's end position. StopAfterCurrent (set by
        // the user via the player-bar toggle) suppresses the auto-advance.
        if (_currentAlbum is null || StopAfterCurrent) return;
        TryPlayAt(_currentIndex + 1, requireExact: false);
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
