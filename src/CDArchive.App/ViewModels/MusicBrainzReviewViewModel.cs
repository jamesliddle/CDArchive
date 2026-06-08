using System.Collections.ObjectModel;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>MusicBrainzReviewWindow</c> (slice 5 of the MB
/// integration). Wraps a planner-produced <see cref="EnrichmentPlan"/> as
/// three ObservableCollections of per-row VMs the XAML binds to, plus the
/// live progress + cancellation surface for the in-flight planner case.
///
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item>Caller constructs the VM and may register it as the planner's
///         <see cref="IProgress{T}"/> sink via the
///         <see cref="AttachPlanningTask"/> helper.</item>
///   <item>The window opens with progress visible; when the planner's task
///         completes, <see cref="LoadPlan"/> populates the row collections.</item>
///   <item>User picks per-row choices, clicks Apply; the host calls
///         <see cref="BuildChoices"/> to collapse VM state into a frozen
///         <see cref="EnrichmentChoices"/> snapshot for
///         <see cref="ItunesImporter.Import"/>.</item>
/// </list>
///
/// <para>Progress marshalling follows the C2 pattern: <see cref="Report"/>
/// uses the captured <see cref="SynchronizationContext"/> (set at
/// construction) so the planner's pool-thread reports reach the UI thread
/// safely. Tests construct without a sync context — reports land inline.</para>
/// </summary>
public partial class MusicBrainzReviewViewModel : ObservableObject, IProgress<EnrichmentProgress>
{
    private readonly SynchronizationContext? _sync;
    private readonly IMusicBrainzImportEnricher? _enricher;
    private CancellationTokenSource? _cts;

    public ObservableCollection<AlbumRowViewModel>  AlbumRows  { get; } = new();
    public ObservableCollection<ArtistRowViewModel> ArtistRows { get; } = new();
    public ObservableCollection<WorkRowViewModel>   WorkRows   { get; } = new();
    public ObservableCollection<EnrichmentWarning>  Warnings   { get; } = new();

    /// <summary>True while the planner task is in-flight. The window's
    /// progress band binds Visibility to this; the Stop button binds its
    /// IsEnabled to this too.</summary>
    [ObservableProperty] private bool _isPlanning;

    /// <summary>Latest progress report from the planner. Null until the
    /// first <see cref="Report"/> call.</summary>
    [ObservableProperty] private EnrichmentProgress? _progress;

    /// <summary>Final dialog disposition — true on Apply, false on Cancel /
    /// Apply none. Caller reads this after <c>ShowDialog</c> returns.</summary>
    [ObservableProperty] private bool? _dialogResult;

    public MusicBrainzReviewViewModel(
        SynchronizationContext? sync = null,
        IMusicBrainzImportEnricher? enricher = null)
    {
        _sync = sync;
        _enricher = enricher;
    }

    /// <summary>
    /// Drive the VM from an in-flight planner task. The VM:
    /// <list type="bullet">
    ///   <item>Sets <see cref="IsPlanning"/> = true.</item>
    ///   <item>Awaits the task on a background continuation.</item>
    ///   <item>On completion, calls <see cref="LoadPlan"/> on the captured
    ///         <see cref="SynchronizationContext"/>.</item>
    ///   <item>The supplied <paramref name="cts"/> is held for the
    ///         <see cref="StopCommand"/> to cancel.</item>
    /// </list>
    /// <para>The optional <paramref name="defaults"/> propagates the user's
    /// settings-defined per-aspect Apply flags into the per-row VMs. Applied
    /// on the UI thread (the sync context's Post callback) — calling
    /// <see cref="LoadPlan"/> on any other thread throws
    /// <see cref="System.NotSupportedException"/> because of the bound
    /// <c>ObservableCollection</c> mutations.</para>
    /// </summary>
    public void AttachPlanningTask(
        Task<EnrichmentPlan> planTask,
        CancellationTokenSource cts,
        EnrichmentReviewDefaults? defaults = null)
    {
        _cts = cts;
        IsPlanning = true;

        // Don't await — fire-and-forget continuation so the window's
        // ShowDialog can return cleanly. Continuation lands on the UI
        // thread via the sync context.
        _ = planTask.ContinueWith(t =>
        {
            void Apply()
            {
                IsPlanning = false;
                if (t.IsCompletedSuccessfully)
                    LoadPlan(t.Result, defaults);
                // On faulted / cancelled: leave row collections empty,
                // user sees the empty review pane + can Cancel out. The
                // planner's partial-on-cancel contract (slice 5) means a
                // cancellation actually returns a populated plan with
                // a "Cancelled" warning, so this branch is rare.
            }
            if (_sync is null) Apply();
            else                _sync.Post(_ => Apply(), null);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Populate the row collections + warnings from an
    /// <see cref="EnrichmentPlan"/>. Idempotent — calling twice replaces
    /// the previous content.
    /// <para>Optional <paramref name="defaults"/> overrides the per-row
    /// initial checkbox states (e.g. wiring the user's settings-defined
    /// "default on/off" preferences for each aspect). When null, the
    /// per-row VMs apply their hardcoded defaults (all true, with the
    /// movement-count-mismatch protective auto-uncheck still applied).</para>
    /// </summary>
    public void LoadPlan(EnrichmentPlan plan, EnrichmentReviewDefaults? defaults = null)
    {
        AlbumRows.Clear();
        foreach (var p in plan.Albums)
            AlbumRows.Add(new AlbumRowViewModel(p, defaults, _enricher, _sync));

        ArtistRows.Clear();
        foreach (var a in plan.Artists)
            ArtistRows.Add(new ArtistRowViewModel(a));

        WorkRows.Clear();
        foreach (var w in plan.Works)
            WorkRows.Add(new WorkRowViewModel(w, defaults));

        Warnings.Clear();
        foreach (var w in plan.Warnings)
            Warnings.Add(w);

        // Refresh derived display properties (the section headers read
        // counts from these collections).
        OnPropertyChanged(nameof(AlbumsTotal));
        OnPropertyChanged(nameof(AlbumsWithMatches));
        OnPropertyChanged(nameof(ArtistsTotal));
        OnPropertyChanged(nameof(ArtistsWithMatches));
        OnPropertyChanged(nameof(WorksTotal));
        OnPropertyChanged(nameof(WorksWithMatches));
        OnPropertyChanged(nameof(WorksWithMismatch));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(AllAlbumsLackCandidates));
    }

    // ── Section header counts ────────────────────────────────────────────────

    public int AlbumsTotal          => AlbumRows.Count;
    public int AlbumsWithMatches    => AlbumRows.Count(r => r.HasCandidates);
    public int ArtistsTotal         => ArtistRows.Count;
    public int ArtistsWithMatches   => ArtistRows.Count(r => r.HasCandidates);
    public int WorksTotal           => WorkRows.Count;
    public int WorksWithMatches     => WorkRows.Count(r => r.HasCandidates);
    public int WorksWithMismatch    => WorkRows.Count(r => r.Proposal.MovementCountMismatch);

    /// <summary>Drives the Warnings panel's Visibility — the framework
    /// BooleanToVisibilityConverter doesn't accept ints, so we expose this
    /// boolean derived from <see cref="Warnings"/>.Count.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>True when at least one album proposal exists AND every
    /// one of them has zero MB candidates. Drives a top-of-page banner
    /// pointing the user at the rolling log so they can investigate
    /// query / connectivity issues. False when the album list is empty
    /// (nothing to enrich) or any album has a hit.</summary>
    public bool AllAlbumsLackCandidates =>
        AlbumRows.Count > 0 && AlbumRows.All(r => !r.HasCandidates);

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Apply the user's current per-row state. Sets
    /// <see cref="DialogResult"/> = true; the window's host reads it and
    /// calls <see cref="BuildChoices"/>.</summary>
    [RelayCommand]
    private void Apply()
    {
        DialogResult = true;
    }

    /// <summary>Skip enrichment entirely — same as Cancel from the
    /// importer's POV (no choices applied).</summary>
    [RelayCommand]
    private void ApplyNone()
    {
        // Clear so BuildChoices returns Empty even if the caller asks
        // after this branch.
        foreach (var r in AlbumRows)  r.ApplyMetadata = r.ApplyPerformers = r.ApplyRecordingSession = false;
        foreach (var r in ArtistRows) r.Apply = false;
        foreach (var r in WorkRows)   r.ApplyScalars = r.ApplyMovementList = false;
        DialogResult = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        // If the planner is still running, also cancel it.
        if (IsPlanning) _cts?.Cancel();
        DialogResult = false;
    }

    /// <summary>Cancel the in-flight planner task. The planner's
    /// partial-on-cancel contract means whatever resolved so far becomes
    /// the review pane's content.</summary>
    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
    }

    // ── IProgress<EnrichmentProgress> ────────────────────────────────────────

    public void Report(EnrichmentProgress value)
    {
        void Apply() => Progress = value;
        if (_sync is null) Apply();
        else                _sync.Post(_ => Apply(), null);
    }

    // ── Build choices ────────────────────────────────────────────────────────

    /// <summary>
    /// Collapse the current VM state into an <see cref="EnrichmentChoices"/>
    /// snapshot for <see cref="ItunesImporter.Import"/>'s apply phase.
    /// Rows whose selected candidate is null, OR whose every per-aspect
    /// Apply flag is false, are skipped — they contribute no entry.
    /// </summary>
    public EnrichmentChoices BuildChoices()
    {
        var albums  = new Dictionary<string, AppliedAlbumEnrichment>(StringComparer.Ordinal);
        var artists = new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase);
        var works   = new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in AlbumRows)
        {
            var chosen = row.EffectiveCandidateForApply;
            if (chosen is null) continue;
            // Even when every per-aspect flag is false we still record the
            // entry — the importer always stamps the MBID linkage (the user
            // explicitly picked this candidate). Empty-row trimming
            // happens at the caller's discretion.
            if (!row.ApplyMetadata && !row.ApplyPerformers && !row.ApplyRecordingSession)
                continue;
            albums[row.Proposal.ItunesAlbumKey] = new AppliedAlbumEnrichment(
                Candidate:             chosen,
                ApplyMetadata:         row.ApplyMetadata,
                ApplyPerformers:       row.ApplyPerformers,
                ApplyRecordingSession: row.ApplyRecordingSession);
        }

        foreach (var row in ArtistRows)
        {
            if (row.SelectedCandidate is null || !row.Apply) continue;
            artists[row.Proposal.ParsedComposerName] = new AppliedArtistEnrichment(
                Candidate: row.SelectedCandidate,
                Apply:     row.Apply);
        }

        foreach (var row in WorkRows)
        {
            if (row.SelectedCandidate is null) continue;
            if (!row.ApplyScalars && !row.ApplyMovementList) continue;
            var key = EnrichmentChoices.BuildWorkKey(
                row.Proposal.ParsedComposerName,
                row.Proposal.ParsedPieceTitle);
            works[key] = new AppliedWorkEnrichment(
                Candidate:         row.SelectedCandidate,
                ApplyScalars:      row.ApplyScalars,
                ApplyMovementList: row.ApplyMovementList);
        }

        return new EnrichmentChoices(albums, artists, works);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-row view-models
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wraps an <see cref="AlbumEnrichmentProposal"/> with the user's per-row
/// state: selected candidate index + three per-aspect Apply flags. Also
/// owns the on-demand detail fetch — when the user picks a candidate, the
/// row asynchronously calls <see cref="IMusicBrainzImportEnricher.GetReleaseByMbidAsync"/>
/// to populate Tracks / Credits / RecordingEvents and stores the result
/// in <see cref="SelectedCandidateDetail"/>. The dropdown items themselves
/// stay immutable (mutating the ItemsSource collection while a ComboBox
/// has a selection in the changed slot makes WPF briefly clear the
/// collapsed display).
/// </summary>
public partial class AlbumRowViewModel : ObservableObject
{
    public AlbumEnrichmentProposal Proposal { get; }

    /// <summary>Dropdown's ItemsSource. Constructed once from
    /// <c>Proposal.Candidates</c> and never mutated. Pre-fix this was an
    /// ObservableCollection that <see cref="LoadDetailAsync"/> rewrote in
    /// place so the importer's apply phase would see the detail-hydrated
    /// candidate — but WPF's ComboBox briefly clears its collapsed
    /// SelectedItem display on Replace, which the user saw as the top
    /// match "disappearing" a second after the row appeared. Now we keep
    /// the dropdown items immutable and store the detail-hydrated
    /// candidate on <see cref="SelectedCandidateDetail"/> instead.</summary>
    public IReadOnlyList<MbReleaseCandidate> Candidates => Proposal.Candidates;

    private readonly IMusicBrainzImportEnricher? _enricher;
    private readonly SynchronizationContext? _sync;
    private CancellationTokenSource? _detailCts;

    /// <summary>Per-MBID detail cache. Repeated selections of the same
    /// candidate skip the MB round-trip entirely.</summary>
    private readonly Dictionary<string, MbReleaseCandidate> _detailByMbid =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private int _selectedIndex;

    [ObservableProperty] private bool _applyMetadata         = true;
    [ObservableProperty] private bool _applyPerformers       = true;
    [ObservableProperty] private bool _applyRecordingSession = true;

    [ObservableProperty] private bool _isLoadingDetail;

    /// <summary>The detail-hydrated form of the currently-selected
    /// candidate (Tracks, Credits, RecordingEvents populated), or null
    /// while the detail fetch is in flight or hasn't run for this
    /// selection. The preview pane binds to this for the detail listings.
    /// <see cref="EffectiveCandidateForApply"/> is what the importer's
    /// apply phase consumes.</summary>
    [ObservableProperty] private MbReleaseCandidate? _selectedCandidateDetail;

    public bool HasCandidates   => Candidates.Count > 0;
    public bool LacksCandidates => Candidates.Count == 0;

    public MbReleaseCandidate? SelectedCandidate =>
        SelectedIndex >= 0 && SelectedIndex < Candidates.Count
            ? Candidates[SelectedIndex]
            : null;

    /// <summary>The candidate the importer should apply. Prefers the
    /// detail-hydrated form when available so applied data includes MB's
    /// credits + recording events; falls back to the search hit when the
    /// detail fetch hasn't completed (or never ran in test paths).</summary>
    public MbReleaseCandidate? EffectiveCandidateForApply =>
        SelectedCandidateDetail ?? SelectedCandidate;

    /// <summary>True when <see cref="SelectedCandidateDetail"/> carries
    /// tracks / credits / recording events. Drives the preview pane's
    /// Visibility.</summary>
    public bool HasDetail
    {
        get
        {
            var c = SelectedCandidateDetail;
            if (c is null) return false;
            return c.Tracks.Count > 0
                || c.Credits.Count > 0
                || c.RecordingEvents.Count > 0;
        }
    }

    public AlbumRowViewModel(
        AlbumEnrichmentProposal proposal,
        EnrichmentReviewDefaults? defaults = null,
        IMusicBrainzImportEnricher? enricher = null,
        SynchronizationContext? sync = null)
    {
        Proposal = proposal;
        _enricher = enricher;
        _sync = sync;
        _selectedIndex = proposal.PreferredIndex;
        if (defaults is not null)
        {
            _applyMetadata         = defaults.ApplyAlbumMetadata;
            _applyPerformers       = defaults.ApplyPerformers;
            _applyRecordingSession = defaults.ApplyRecordingSession;
        }

        // Kick off the initial detail fetch for the preferred candidate so
        // the preview pane is populated by the time the user looks.
        if (_selectedIndex >= 0 && _enricher is not null)
            _ = LoadDetailAsync();
    }

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedCandidate));
        OnPropertyChanged(nameof(EffectiveCandidateForApply));
        // Drop the detail until the fetch for the new selection lands —
        // otherwise the preview pane briefly shows the previous selection's
        // detail attached to the new selection's MBID.
        if (_enricher is not null)
        {
            SelectedCandidateDetail = LookupCachedDetail();
            _ = LoadDetailAsync();
        }
    }

    partial void OnSelectedCandidateDetailChanged(MbReleaseCandidate? value)
    {
        OnPropertyChanged(nameof(EffectiveCandidateForApply));
        OnPropertyChanged(nameof(HasDetail));
    }

    private MbReleaseCandidate? LookupCachedDetail()
    {
        var current = SelectedCandidate;
        if (current is null) return null;
        return _detailByMbid.TryGetValue(current.MbReleaseId, out var cached)
            ? cached : null;
    }

    /// <summary>
    /// Fetch the currently-selected candidate's full release detail and
    /// publish it on <see cref="SelectedCandidateDetail"/>. Per-MBID
    /// cache short-circuits repeated selections; the enricher's URL
    /// cache covers concurrent rows that pick the same release.
    /// </summary>
    private async Task LoadDetailAsync()
    {
        if (_enricher is null) return;
        var current = SelectedCandidate;
        if (current is null) return;
        // Already hydrated → nothing to do.
        if (current.Tracks.Count > 0 || current.Credits.Count > 0) return;
        var mbid = current.MbReleaseId;
        if (string.IsNullOrWhiteSpace(mbid)) return;

        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        try
        {
            void Set(bool flag)
            {
                if (_sync is null) IsLoadingDetail = flag;
                else _sync.Post(_ => IsLoadingDetail = flag, null);
            }
            Set(true);

            var detail = await _enricher.GetReleaseByMbidAsync(mbid, cts.Token)
                                       .ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;
            if (detail is null) return;

            // Cache for any later re-selection of this MBID.
            _detailByMbid[mbid] = detail;

            void Publish()
            {
                // Only publish if the user hasn't picked a different
                // candidate while the fetch was in flight.
                if (SelectedCandidate?.MbReleaseId == mbid)
                    SelectedCandidateDetail = detail;
            }
            if (_sync is null) Publish();
            else _sync.Post(_ => Publish(), null);
        }
        catch (OperationCanceledException) { /* swallow — newer fetch in flight */ }
        catch
        {
            // Best-effort. Log here when we wire a logger into the row.
        }
        finally
        {
            void ClearFlag() { if (_detailCts == cts) IsLoadingDetail = false; }
            if (_sync is null) ClearFlag();
            else _sync.Post(_ => ClearFlag(), null);
        }
    }
}

