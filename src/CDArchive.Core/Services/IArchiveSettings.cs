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
