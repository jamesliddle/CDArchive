using CDArchive.App.ViewModels;
using CDArchive.Core.Services;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="MusicBrainzReviewViewModel"/> and its three per-row
/// VMs (slice 5 of the MB integration). Cover:
///
/// <list type="bullet">
///   <item><b>LoadPlan</b> populates the row collections + warnings + derived
///         count properties.</item>
///   <item><b>Per-row defaults.</b> SelectedIndex = PreferredIndex; per-aspect
///         Apply checkboxes default ON; WorkRow's ApplyMovementList auto-
///         defaults OFF when the proposal carries MovementCountMismatch=true.</item>
///   <item><b>BuildChoices</b> respects the per-aspect checkboxes and skips
///         rows with no selected candidate.</item>
///   <item><b>Progress</b> reports via IProgress route into the
///         <see cref="MusicBrainzReviewViewModel.Progress"/> property.</item>
///   <item><b>Apply / Cancel / ApplyNone commands</b> set DialogResult.</item>
/// </list>
/// </summary>
public class MusicBrainzReviewViewModelTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static MbReleaseCandidate Release(string id, double conf) =>
        new(MbReleaseId: id, Title: id, Label: null, CatalogueNumber: null, Barcode: null,
            Date: null, Country: null, DiscCount: 1, TrackCount: 0,
            Tracks: Array.Empty<MbReleaseTrack>(),
            RecordingEvents: Array.Empty<MbReleaseEvent>(),
            Credits: Array.Empty<MbReleaseCredit>(),
            Confidence: conf);

    private static MbArtistSuggestion Artist(string id, double conf) =>
        new(MbArtistId: id, Name: id, SortName: id,
            BirthYear: null, DeathYear: null,
            BirthPlace: null, DeathPlace: null,
            Confidence: conf);

    private static MbWorkSuggestion Work(string id, double conf, int movementCount = 0)
    {
        var movements = new List<MbWorkMovement>();
        for (int i = 1; i <= movementCount; i++)
            movements.Add(new MbWorkMovement(i, $"Mov {i}", Tempo: null));
        return new MbWorkSuggestion(
            MbWorkId: id, Title: id, Catalogue: null, KeyTonality: null, KeyMode: null,
            Movements: movements, Confidence: conf);
    }

    private static AlbumEnrichmentProposal AlbumProposal(string key, params MbReleaseCandidate[] candidates)
        => new(ItunesAlbumKey: key, ItunesAlbumTitle: key,
               ItunesAlbumArtist: "", TrackCount: candidates.Length > 0 ? candidates[0].TrackCount : 0,
               Candidates: candidates, PreferredIndex: candidates.Length > 0 ? 0 : -1,
               ItunesTracks: Array.Empty<CDArchive.Core.Models.ItunesTrack>());

    private static ArtistEnrichmentProposal ArtistProposal(string name, params MbArtistSuggestion[] candidates)
        => new(ParsedComposerName: name,
               Candidates: candidates, PreferredIndex: candidates.Length > 0 ? 0 : -1);

    private static WorkEnrichmentProposal WorkProposal(
        string composer, string title, int expected, bool mismatch, params MbWorkSuggestion[] candidates)
        => new(ParsedComposerName: composer, ParsedPieceTitle: title,
               ExpectedMovementCount: expected,
               Candidates: candidates,
               PreferredIndex: candidates.Length > 0 ? 0 : -1,
               MovementCountMismatch: mismatch);

    // ── LoadPlan ─────────────────────────────────────────────────────────────

    [Fact]
    public void LoadPlan_PopulatesAllCollectionsAndCounts()
    {
        var vm = new MusicBrainzReviewViewModel();
        var plan = new EnrichmentPlan(
            Albums:   new[] { AlbumProposal("a", Release("r1", 0.9)) },
            Artists:  new[] { ArtistProposal("Beethoven", Artist("a1", 0.95)) },
            Works:    new[] { WorkProposal("Beethoven", "Sonata", 3, mismatch: false, Work("w1", 0.9, 3)) },
            Warnings: new[] { new EnrichmentWarning("Foo", "test message") });

        vm.LoadPlan(plan);

        Assert.Single(vm.AlbumRows);
        Assert.Single(vm.ArtistRows);
        Assert.Single(vm.WorkRows);
        Assert.Single(vm.Warnings);

        Assert.Equal(1, vm.AlbumsTotal);
        Assert.Equal(1, vm.AlbumsWithMatches);
        Assert.Equal(1, vm.ArtistsTotal);
        Assert.Equal(1, vm.ArtistsWithMatches);
        Assert.Equal(1, vm.WorksTotal);
        Assert.Equal(1, vm.WorksWithMatches);
        Assert.Equal(0, vm.WorksWithMismatch);
        Assert.True(vm.HasWarnings);
    }

    [Fact]
    public void LoadPlan_ReplacesPreviousContent_Idempotent()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            new[] { AlbumProposal("a") }, new[] { ArtistProposal("X") },
            Array.Empty<WorkEnrichmentProposal>(), Array.Empty<EnrichmentWarning>()));

        // Second call replaces, not appends.
        vm.LoadPlan(EnrichmentPlan.Empty);

        Assert.Empty(vm.AlbumRows);
        Assert.Empty(vm.ArtistRows);
    }

    // ── Per-row defaults ─────────────────────────────────────────────────────

    [Fact]
    public void AlbumRow_DefaultsToPreferredIndex_AndAllApplyFlagsTrue()
    {
        var proposal = AlbumProposal("k", Release("r1", 0.92), Release("r2", 0.51));
        var row = new AlbumRowViewModel(proposal);

        Assert.Equal(0, row.SelectedIndex);
        Assert.Equal("r1", row.SelectedCandidate!.MbReleaseId);
        Assert.True(row.ApplyMetadata);
        Assert.True(row.ApplyPerformers);
        Assert.True(row.ApplyRecordingSession);
        Assert.True(row.HasCandidates);
        Assert.False(row.LacksCandidates);
    }

    [Fact]
    public void AlbumRow_SelectedIndexChange_UpdatesSelectedCandidate()
    {
        var proposal = AlbumProposal("k", Release("r1", 0.92), Release("r2", 0.51));
        var row = new AlbumRowViewModel(proposal);

        row.SelectedIndex = 1;
        Assert.Equal("r2", row.SelectedCandidate!.MbReleaseId);
    }

    [Fact]
    public void ArtistRow_DefaultsToPreferredIndex_AndApplyTrue()
    {
        var proposal = ArtistProposal("X", Artist("a1", 0.99));
        var row = new ArtistRowViewModel(proposal);

        Assert.Equal(0, row.SelectedIndex);
        Assert.True(row.Apply);
        Assert.True(row.HasCandidates);
    }

    [Fact]
    public void WorkRow_MovementCountMatch_DefaultsApplyMovementListToTrue()
    {
        var proposal = WorkProposal("Beethoven", "Sonata", 3, mismatch: false,
            Work("w1", 0.9, 3));
        var row = new WorkRowViewModel(proposal);

        Assert.True(row.ApplyScalars);
        Assert.True(row.ApplyMovementList);
    }

    [Fact]
    public void WorkRow_MovementCountMismatch_AutoDefaultsApplyMovementListToFalse()
    {
        // The protective default: the user has to deliberately re-tick the
        // box after reviewing the side-by-side preview.
        var proposal = WorkProposal("Beethoven", "Goldberg", 30, mismatch: true,
            Work("w1", 0.9, 32));
        var row = new WorkRowViewModel(proposal);

        Assert.True(row.ApplyScalars);
        Assert.False(row.ApplyMovementList);
    }

    [Fact]
    public void EmptyCandidates_HasCandidatesFalse_LacksCandidatesTrue()
    {
        var proposal = AlbumProposal("empty");
        var row = new AlbumRowViewModel(proposal);

        Assert.False(row.HasCandidates);
        Assert.True(row.LacksCandidates);
        Assert.Equal(-1, row.SelectedIndex);
        Assert.Null(row.SelectedCandidate);
    }

    // ── BuildChoices ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildChoices_AppliesEveryRowWithCandidate_DefaultFlagsTrue()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  new[] { AlbumProposal("album-a|", Release("r1", 0.9)) },
            Artists: new[] { ArtistProposal("Beethoven", Artist("a1", 0.95)) },
            Works:   new[] { WorkProposal("Beethoven", "Sonata", 3, false, Work("w1", 0.9, 3)) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        var choices = vm.BuildChoices();

        Assert.Single(choices.AlbumsByKey);
        Assert.Single(choices.ArtistsByName);
        Assert.Single(choices.WorksByKey);

        var album = choices.AlbumsByKey["album-a|"];
        Assert.Equal("r1", album.Candidate.MbReleaseId);
        Assert.True(album.ApplyMetadata);
        Assert.True(album.ApplyPerformers);
        Assert.True(album.ApplyRecordingSession);

        var artist = choices.ArtistsByName["Beethoven"];
        Assert.Equal("a1", artist.Candidate.MbArtistId);
        Assert.True(artist.Apply);

        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Sonata");
        var work = choices.WorksByKey[workKey];
        Assert.True(work.ApplyScalars);
        Assert.True(work.ApplyMovementList);
    }

    [Fact]
    public void BuildChoices_SkipsRowsWithoutCandidate()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  new[] { AlbumProposal("empty") },
            Artists: new[] { ArtistProposal("NoMatch") },
            Works:   new[] { WorkProposal("X", "Y", 1, false) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        var choices = vm.BuildChoices();

        Assert.Empty(choices.AlbumsByKey);
        Assert.Empty(choices.ArtistsByName);
        Assert.Empty(choices.WorksByKey);
    }

    [Fact]
    public void BuildChoices_AlbumRowWithAllAspectsOff_Skipped()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums: new[] { AlbumProposal("k", Release("r1", 0.9)) },
            Artists: Array.Empty<ArtistEnrichmentProposal>(),
            Works:   Array.Empty<WorkEnrichmentProposal>(),
            Warnings: Array.Empty<EnrichmentWarning>()));

        var row = vm.AlbumRows[0];
        row.ApplyMetadata = row.ApplyPerformers = row.ApplyRecordingSession = false;

        var choices = vm.BuildChoices();
        Assert.Empty(choices.AlbumsByKey);
    }

    [Fact]
    public void BuildChoices_ArtistRowApplyFalse_Skipped()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  Array.Empty<AlbumEnrichmentProposal>(),
            Artists: new[] { ArtistProposal("X", Artist("a", 0.9)) },
            Works:   Array.Empty<WorkEnrichmentProposal>(),
            Warnings: Array.Empty<EnrichmentWarning>()));

        vm.ArtistRows[0].Apply = false;

        var choices = vm.BuildChoices();
        Assert.Empty(choices.ArtistsByName);
    }

    [Fact]
    public void BuildChoices_WorkRowWithBothAspectsOff_Skipped()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  Array.Empty<AlbumEnrichmentProposal>(),
            Artists: Array.Empty<ArtistEnrichmentProposal>(),
            Works:   new[] { WorkProposal("X", "Y", 1, false, Work("w", 0.9)) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        vm.WorkRows[0].ApplyScalars = false;
        vm.WorkRows[0].ApplyMovementList = false;

        var choices = vm.BuildChoices();
        Assert.Empty(choices.WorksByKey);
    }

    [Fact]
    public void BuildChoices_ReflectsUserChangedFlags()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  new[] { AlbumProposal("k", Release("r1", 0.9)) },
            Artists: Array.Empty<ArtistEnrichmentProposal>(),
            Works:   new[] { WorkProposal("X", "Y", 1, false, Work("w", 0.9, 3)) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        // User unticks Performers + Movement list specifically.
        vm.AlbumRows[0].ApplyPerformers = false;
        vm.WorkRows[0].ApplyMovementList = false;

        var choices = vm.BuildChoices();
        Assert.True(choices.AlbumsByKey["k"].ApplyMetadata);
        Assert.False(choices.AlbumsByKey["k"].ApplyPerformers);
        Assert.True(choices.AlbumsByKey["k"].ApplyRecordingSession);

        var workKey = EnrichmentChoices.BuildWorkKey("X", "Y");
        Assert.True(choices.WorksByKey[workKey].ApplyScalars);
        Assert.False(choices.WorksByKey[workKey].ApplyMovementList);
    }

    // ── Progress + commands ──────────────────────────────────────────────────

    [Fact]
    public void Report_UpdatesProgressProperty()
    {
        var vm = new MusicBrainzReviewViewModel();
        var report = new EnrichmentProgress(2, 5, 1, 3, 0, 0);

        ((IProgress<EnrichmentProgress>)vm).Report(report);

        Assert.NotNull(vm.Progress);
        Assert.Equal(2, vm.Progress!.AlbumsResolved);
        Assert.Equal(5, vm.Progress.AlbumsTotal);
    }

    [Fact]
    public void ApplyCommand_SetsDialogResultTrue()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.DialogResult);
    }

    [Fact]
    public void CancelCommand_SetsDialogResultFalse()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.CancelCommand.Execute(null);
        Assert.False(vm.DialogResult);
    }

    [Fact]
    public void ApplyNoneCommand_ClearsAllFlagsAndSetsDialogResultTrue()
    {
        var vm = new MusicBrainzReviewViewModel();
        vm.LoadPlan(new EnrichmentPlan(
            Albums:  new[] { AlbumProposal("k", Release("r1", 0.9)) },
            Artists: new[] { ArtistProposal("X", Artist("a", 0.9)) },
            Works:   new[] { WorkProposal("X", "Y", 1, false, Work("w", 0.9, 3)) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        vm.ApplyNoneCommand.Execute(null);

        Assert.True(vm.DialogResult);
        // Subsequent BuildChoices reflects the cleared flags: every row's
        // aspects are off, so no entries land in the choices.
        var choices = vm.BuildChoices();
        Assert.Empty(choices.AlbumsByKey);
        Assert.Empty(choices.ArtistsByName);
        Assert.Empty(choices.WorksByKey);
    }

    /// <summary>
    /// Scripted enricher for the album-row detail-fetch test. Records each
    /// GetReleaseByMbidAsync call and hands back a candidate with non-empty
    /// Tracks/Credits/RecordingEvents so HasDetail flips true.
    /// </summary>
    private sealed class FakeEnricher : IMusicBrainzImportEnricher
    {
        public List<string> ByMbidCalls { get; } = new();

        public Task<IReadOnlyList<MbReleaseCandidate>> SearchReleasesAsync(
            string albumTitle, string albumArtist,
            IReadOnlyList<TimeSpan> trackLengths, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MbReleaseCandidate>>(Array.Empty<MbReleaseCandidate>());

        public Task<MbReleaseCandidate?> GetReleaseByMbidAsync(string mbReleaseId, CancellationToken ct)
        {
            ByMbidCalls.Add(mbReleaseId);
            return Task.FromResult<MbReleaseCandidate?>(new MbReleaseCandidate(
                MbReleaseId: mbReleaseId, Title: "Detail Title",
                Label: "DG", CatalogueNumber: "111", Barcode: null,
                Date: "2020", Country: "DE", DiscCount: 1, TrackCount: 2,
                Tracks: new[]
                {
                    new MbReleaseTrack(1, 1, "A", null, null, null),
                    new MbReleaseTrack(1, 2, "B", null, null, null),
                },
                RecordingEvents: new[]
                {
                    new MbReleaseEvent("2018", "Hall", "City", "DE",
                        Array.Empty<string>(), Array.Empty<string>()),
                },
                Credits: new[]
                {
                    new MbReleaseCredit("Conductor", "conductor", null, null),
                },
                Confidence: 1.0, ArtistCredit: "Composer"));
        }

        public Task<IReadOnlyList<MbArtistSuggestion>> ResolveArtistAsync(string lastName, string? firstName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MbArtistSuggestion>>(Array.Empty<MbArtistSuggestion>());

        public Task<IReadOnlyList<MbWorkSuggestion>> ResolveWorkAsync(string composerName, string parsedPieceTitle, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MbWorkSuggestion>>(Array.Empty<MbWorkSuggestion>());
    }

    [Fact]
    public async Task AlbumRow_OnConstruction_FetchesDetail_PopulatesSelectedCandidateDetail()
    {
        // The row eagerly fires GetReleaseByMbidAsync for the preferred
        // candidate at construction. The detail lands on
        // SelectedCandidateDetail (NOT on the dropdown's Candidates list —
        // mutating that mid-binding makes WPF's ComboBox briefly clear its
        // collapsed display, which the user saw as the top match
        // "disappearing" a second after the row appeared). HasDetail and
        // EffectiveCandidateForApply both flip to reflect the hydrated form.
        var fake = new FakeEnricher();
        var proposal = AlbumProposal("k", Release("r1", 0.9));

        var row = new AlbumRowViewModel(proposal, defaults: null, enricher: fake, sync: null);

        // Wait briefly for the fire-and-forget continuation.
        for (int i = 0; i < 20 && row.IsLoadingDetail; i++)
            await Task.Delay(10);

        Assert.Single(fake.ByMbidCalls);
        Assert.Equal("r1", fake.ByMbidCalls[0]);

        // Detail landed on the separate observable property.
        Assert.True(row.HasDetail);
        Assert.NotNull(row.SelectedCandidateDetail);
        Assert.Equal(2, row.SelectedCandidateDetail!.Tracks.Count);
        Assert.Single(row.SelectedCandidateDetail.Credits);
        Assert.Single(row.SelectedCandidateDetail.RecordingEvents);

        // Dropdown items stay immutable — the search hit is still in
        // the Candidates list at the same index it was constructed at.
        Assert.Same(proposal.Candidates[0], row.SelectedCandidate);

        // EffectiveCandidateForApply prefers the detail-hydrated form so
        // the importer's apply phase gets the rich data.
        Assert.Same(row.SelectedCandidateDetail, row.EffectiveCandidateForApply);

        // IsLoadingDetail cleared.
        Assert.False(row.IsLoadingDetail);
    }

    [Fact]
    public async Task AlbumRow_NoEnricher_NoFetchAttempted()
    {
        // Defensive: when no enricher is wired (tests / pre-DI builds),
        // the row still constructs and behaves normally; HasDetail stays
        // false and no fetch is attempted.
        var proposal = AlbumProposal("k", Release("r1", 0.9));
        var row = new AlbumRowViewModel(proposal, defaults: null, enricher: null, sync: null);

        await Task.Delay(20);

        Assert.False(row.HasDetail);
        Assert.False(row.IsLoadingDetail);
    }

    [Fact]
    public async Task AttachPlanningTask_AppliesDefaults_OnUiThreadCompletion()
    {
        // Regression: pre-fix the caller wrapped planTask in an outer
        // ContinueWith that called LoadPlan(plan, defaults) on a
        // thread-pool worker. The ObservableCollection mutations threw
        // NotSupportedException, the continuation faulted silently, and
        // the inner UI-thread continuation skipped LoadPlan because
        // IsCompletedSuccessfully was false — the review pane stayed empty.
        // Fix: AttachPlanningTask itself accepts defaults and applies them
        // on the sync-context callback. This test pins both:
        //   (a) defaults are honoured (work row's ApplyMovementList=false
        //       even when the proposal isn't mismatched);
        //   (b) the rows actually get populated.
        var vm = new MusicBrainzReviewViewModel();
        var tcs = new TaskCompletionSource<EnrichmentPlan>();
        var cts = new CancellationTokenSource();
        var defaults = new EnrichmentReviewDefaults(
            ApplyAlbumMetadata: false,
            ApplyPerformers:    false,
            ApplyRecordingSession: false,
            ApplyMovementList:  false);

        vm.AttachPlanningTask(tcs.Task, cts, defaults);

        tcs.SetResult(new EnrichmentPlan(
            Albums:  new[] { AlbumProposal("k", Release("r1", 0.9)) },
            Artists: Array.Empty<ArtistEnrichmentProposal>(),
            Works:   new[] { WorkProposal("X", "Y", 3, mismatch: false, Work("w", 0.9, 3)) },
            Warnings: Array.Empty<EnrichmentWarning>()));

        for (int i = 0; i < 20 && vm.IsPlanning; i++)
            await Task.Delay(10);

        // (b) rows populated.
        Assert.Single(vm.AlbumRows);
        Assert.Single(vm.WorkRows);

        // (a) defaults honoured.
        Assert.False(vm.AlbumRows[0].ApplyMetadata);
        Assert.False(vm.AlbumRows[0].ApplyPerformers);
        Assert.False(vm.AlbumRows[0].ApplyRecordingSession);
        Assert.False(vm.WorkRows[0].ApplyMovementList);
    }

    [Fact]
    public async Task AttachPlanningTask_SetsIsPlanningWhileRunning_ClearsOnCompletion()
    {
        var vm = new MusicBrainzReviewViewModel();
        var tcs = new TaskCompletionSource<EnrichmentPlan>();
        var cts = new CancellationTokenSource();

        vm.AttachPlanningTask(tcs.Task, cts);

        Assert.True(vm.IsPlanning);

        // Complete the planning task — VM should pick that up and populate.
        tcs.SetResult(new EnrichmentPlan(
            new[] { AlbumProposal("k", Release("r1", 0.9)) },
            Array.Empty<ArtistEnrichmentProposal>(),
            Array.Empty<WorkEnrichmentProposal>(),
            Array.Empty<EnrichmentWarning>()));

        // ContinueWith scheduled on TaskScheduler.Default — yield a few
        // times until the continuation runs.
        for (int i = 0; i < 20 && vm.IsPlanning; i++)
            await Task.Delay(10);

        Assert.False(vm.IsPlanning);
        Assert.Single(vm.AlbumRows);
    }
}