/// <summary>
/// Wraps an <see cref="ArtistEnrichmentProposal"/>. Single per-row Apply
/// checkbox — the artist enrichment is all-or-nothing per composer.
/// </summary>
public partial class ArtistRowViewModel : ObservableObject
{
    public ArtistEnrichmentProposal Proposal { get; }

    [ObservableProperty] private int  _selectedIndex;
    [ObservableProperty] private bool _apply = true;

    public bool HasCandidates   => Proposal.Candidates.Count > 0;
    /// <summary>Inverse of <see cref="HasCandidates"/>, exposed so the XAML
    /// can bind the "no candidates" empty-state TextBlock's Visibility
    /// (WPF's default BooleanToVisibilityConverter doesn't support an
    /// Inverse ConverterParameter).</summary>
    public bool LacksCandidates => Proposal.Candidates.Count == 0;

    public MbArtistSuggestion? SelectedCandidate =>
        SelectedIndex >= 0 && SelectedIndex < Proposal.Candidates.Count
            ? Proposal.Candidates[SelectedIndex]
            : null;

    public ArtistRowViewModel(ArtistEnrichmentProposal proposal)
    {
        Proposal = proposal;
        _selectedIndex = proposal.PreferredIndex;
    }

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(SelectedCandidate));
}

/// <summary>
/// Wraps a <see cref="WorkEnrichmentProposal"/>. Two per-aspect Apply
/// flags: scalars (title / catalogue / key) and movement list.
/// <see cref="ApplyMovementList"/> auto-defaults to FALSE when the
/// proposal carries <see cref="WorkEnrichmentProposal.MovementCountMismatch"/>
/// — the user has to deliberately re-tick it.
/// </summary>
public partial class WorkRowViewModel : ObservableObject
{
    public WorkEnrichmentProposal Proposal { get; }

