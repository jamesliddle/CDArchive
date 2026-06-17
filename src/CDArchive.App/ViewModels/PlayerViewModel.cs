using System.IO;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    private readonly IAudioPlayerService _player;
    private readonly IArchiveAudioLocator _locator;
    private readonly IAlbumArtworkLocator? _artworkLocator;
    private readonly IArchiveSettings _settings;
    private readonly ICanonDataService _data;
    private readonly ILogger<PlayerViewModel> _logger;

    // Composer name -> lifespan (e.g. "(1858–1924)") for the hover tooltip,
    // loaded once at construction. Null until the async load completes; a miss
    // simply omits the dates. Stale if composers are edited mid-session — an
    // accepted trade for a tooltip enrichment.
    private Dictionary<string, string>? _composerLifespans;

    // Playback context: which album we're playing and the flattened
    // (disc-ordered) sequence of its tracks plus our position in it. Empty
    // until the first successful Play*().
    private CanonAlbum? _currentAlbum;
    private List<TrackEntry> _currentSequence = new();
    private int _currentIndex;

    // The track pre-queued with the audio engine for gapless auto-advance, and
    // its resolved file path. -1 when nothing is queued (end of album,
    // stop-after-current, or a format mismatch that forces a gapful fallback).
    private int _queuedIndex = -1;
    private string? _queuedFilePath;

    internal readonly record struct TrackEntry(AlbumDisc Disc, AlbumTrack Track);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackTitleLine))]
    private string? _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackDetailLine))]
    [NotifyPropertyChangedFor(nameof(HasTrackDetail))]
    private string? _composer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackDetailLine))]
    [NotifyPropertyChangedFor(nameof(HasTrackDetail))]
    private string? _performers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackDetailLine))]
    [NotifyPropertyChangedFor(nameof(HasTrackDetail))]
    private string? _album;

    /// <summary>
    /// A complete multi-line summary of the current track — album title,
    /// composer, the piece / subpiece hierarchy (progressively indented), and
    /// the full performer list with roles / instruments. Surfaced as the hover
    /// tooltip over the caption. Null when nothing is loaded (no tooltip).
    /// </summary>
    [ObservableProperty]
    private string? _trackTooltip;

    /// <summary>
    /// Caption line 1: the track title in the player bar's primary font.
    /// Falls back to "No track loaded" when nothing has been loaded yet.
    /// </summary>
    public string TrackTitleLine =>
        string.IsNullOrWhiteSpace(Title) ? "No track loaded" : Title!;

    /// <summary>
    /// Caption line 2: "Composer — Performers — Album", omitting empty parts.
    /// Rendered in a smaller font beneath the title. Empty when no track is
    /// loaded (line 2 collapses via <see cref="HasTrackDetail"/>).
    /// </summary>
    public string TrackDetailLine =>
        string.Join(" — ", new[] { Composer, Performers, Album }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Drives the visibility of caption line 2 — collapses the row
    /// when there's no composer / performer / album to show.</summary>
    public bool HasTrackDetail => !string.IsNullOrEmpty(TrackDetailLine);

    /// <summary>
    /// Full on-disk path of the file currently playing (caption line 3).
    /// Always tracked; only shown when <see cref="ShowFilePath"/> is true.
    /// </summary>
    [ObservableProperty]
    private string? _playingFilePath;

    /// <summary>
    /// Drives the visibility of caption line 3 (the file path). True when the
    /// "Show playing file path" setting is on and a file is loaded. Re-evaluated
    /// per track load and on demand via <see cref="RefreshCaptionOptions"/> so a
    /// settings toggle takes effect immediately on the current track.
    /// </summary>
    [ObservableProperty]
    private bool _showFilePath;

    /// <summary>true when the player has a track loaded; drives bar enabled/greyed state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverSlotVisible))]
    private bool _isTrackLoaded;

    /// <summary>
    /// Resolved cover art for the current track (embedded-tag art preferred,
    /// then a folder image), or null when none (or a loose track is playing).
    /// The player bar binds an Image to this via the AlbumArtwork converter and
    /// falls back to a placeholder glyph when null (see <see cref="HasArtwork"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtwork))]
    private AlbumArtwork? _currentArtwork;

    /// <summary>True when cover art was resolved for the current track — drives
    /// the player bar's image-vs-placeholder swap.</summary>
    public bool HasArtwork => CurrentArtwork is not null;

    /// <summary>Whether the player bar shows a cover-art thumbnail at all
    /// (settings-driven). When off, the cover slot is hidden entirely.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverSlotVisible))]
    private bool _showArtwork = true;

    /// <summary>Side length (DIP) of the player cover thumbnail, from the
    /// player artwork-size setting.</summary>
    [ObservableProperty]
    private double _artworkBoxSize = 40;

    /// <summary>Drives the cover slot's visibility: a track is loaded AND the
    /// player-artwork setting is on.</summary>
    public bool CoverSlotVisible => IsTrackLoaded && ShowArtwork;

    /// <summary>Seek-back step in seconds (settings-driven) — shown in the back glyph.</summary>
    public int SeekBackSeconds => Math.Clamp(_settings.SeekBackwardSeconds, 1, 60);

    /// <summary>Seek-forward step in seconds (settings-driven) — shown in the forward glyph.</summary>
    public int SeekForwardSeconds => Math.Clamp(_settings.SeekForwardSeconds, 1, 60);

    /// <summary>True when there's an earlier track on the current album. Drives
    /// the "go to previous" branch of the Previous button.</summary>
    [ObservableProperty]
    private bool _hasPreviousTrack;

    /// <summary>True when there's a later track on the current album to skip
    /// forward to. Drives the next-track button's enabled state.</summary>
    [ObservableProperty]
    private bool _hasNextTrack;

    /// <summary>
    /// Whether the Previous button is enabled: there's an earlier track to go
    /// to, OR the position is past the restart threshold so the button can
    /// restart the current track. Recomputed on track + position changes.
    /// </summary>
    [ObservableProperty]
    private bool _canPreviousTrack;

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

    public PlayerViewModel(
        IAudioPlayerService player,
        IArchiveAudioLocator locator,
        IArchiveSettings settings,
        ICanonDataService data,
        ILogger<PlayerViewModel>? logger = null,
        IAlbumArtworkLocator? artworkLocator = null)
    {
        _player   = player;
        _locator  = locator;
        _settings = settings;
        _data     = data;
        _logger   = logger ?? NullLogger<PlayerViewModel>.Instance;
        _artworkLocator = artworkLocator;

        // Restore persisted volume before the user can move the slider; the
        // engine carries it forward to every track Loaded later.
        _volume         = Math.Clamp(_settings.PlayerVolume, 0f, 1f);
        _player.Volume  = _volume;

        _player.StateChanged     += OnStateChanged;
        _player.PositionChanged  += OnPositionChanged;
        _player.DurationKnown    += OnDurationKnown;
        _player.PlaybackEnded    += OnPlaybackEnded;
        _player.TrackTransitioned += OnTrackTransitioned;

        RefreshArtworkOptions();

        // Best-effort, fire-and-forget: warm the composer-dates cache for the
        // hover tooltip. Failures are non-fatal — the tooltip just omits dates.
        _ = LoadComposerLifespansAsync();
    }

    /// <summary>Re-reads the player cover-art settings (show + size). Called at
    /// construction and after a settings save so a change applies immediately.</summary>
    public void RefreshArtworkOptions()
    {
        ShowArtwork    = _settings.ShowPlayerArtwork;
        ArtworkBoxSize = Helpers.ArtworkSizes.PlayerBox(_settings.PlayerArtworkSize);
    }

    private async Task LoadComposerLifespansAsync()
    {
        try
        {
            var composers = await _data.LoadComposersAsync();
            if (composers is null) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in composers)
            {
                if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrEmpty(c.LifeSpan))
                    map[c.Name] = c.LifeSpan;
            }
            _composerLifespans = map;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load composer dates for the player tooltip; dates will be omitted.");
        }
    }

    /// <summary>Lifespan for a composer name, or null when unknown / not yet loaded.</summary>
    private string? LookupComposerLifespan(string name) =>
        _composerLifespans is { } map && map.TryGetValue(name, out var span) ? span : null;

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
            // Wipe the context so neither the gapless queue nor OnPlaybackEnded
            // advances. (PlayFromTrack queued the album's next track — undo it.)
            _currentAlbum    = null;
            _currentSequence = new();
            _queuedIndex     = -1;
            _player.QueueNext(null);
            UpdateTrackNavState(); // no album context → prev/next disabled
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
        _queuedIndex     = -1;

        var firstRef = track.PieceRefs?.FirstOrDefault();
        Title        = BuildCaptionTitle(track.PieceRefs, track.Description);
        Composer     = firstRef?.Composer;
        Performers   = FormatPerformers(track.Performers);
        Album        = null;
        // Loose tracks have no album folder, but their own file may carry
        // embedded cover art (read directly from the override path's tags).
        CurrentArtwork = _artworkLocator?.ResolveFromTrack(track);
        TrackTooltip = BuildTrackTooltip(
            albumTitle: null, track.PieceRefs, track.Description, track.Performers,
            LookupComposerLifespan);
        PlayingFilePath = path;
        RefreshCaptionOptions();
        UpdateTrackNavState(); // loose track → no album context → prev/next disabled

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
            PlayingFilePath = hit.Value.Path;
            RefreshCaptionOptions();
            _player.Load(hit.Value.Path);
            _player.Play();
            UpdateTrackNavState();
            QueueNextTrack(); // pre-load the next track for gapless auto-advance
            return PlayRequestResult.Playing;
        }
        return PlayRequestResult.NoAudioFile;
    }

    /// <summary>
    /// Pre-loads the next playable album track into the audio engine so the
    /// auto-advance hand-off is gapless. Skips when there's no album, when
    /// stop-after-current is on, or when the engine declines (format mismatch) —
    /// in which case the natural-end fallback in <see cref="OnPlaybackEnded"/>
    /// advances with the usual small gap.
    /// </summary>
    private void QueueNextTrack()
    {
        _queuedIndex = -1;
        _queuedFilePath = null;

        if (_currentAlbum is null || _settings.StopAfterCurrentTrack)
        {
            _player.QueueNext(null);
            return;
        }

        for (int i = _currentIndex + 1; i < _currentSequence.Count; i++)
        {
            var entry = _currentSequence[i];
            var hit = _locator.Resolve(_currentAlbum, entry.Disc, entry.Track);
            if (hit is null) continue;

            if (_player.QueueNext(hit.Value.Path))
            {
                _queuedIndex = i;
                _queuedFilePath = hit.Value.Path;
            }
            else
            {
                _player.QueueNext(null); // couldn't queue → leave to gapful fallback
            }
            return; // the immediate next playable track is what auto-advance uses
        }

        _player.QueueNext(null); // no further track
    }

    /// <summary>
    /// Audio engine transitioned gaplessly into the pre-queued next track. Move
    /// our context to it, refresh the now-playing display, and queue the one
    /// after. (Duration / slider reset is handled by the engine's DurationKnown.)
    /// </summary>
    private void OnTrackTransitioned(object? sender, EventArgs e)
    {
        if (_currentAlbum is null || _queuedIndex < 0 || _queuedIndex >= _currentSequence.Count)
            return;

        _currentIndex = _queuedIndex;
        ApplyDisplay(_currentAlbum, _currentSequence[_currentIndex]);
        PlayingFilePath = _queuedFilePath;
        RefreshCaptionOptions();
        UpdateTrackNavState();
        QueueNextTrack();
    }

    /// <summary>
    /// Plays the nearest playable track at or before <paramref name="index"/>
    /// (searching backward, skipping tracks whose audio can't be located).
    /// No-op when there's no album context or nothing playable earlier.
    /// </summary>
    private void TryPlayBackwardFrom(int index)
    {
        if (_currentAlbum is null) return;
        for (int i = index; i >= 0 && i < _currentSequence.Count; i--)
        {
            var entry = _currentSequence[i];
            var hit = _locator.Resolve(_currentAlbum, entry.Disc, entry.Track);
            if (hit is null) continue;
            _currentIndex = i;
            ApplyDisplay(_currentAlbum, entry);
            PlayingFilePath = hit.Value.Path;
            RefreshCaptionOptions();
            _player.Load(hit.Value.Path);
            _player.Play();
            UpdateTrackNavState();
            QueueNextTrack();
            return;
        }
    }

    /// <summary>Recomputes the previous/next-track button enabled state for the
    /// current album position. Single/loose tracks have no album context, so
    /// both are false.</summary>
    private void UpdateTrackNavState()
    {
        HasPreviousTrack = _currentAlbum is not null && _currentIndex > 0;
        HasNextTrack     = _currentAlbum is not null && _currentIndex < _currentSequence.Count - 1;
        UpdatePreviousNavState();
    }

    /// <summary>
    /// Recomputes <see cref="CanPreviousTrack"/> (the Previous button's enabled
    /// state). Enabled when there's an earlier track, or the position is past
    /// the restart threshold so Previous can restart the current track. Called
    /// on track changes and on every position tick (to catch the threshold
    /// crossing on the first track).
    /// </summary>
    private void UpdatePreviousNavState()
    {
        bool pastThreshold = IsTrackLoaded
            && _player.Position.TotalSeconds > _settings.PreviousRestartThresholdSeconds;
        CanPreviousTrack = HasPreviousTrack || pastThreshold;
    }

    private void ApplyDisplay(CanonAlbum album, TrackEntry entry)
    {
        // Composer comes from the first PieceRef; uncatalogued tracks have none.
        // Title omits the composer (it's shown on the detail line instead).
        // Performers: track-level override wins, else the album-level credits.
        var firstRef = entry.Track.PieceRefs?.FirstOrDefault();
        var effectivePerformers = entry.Track.Performers ?? album.Performers;
        Title        = BuildCaptionTitle(entry.Track.PieceRefs, entry.Track.Description);
        Composer     = firstRef?.Composer;
        Performers   = FormatPerformers(effectivePerformers);
        Album        = album.DisplayTitle;
        CurrentArtwork = _artworkLocator?.Resolve(album, entry.Disc, entry.Track);
        TrackTooltip = BuildTrackTooltip(
            album.DisplayTitle, entry.Track.PieceRefs, entry.Track.Description, effectivePerformers,
            LookupComposerLifespan);
    }

    /// <summary>
    /// Re-evaluates caption line 3 visibility from the current
    /// <see cref="IArchiveSettings.ShowPlayingFilePath"/> setting. Called per
    /// track load and by <c>SettingsViewModel</c> after a save so toggling the
    /// setting updates the currently-playing track immediately.
    /// </summary>
    public void RefreshCaptionOptions() =>
        ShowFilePath = _settings.ShowPlayingFilePath && !string.IsNullOrEmpty(PlayingFilePath);

    /// <summary>
    /// Called after the settings screen saves: re-reads the player-related
    /// settings so the caption line, seek-glyph step counts, and Previous-button
    /// restart threshold take effect on the current track immediately.
    /// </summary>
    public void OnSettingsChanged()
    {
        RefreshCaptionOptions();
        RefreshArtworkOptions();
        OnPropertyChanged(nameof(SeekBackSeconds));
        OnPropertyChanged(nameof(SeekForwardSeconds));
        UpdatePreviousNavState();
        // Honour a mid-track stop-after-current toggle: re-evaluate the gapless
        // queue (clears it when stop-after-current is now on, re-queues otherwise).
        QueueNextTrack();
    }

    /// <summary>
    /// Builds caption line 1. A single piece ref uses its normal summary
    /// (without composer). With two or more leaf pieces that share hierarchy,
    /// the first piece shows its full "Piece &gt; Subpiece &gt; … &gt; Leaf"
    /// path; each subsequent piece shows only the portion of its path that
    /// changed from the previous one (its own leaf, plus any higher levels that
    /// differ). Hierarchy levels are always joined with " &gt; "; the pieces
    /// themselves are joined with " - ". For example four refs under Turandot /
    /// Act II / Scene 1-2 collapse to
    /// "Turandot &gt; Act II &gt; Scene 1 &gt; Ho una casa nell'Honan -
    /// O mondo, o mondo - Scene 2 &gt; Introduction - Gravi, enormi ed imponenti".
    /// Version / marker decorations are omitted in the collapsed multi-piece
    /// view to keep the line readable.
    /// </summary>
    internal static string BuildCaptionTitle(
        IReadOnlyList<TrackPieceRef>? pieceRefs, string? fallbackDescription)
    {
        if (pieceRefs is null or { Count: 0 })
            return fallbackDescription ?? "(no description)";
        if (pieceRefs.Count == 1)
            return pieceRefs[0].DisplaySummaryWithoutComposer;

        var paths = pieceRefs.Select(LeafPath).ToList();
        var parts = new List<string>(paths.Count) { string.Join(" > ", paths[0]) };
        for (int i = 1; i < paths.Count; i++)
        {
            int common = CommonPrefixLength(paths[i], paths[i - 1]);
            var changed = paths[i].Skip(common).ToList();
            if (changed.Count == 0) changed.Add(paths[i][^1]); // identical-path guard
            parts.Add(string.Join(" > ", changed));
        }
        return string.Join(" - ", parts);
    }

    /// <summary>
    /// The ordered path components of a piece ref: [PieceTitle, …SubpiecePath],
    /// with the leaf last. A <see cref="TrackPieceRef.DisplayLabel"/> override
    /// collapses to a single component (it carries no shareable hierarchy).
    /// </summary>
    private static IReadOnlyList<string> LeafPath(TrackPieceRef r)
    {
        if (!string.IsNullOrWhiteSpace(r.DisplayLabel))
            return new[] { r.DisplayLabel! };
        var path = new List<string> { r.PieceTitle };
        if (r.SubpiecePath is { Count: > 0 })
            path.AddRange(r.SubpiecePath);
        return path;
    }

    private static int CommonPrefixLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int k = 0;
        int max = Math.Min(a.Count, b.Count);
        while (k < max && string.Equals(a[k], b[k], StringComparison.OrdinalIgnoreCase)) k++;
        return k;
    }

    /// <summary>
    /// Performer summary for the caption: the first two performers, each
    /// rendered as "Name (detail)" where detail is the role if set, else the
    /// instrument, plus a " +N more" suffix when others follow. Returns null
    /// when empty so it drops out of the joined detail line. The complete list
    /// is available via <see cref="FormatAllPerformers"/> (the hover tooltip).
    /// </summary>
    internal static string? FormatPerformers(List<AlbumPerformer>? performers)
    {
        if (performers is null or { Count: 0 }) return null;
        var shown = string.Join(", ", performers.Take(2).Select(FormatPerformer));
        var extra = performers.Count - 2;
        return extra > 0 ? $"{shown} +{extra} more" : shown;
    }

    /// <summary>Indent applied per piece-hierarchy level in the tooltip.</summary>
    private const string TooltipIndent = "    ";

    /// <summary>
    /// Builds the complete hover tooltip for the current track, one item per
    /// line, top to bottom:
    /// <list type="bullet">
    ///   <item>album title (omitted for loose tracks);</item>
    ///   <item>composer (repeated only when a later piece ref has a different one);</item>
    ///   <item>each piece ref's title then its subpiece path, progressively
    ///         indented one level deeper per step;</item>
    ///   <item>every performer with role and/or instrument in parentheses.</item>
    /// </list>
    /// Uncatalogued tracks fall back to the track description in place of the
    /// piece hierarchy. Returns null when there's nothing to show.
    /// </summary>
    internal static string? BuildTrackTooltip(
        string? albumTitle,
        IReadOnlyList<TrackPieceRef>? pieceRefs,
        string? fallbackDescription,
        IReadOnlyList<AlbumPerformer>? performers,
        Func<string, string?>? composerLifespan = null)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(albumTitle))
            lines.Add(albumTitle!);

        if (pieceRefs is { Count: > 0 })
        {
            string? lastComposer = null;
            foreach (var r in pieceRefs)
            {
                if (!string.IsNullOrWhiteSpace(r.Composer)
                    && !string.Equals(r.Composer, lastComposer, StringComparison.OrdinalIgnoreCase))
                {
                    var composerLine = r.Composer;
                    var span = composerLifespan?.Invoke(r.Composer);
                    if (!string.IsNullOrWhiteSpace(span))
                        composerLine += " " + span;
                    lines.Add(composerLine);
                    lastComposer = r.Composer;
                }

                var piece = r.PieceTitle;
                if (!string.IsNullOrWhiteSpace(r.VersionDescription))
                    piece += $" ({r.VersionDescription})";
                lines.Add(piece);

                if (r.SubpiecePath is { Count: > 0 })
                {
                    for (int i = 0; i < r.SubpiecePath.Count; i++)
                        lines.Add(Repeat(TooltipIndent, i + 1) + r.SubpiecePath[i]);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(fallbackDescription))
        {
            lines.Add(fallbackDescription!);
        }

        if (performers is { Count: > 0 })
            lines.AddRange(performers.Select(FormatPerformerDetailed));

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    private static string Repeat(string unit, int times) =>
        string.Concat(Enumerable.Repeat(unit, times));

    /// <summary>
    /// One performer for the tooltip as "Name (role, instrument)" — shows both
    /// role and instrument when both are present, just the one that's set
    /// otherwise, or the bare name when neither is.
    /// </summary>
    internal static string FormatPerformerDetailed(AlbumPerformer performer)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(performer.Role)) parts.Add(performer.Role!);
        if (!string.IsNullOrWhiteSpace(performer.Instrument)) parts.Add(performer.Instrument!);
        return parts.Count > 0 ? $"{performer.Name} ({string.Join(", ", parts)})" : performer.Name;
    }

    /// <summary>
    /// One performer as "Name (role-or-instrument)". The role takes precedence
    /// over the instrument; when neither is present just the name is shown.
    /// </summary>
    internal static string FormatPerformer(AlbumPerformer performer)
    {
        var detail = !string.IsNullOrWhiteSpace(performer.Role)
            ? performer.Role
            : performer.Instrument;
        return string.IsNullOrWhiteSpace(detail)
            ? performer.Name
            : $"{performer.Name} ({detail})";
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
        var target = _player.Position - TimeSpan.FromSeconds(SeekBackSeconds);
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
        var target = _player.Position + TimeSpan.FromSeconds(SeekForwardSeconds);
        _player.Seek(target);
    }

    /// <summary>
    /// Previous button: when the position is past the restart threshold,
    /// restarts the current track; otherwise skips to the previous track (when
    /// there is one). With no track loaded, no-op.
    /// </summary>
    [RelayCommand]
    private void PreviousTrack()
    {
        if (_player.Position.TotalSeconds > _settings.PreviousRestartThresholdSeconds)
        {
            _player.Seek(TimeSpan.Zero); // restart the current track
            return;
        }
        if (_currentAlbum is not null && HasPreviousTrack)
            TryPlayBackwardFrom(_currentIndex - 1);
    }

    [RelayCommand]
    private void NextTrack()
    {
        if (_currentAlbum is null) return;
        TryPlayAt(_currentIndex + 1, requireExact: false);
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
        // The Previous button enables once we pass the restart threshold.
        UpdatePreviousNavState();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        SliderValue = 0;
        // Auto-advance through the album. Tracks that fail to resolve are
        // skipped silently; on end-of-album the player just stops at the
        // last successful track's end position. The "Stop after current track"
        // setting (Settings screen) suppresses the auto-advance. Read live so
        // a settings change takes effect at the next track boundary without
        // needing to restart playback.
        if (_currentAlbum is null || _settings.StopAfterCurrentTrack) return;
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
        _player.StateChanged     -= OnStateChanged;
        _player.PositionChanged  -= OnPositionChanged;
        _player.DurationKnown    -= OnDurationKnown;
        _player.PlaybackEnded    -= OnPlaybackEnded;
        _player.TrackTransitioned -= OnTrackTransitioned;
    }
}
