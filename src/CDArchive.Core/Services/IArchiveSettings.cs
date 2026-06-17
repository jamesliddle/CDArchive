namespace CDArchive.Core.Services;

public interface IArchiveSettings
{
    string ArchiveRootPath { get; set; }
    string FfmpegPath { get; set; }
    int Mp3Bitrate { get; set; }

    /// <summary>
    /// Player preference for which audio format to play when both FLAC and MP3
    /// exist. The locator falls back to the other format if the preferred one
    /// is missing.
    /// </summary>
    PreferredAudioFormat PreferredAudioFormat { get; set; }

    /// <summary>
    /// Player output volume in [0.0, 1.0]. Persisted across sessions; passed
    /// through to the audio engine on every Load() so each track inherits the
    /// last-set level.
    /// </summary>
    float PlayerVolume { get; set; }

    /// <summary>
    /// When true, the player stops at the end of the current track instead of
    /// auto-advancing to the next track on the album. Persisted across
    /// sessions. Defaults false (auto-advance on). Read live by
    /// <c>PlayerViewModel</c> at end-of-track so a settings change takes effect
    /// on the next track boundary without restarting playback.
    /// </summary>
    bool StopAfterCurrentTrack { get; set; }

    /// <summary>
    /// When true, the player caption shows a third line with the full path and
    /// name of the audio file currently playing. Persisted across sessions.
    /// Defaults false. Useful as a diagnostic for confirming which on-disk file
    /// a track resolved to.
    /// </summary>
    bool ShowPlayingFilePath { get; set; }

    /// <summary>Seconds the player skips forward per seek-forward press (1–60). Defaults 10.</summary>
    int SeekForwardSeconds { get; set; }

    /// <summary>Seconds the player skips back per seek-back press (1–60). Defaults 10.</summary>
    int SeekBackwardSeconds { get; set; }

    /// <summary>
    /// How many seconds into the current track the position must be before the
    /// Previous button restarts the track instead of skipping to the previous
    /// track (0–60). Defaults 2. 0 means "always go to the previous track".
    /// </summary>
    int PreviousRestartThresholdSeconds { get; set; }

    // ── MusicBrainz import enrichment (slice 6) ──────────────────────────────
    // All MB-side settings default off (the EnableXxx master) so users opt in
    // once they've seen the workflow. The per-aspect Apply* defaults
    // initialise the review pane's per-row checkboxes; the user can override
    // any subset per-import.

    /// <summary>Master switch. When false the existing import path runs
    /// unchanged — no planner, no review pane. Defaults false.</summary>
    bool EnableMusicBrainzImportEnrichment { get; set; }

    /// <summary>How many MB candidates the planner surfaces per
    /// proposal. The review pane shows the top-N. Defaults 3.</summary>
    int MusicBrainzCandidatesPerProposal { get; set; }

    /// <summary>Initial state of the review pane's per-album "Metadata"
    /// checkbox (Label / CatalogueNumber / Barcode). Defaults true.</summary>
    bool ApplyMbAlbumMetadata { get; set; }

    /// <summary>Initial state of the review pane's per-album "Performers"
    /// checkbox. Defaults true.</summary>
    bool ApplyMbPerformerCredits { get; set; }

    /// <summary>Initial state of the review pane's per-album "Recording
    /// session" checkbox. Defaults true.</summary>
    bool ApplyMbRecordingSessions { get; set; }

    /// <summary>Initial state of the review pane's per-piece "Movement
    /// list" checkbox. Defaults true (user decision); the planner's
    /// MovementCountMismatch detection still auto-unchecks per-row when
    /// MB and iTunes disagree on count.</summary>
    bool ApplyMbCanonicalWorkStructure { get; set; }

    // ── Cover-art display (per surface) ──────────────────────────────────────
    // Each surface can show or hide cover-art thumbnails independently, with a
    // small / medium / large size. The pixel dimensions are a view concern.

    /// <summary>Show cover-art thumbnails in the Albums list. Defaults true.</summary>
    bool ShowAlbumListArtwork { get; set; }

    /// <summary>Show cover-art thumbnails in the Tracks list. Defaults false
    /// (the tracks list is dense; the user opts in).</summary>
    bool ShowTrackListArtwork { get; set; }

    /// <summary>Show the cover-art thumbnail in the player bar. Defaults true.</summary>
    bool ShowPlayerArtwork { get; set; }

    /// <summary>Thumbnail size in the Albums list. Defaults Medium.</summary>
    ArtworkSize AlbumListArtworkSize { get; set; }

    /// <summary>Thumbnail size in the Tracks list. Defaults Small.</summary>
    ArtworkSize TrackListArtworkSize { get; set; }

    /// <summary>Thumbnail size in the player bar. Defaults Medium.</summary>
    ArtworkSize PlayerArtworkSize { get; set; }

    void Save();

    /// <summary>
    /// Reads persisted settings from disk and applies them on top of the
    /// in-memory defaults. The constructor is I/O-free by design (see
    /// <see cref="ArchiveSettings"/> docstring + Rework H4); call this once
    /// at startup, on the UI thread, before any consumer reads property
    /// values. Failure modes (missing file, corrupt JSON, permission denied,
    /// AV file lock) are logged and swallowed — the defaults survive.
    /// </summary>
    void Initialize();
}