    [ObservableProperty] private int  _selectedIndex;
    [ObservableProperty] private bool _applyScalars      = true;
    [ObservableProperty] private bool _applyMovementList = true;

    public bool HasCandidates   => Proposal.Candidates.Count > 0;
    /// <summary>Inverse of <see cref="HasCandidates"/>, exposed so the XAML
    /// can bind the "no candidates" empty-state TextBlock's Visibility
    /// (WPF's default BooleanToVisibilityConverter doesn't support an
    /// Inverse ConverterParameter).</summary>
    public bool LacksCandidates => Proposal.Candidates.Count == 0;

    public MbWorkSuggestion? SelectedCandidate =>
        SelectedIndex >= 0 && SelectedIndex < Proposal.Candidates.Count
            ? Proposal.Candidates[SelectedIndex]
            : null;

    public WorkRowViewModel(WorkEnrichmentProposal proposal, EnrichmentReviewDefaults? defaults = null)
    {
        Proposal = proposal;
        _selectedIndex = proposal.PreferredIndex;

        if (defaults is not null)
            _applyScalars = defaults.ApplyAlbumMetadata; // scalars default tracks "metadata" toggle

        // Settings-default + mismatch override: mismatch always wins —
        // even if the user set "default on", the planner's
        // movement-count-mismatch flag still auto-unchecks. The user can
        // deliberately re-tick per-row after reviewing the side-by-side
        // preview.
        var settingsDefault = defaults?.ApplyMovementList ?? true;
        _applyMovementList = settingsDefault && !proposal.MovementCountMismatch;
    }

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(SelectedCandidate));
}

/// <summary>
/// Initial per-row checkbox state for <see cref="MusicBrainzReviewViewModel.LoadPlan"/>.
/// Threaded from <see cref="IArchiveSettings"/> so the user's preferences
/// drive defaults; per-row checkboxes still override in the review pane.
/// </summary>
public sealed record EnrichmentReviewDefaults(
    bool ApplyAlbumMetadata    = true,
    bool ApplyPerformers       = true,
    bool ApplyRecordingSession = true,
    bool ApplyMovementList     = true);
