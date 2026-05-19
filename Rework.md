# CDArchive Rework

Consolidated findings from a two-pass technical review of the codebase. Items are grouped by severity. Each one names the file/line, why it matters, and a suggested fix.

The architecture is fundamentally sound — the schema is well-designed, the load-mutate-save album path is genuinely good engineering, and the persistence test suite is thorough. The work below is the supporting cast: operational concerns (no logging, no CI, fragile settings I/O), error-handling discipline, code-organisation drift, and a handful of latent threading / external-integration hazards.

## Coverage

Reviewed in depth: persistence layer (`SqliteCanonDataService`, schema, migrations), DI/startup, audio player, iTunes integration, locator, FFmpeg conversion, MusicBrainz, tag parser, four largest VMs (Canon, Albums, Tracks, Player), `AlbumEditorWindow`, `PieceEditorWindow` (skimmed), `SeedDb` tool, key persistence tests.

Not yet reviewed in detail: the core domain model classes (`CanonPiece.cs`, `CanonAlbum.cs`, `CanonComposer.cs`, `CanonPickLists.cs`, `TrackPieceRef.cs`, `RecordingSession.cs`, `PieceAlbumHit.cs`, `AlbumPerformer.cs`, the smaller model DTOs); the tooling projects (`ItunesProbe`, `MigrateIsProvisional`); `Helpers/WpfExtensions.cs`; the smaller enums and value types in `Models/`. The 244 findings predominantly come from the *services and views* layer; the models layer was inferred from how it's used and is likely the next-highest-value remaining target.

Note: `MovementEditorWindow` and `VersionEditorWindow` referenced in CLAUDE.md do not exist as separate files — they were consolidated into `PieceEditorWindow` via the `PieceEditorMode` enum.

---

## Top 5 priorities (start here)

1. **Stop tests from mutating the LIVE production data directory.** `SaveOperations_DoNotTouchJsonFiles` and `Constructor_ResolvesViaDatabaseMarker_WhenComposersJsonAbsent` run against the real `data/` — a crash mid-test can leave the user's DB partially written or `Classical Canon composers.json` renamed and unrestored. Spin up a temp dir per test (the pattern `AlbumSaveInPlaceTests` uses) or gate behind a CI-off `[Trait("Category","LiveData")]`. (C15)
2. **Trim `SaveAlbumsAsync`'s full-graph load.** Loads every album's full graph on every save (`.Include(...).ThenInclude(...)` over Volumes, Sessions, Performers, Discs→Tracks→PieceRefs/Performers) just to save one edited album. Bearable at 99 albums; multi-second hang at the 3,000-CD target. Load only matched albums by ID + check orphans via a lightweight `SELECT Id FROM albums`. Combined with `AsSplitQuery()` (M15) should reduce save latency 10× at scale. (C11)
3. **Clean up `.gitignore` so the working tree stops being noisy.** Today's `git status` shows 20+ uncommitted `.bak` files, `msbuild.binlog`, `__pycache__/`, `data/*.db`, `data/*.json.bak.*`, `_wpftmp.csproj` artifacts. The DB file itself probably shouldn't be in git (binary, regenerable from JSON via the seeder); the JSON snapshots probably should. Add `*.bak*`, `*.binlog`, `*_wpftmp.csproj`, `__pycache__/`, `data/*.db*`, `data/*.bak.*` to `.gitignore`. (C7)
4. **Defer `NAudioPlayerService`'s `SynchronizationContext` capture.** Ctor-time capture is correct by accident — it works only because the DI container builds singletons on the UI thread today. A future background-thread resolve (startup warm-up, headless mode, test harness) silently breaks WPF binding marshalling. Pass a captured dispatcher explicitly or capture lazily on the first event registration. (C2)
5. **Make the migration table-recreate self-healing across crashes.** The `album_tracks_new` recreate dance commits in a transaction, but `PRAGMA foreign_keys=OFF` runs *outside* it and never gets restored if the helper no-ops on the next start. A commit-then-rename crash also leaves a stale `*_new` table forever. Add a startup pass that drops any orphan `*_new` tables and a `PRAGMA foreign_keys=ON` in the no-op branch. (C6)

The next tier (after those five) is the structural work: extract `AlbumEditorViewModel` and `PieceEditorViewModel`, split `SqliteCanonDataService`, dedupe the `SimpleDbContextFactory` boilerplate. Higher-effort; cap the ceiling on how fast future features land.

---

## Working through this document

This is a living backlog. The intended workflow is multiple focused passes over time:

1. **Pick the next finding(s)** in priority order:
   - If the Top-5 list still has open items, start there.
   - Otherwise: Critical → High → Medium → Low → Nit, numeric within each.
   - Group related findings — many entries cross-reference each other (e.g. C3 + C9 are both "bare catch blocks"; H35 + L29 share "no shared resource dictionary"). Bundle them in one retirement when natural.

2. **Address the finding.** Each entry includes a suggested fix; treat it as a starting point. The implementation may surface constraints not visible from the review (e.g. fixing C4 logging unlocks proper remediation of C3's bare-catch sites).

3. **Add a regression test where feasible.** H40 calls out that no Critical/High has a test today; landing a test alongside the fix prevents drift. VM-layer fixes likely need H39 done first (create a `CDArchive.App.Tests` project).

4. **Verify.** Build + tests pass; if the finding has a behavioural observable (e.g. C13's atomic write), exercise it manually too.

5. **Retire the finding.** Move the entry from its severity section to the `✅ Retired` section at the bottom, with a one-line note: `[YYYY-MM-DD] <commit-hash> — <brief description of fix>`. Update the summary table counts.

6. **Re-evaluate the Top-5 list.** As the top items retire, promote next-most-impactful items.

7. **Cross-link new gotchas in `CLAUDE.md`** under "Lessons Learned" if the fix surfaced a non-obvious trap — same problem → solution → corollary pattern as the existing entries.

**Deferring instead of retiring**: if a finding is intentionally not being addressed (architectural cost too high, requires a feature decision the user isn't ready to make), mark it `🟡 Deferred — <reason>` in place. Don't delete; the rationale matters for the next reviewer.

**The summary table tracks open counts only.** Retired findings stay visible for historical context and audit trail but don't count toward the open totals.

---

## Summary

| Severity | Count |
|---|---|
| 🔴 Critical | 5 |
| 🟠 High | 45 |
| 🟡 Medium | 85 |
| 🟢 Low | 46 |
| ⚪ Nit | 48 |
| **Total** | **229** |

---

## 🔴 Critical

### C2. Singleton `NAudioPlayerService` captures `SynchronizationContext` in its constructor
[NAudioPlayerService.cs:54-60](src/CDArchive.Core/Services/NAudioPlayerService.cs:54) — `_sync = SynchronizationContext.Current` runs whenever the DI container first resolves the singleton. Today that's `App.OnStartup` on the UI thread, correct by accident. If anyone ever pre-resolves the service from a background thread (a startup hook, a background warm-up, a future test/headless mode) the captured context becomes null/wrong and every event fires off the UI thread, corrupting WPF bindings silently. Pass a captured `Dispatcher` or `SynchronizationContext` explicitly, or capture lazily on the first event registration from the UI thread.

### C6. Migration table-recreate has no cleanup or crash recovery
[SqliteCanonDataService.cs:257-327](src/CDArchive.Core/Services/SqliteCanonDataService.cs:257) — the `album_tracks_new` recreate dance is wrapped in a transaction (good), but `PRAGMA foreign_keys=OFF` runs *outside* the transaction. If the helper no-ops next startup (column already nullable), nothing turns FKs back on. Also if the transaction commits but `RENAME` partially happened (extremely rare on disk-full), a stale `*_new` table could linger forever. Add: a) cleanup of `*_new` tables at startup, b) `PRAGMA foreign_keys=ON` in the no-op branch.

### C7. `.gitignore` is missing the daily mess; DB file is committable
The repo currently has 20+ `.bak` files, `msbuild.binlog`, `__pycache__/`, `data/*.db`, `data/*.json.bak.*`, and `_wpftmp.csproj` artifacts in `git status`. `.gitignore` covers `bin/`, `obj/`, `.vs/`, `.idea/`, but not the rest. The DB file itself probably shouldn't be in git (binary, regenerable from JSON via seeder); the JSON snapshots probably should be (textual source). Add `*.bak`, `*.binlog`, `*_wpftmp.csproj`, `__pycache__/`, `data/*.db*`, `data/*.bak.*` to `.gitignore`; keep one explicit seed file if you want a starting state in-tree.

### C11. `SaveAlbumsAsync` loads the entire `albums` graph on every single album save
[SqliteCanonDataService.cs:2024-2031](src/CDArchive.Core/Services/SqliteCanonDataService.cs:2024) eager-loads every album's full graph (`.Include(...).ThenInclude(...)` over Volumes, Sessions, Performers, Discs→Tracks→PieceRefs/Performers) just to save one edited album. With 99 albums and ~2,200 tracks today the cost is bearable; at the target of 3,000+ CDs (≈50k tracks) this is a multi-second hang and a sizeable RAM spike per Save click. Load only matched albums by ID (or by IdentityKey via WHERE), and check orphans via a lightweight `SELECT Id FROM albums`. Combined with `AsSplitQuery()` (M15), should reduce save latency by 10× at scale.

### C15. Two tests mutate the LIVE production data directory — `dotnet test` corrupts user data
- [SqliteRoundTripTests.cs:159-194 `SaveOperations_DoNotTouchJsonFiles`](tests/CDArchive.Core.Tests/SqliteRoundTripTests.cs:159) — calls `FindDataDirectory()` (which locates the *real* `data/ClassicalCanon.db`) and then executes `SaveComposersAsync(...)`, `SavePiecesAsync(...)`, `SavePickListsAsync(...)`, `SaveAlbumsAsync(...)` directly against it. Even though it's a load-then-save round-trip "should be a no-op", each save runs a full transaction, updates row mtimes, and re-computes the load-mutate-save merge. Two test runs racing (xUnit's default parallelisation) hit SQLite file-lock contention and may leave a `*.db-journal` sidecar. A crash mid-test leaves the user's DB partially written.
- [CanonDataServiceTests.cs:54-77 `Constructor_ResolvesViaDatabaseMarker_WhenComposersJsonAbsent`](tests/CDArchive.Core.Tests/CanonDataServiceTests.cs:54) — **moves `Classical Canon composers.json` aside** to `.test-resolver-bak`, runs the test, then moves it back in a `finally`. If the test crashes between the Move and the restore (or if the process is killed), the user's primary JSON snapshot is renamed and the next app launch fails.

Tests must operate on a *copy* of the data, not the source of truth. Either:
- Spin up a temp dir with a copied DB before each test (the pattern `AlbumSaveInPlaceTests` correctly uses for fresh DBs).
- Or, if integration against the live data is genuinely needed, mark with `[Trait("Category","LiveData")]` + a `[SkippableFact]` gate that's off in CI by default and warns when run locally.

---

## 🟠 High

### H1. `SqliteCanonDataService` is a 3,072-line god class
One file owns: schema migrations, load operations for 5 subsystems, save operations for 5 subsystems, the load-mutate-save merge for albums, all row↔model mapping, JSON column (de)serialization, identity tracking. Reading the file requires holding the whole architecture in your head. Split by subsystem (`Composers/`, `Pieces/`, `Albums/`, `Migrations/`, `Mapping/`), then split Save and Load into separate partial classes per subsystem.

### H2. `CanonView.xaml.cs` is 1,436 lines of code-behind doing VM/service work
Owns: sort state, context-menu state, expansion state across 3 tree levels, the tree-rebuild orchestrator, provisional filter routing, suppression flags. Approve/Reject handlers reach into the VM, mutate observable collections, call `SaveAllAsync`, overwrite status messages. CLAUDE.md flags one symptom of this; the file is full of similar foot-guns. Extract expansion state → service, sort/filter UI state → into VM, Approve/Reject handlers → VM RelayCommands via `CommandParameter`.

### H3. Multiple VMs call `MessageBox.Show` directly + use WPF file dialogs in VMs
- [AlbumsViewModel.cs:137-139](src/CDArchive.App/ViewModels/AlbumsViewModel.cs:137) — Reject confirmation.
- [ItunesImportViewModel.cs:109-111](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:109) — "Nothing to import".
- [ItunesImportViewModel.cs:156-157](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:156) — Import error (dumps full stack trace to user).
- [ImportExportViewModel.cs:208-212](src/CDArchive.App/ViewModels/ImportExportViewModel.cs:208) — Restore confirmation.
- [ImportExportViewModel.cs:280-302](src/CDArchive.App/ViewModels/ImportExportViewModel.cs:280) — `SaveFileDialog` / `OpenFileDialog` directly from VM helpers.

VMs shouldn't reference `System.Windows` or `Microsoft.Win32` — blocks headless unit-testing and couples Core MVVM logic to WPF. The good counterexample is [SettingsViewModel.cs:30-31, :62-71](src/CDArchive.App/ViewModels/SettingsViewModel.cs:30) which raises `BrowseArchivePathRequested` / `BrowseFfmpegPathRequested` events for the View to handle the dialog — that's the right pattern.

Introduce a minimal `IDialogService { Task<bool> ConfirmAsync(...); Task ShowErrorAsync(...); }` and an `IFileDialogService { string? PickSave(...); string? PickOpen(...); }` and inject both.

### H4. `ArchiveSettings` — synchronous I/O in constructor, non-atomic writes, narrow exception filter
[ArchiveSettings.cs:19-65](src/CDArchive.Core/Services/ArchiveSettings.cs:19):
- Reads JSON in the ctor → blocks DI container build, slow/locked filesystem freezes startup.
- `File.WriteAllText` is non-atomic → crash mid-write leaves a truncated `settings.json` and the user loses their archive root, ffmpeg path, etc.
- `catch (JsonException)` only — `IOException`, `UnauthorizedAccessException`, etc. all crash the app.

Fix: write to `.tmp` then `File.Move(tmp, real, overwrite: true)`. Move the load out of ctor into `InitializeAsync`. Widen the catch.

### H5. `ItunesLibraryReference.LoadAllTracksAsync` does not use the `_cache`
[ItunesLibraryReference.cs:34-100](src/CDArchive.Core/Services/ItunesLibraryReference.cs:34) — `LoadAllTracksAsync` parses the entire iTunes Music Library XML on every call. The `Lazy<Task<LibraryCache>>` field is only used by lookup methods. Every time the iTunes Import view loads, the XML (potentially hundreds of MB) re-parses. Cache the parsed track list too, or add a "Refresh from iTunes" button.

### H6. iTunes filter hardcodes `"CD%20archive"` substring
[ItunesLibraryReference.cs:184](src/CDArchive.Core/Services/ItunesLibraryReference.cs:184) — embedded in cache-building code with no setting to change it. Any other archive path silently produces an empty cache. Lift to `IArchiveSettings.ItunesLibraryFilter` or compute from `ArchiveRootPath`.

### H7. `PieceReferenceIndex.Current` static singleton + concurrent rebuilds
[PieceReferenceIndex.cs:26-28](src/CDArchive.Core/Services/PieceReferenceIndex.cs:26) — `Current` is set in the ctor AND in every `Rebuild`/`RebuildContainers`/`BuildResolver` call. Comments note that callers sometimes construct throwaway resolvers (seeder, ItunesImporter pre-fix, `SaveAlbumsAsync` at [:2011](src/CDArchive.Core/Services/SqliteCanonDataService.cs:2011), `SaveLooseTracksAsync` at [:2193](src/CDArchive.Core/Services/SqliteCanonDataService.cs:2193)) which steal the static accessor.

Worse: `BuildResolver` does NOT reclaim `Current` — only `Rebuild`/`RebuildContainers` do. So a throwaway resolver constructed and BuildResolver'd in the save path leaves `Current` pointing at an empty-hits index. Every subsequent badge-converter read during/after that save window returns 0 for everything until a real Rebuild is triggered. Concrete symptom: badges flicker to zero mid-save and recover on the next reload.

Mark throwaway sites with a separate ctor (`internal PieceReferenceIndex(bool registerAsCurrent)`) or have `BuildResolver` reclaim. Better still: kill the static accessor and pass the index into the converter via `MarkupExtension` or a DI-aware locator.

### H8. `ArchiveAudioLocator` does synchronous filesystem I/O on every track resolution
[ArchiveAudioLocator.cs:112-131](src/CDArchive.Core/Services/ArchiveAudioLocator.cs:112) — every track played calls `Directory.Exists` on three folders, then `Directory.EnumerateFiles` with a glob. For 99 albums fine; for 3k+ slow when iterating across the Tracks view. Cache `Directory.Exists` + the file list per (album, disc) with invalidation on settings changes.

### H9. `CanonViewModel.LoadDataAsync` and friends re-load albums/loose-tracks 3+ times per session
Initial load, after save, after reject composer, after reject piece — each issues `LoadAlbumsAsync` + `LoadLooseTracksAsync` with full graph `.Include`s. With the singleton `AlbumsViewModel` holding the same data, you can pull from there. Document why fresh reads are needed — if it's just paranoia, that's expensive paranoia.

### H10. `CanonView`'s `Loaded += (_, _) => { }` dead handler
[CanonView.xaml.cs:72](src/CDArchive.App/Views/CanonView.xaml.cs:72) — empty Loaded handler in the ctor. The real `OnLoaded` is wired in XAML. Delete or consolidate.

### H11. `CanonViewModel.OnSelectedComposerChanged` is dead code with a misleading comment
[CanonViewModel.cs:79-82](src/CDArchive.App/ViewModels/CanonViewModel.cs:79) — partial method body is just a comment ("No longer need to filter pieces by composer"). Delete it; the source generator handles the no-op.

### H12. `AlbumEditorWindow.xaml.cs` reaches into `App.ServiceProvider` for `PlayerViewModel`
[AlbumEditorWindow.xaml.cs:437](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:437):
```csharp
var player = App.ServiceProvider.GetRequiredService<PlayerViewModel>();
```
Pure service-locator anti-pattern. The window is hand-instantiated with constructor args. The right fix is a `WindowFactory` (or `Func<CanonAlbum, ..., AlbumEditorWindow>`) registered in DI. Or move the play-track action into a VM (see H13).

### H13. Every editor window is pure code-behind with no VM
The big three are the worst offenders:
- [AlbumEditorWindow.xaml.cs](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs) — 685 lines, two constructors (single vs multi-edit) with 90%+ duplicate setup.
- [TrackEditorWindow.xaml.cs](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs) — 711 lines, THREE constructors (single album-bound / multi-edit / loose-track) with the same overlap.
- [PieceEditorWindow.xaml.cs](src/CDArchive.App/Views/PieceEditorWindow.xaml.cs) — 1,114 lines, two constructors (piece+subpiece / version).

And the small ones share the pattern at smaller scale (40–170 lines each): `ComposerEditorWindow`, `PerformerEditorWindow`, `SessionEditorWindow`, `RoleEditorWindow`, `ComposerCreditEditorWindow`, `InstrumentEntryEditorWindow`, `EnsembleEntryEditorWindow`, `VariantEditorWindow`, `MarkerEditorWindow`, `RolePickerWindow`, `PieceRefDetailsWindow` (339 lines), `PiecePickerWindow` (518 lines), `PieceAlbumsWindow`.

All own: details/list population, save logic with validation, field propagation, dialog ownership. Untestable without WPF. The pattern repeats: constructor → optional mode flags → branchy `PopulateXxx` and `OnOkClick`. Extract real VMs (at least for the three big editors). For the small ones, a shared `EditorDialogBase` with common patterns (NullIfEmpty, validation result, Result/Saved property) would consolidate the boilerplate.

### H14. `PieceEditorWindow.xaml.cs` is 1,114 lines and uses manual field copying between piece/version
[PieceEditorWindow.xaml.cs:145-181](src/CDArchive.App/Views/PieceEditorWindow.xaml.cs:145) — `VersionToPiece` and `CopyPieceToVersion` manually shuttle ~20 properties between `CanonPiece` and `CanonPieceVersion`. New field added to the model needs adding to both. Either share a base abstract class with `[ObservableProperty]`s, or generate the shuttle with a source generator. At minimum, add a reflection-based test asserting every shared property name flows both ways.

### H17. `SimpleDbContextFactory` boilerplate is duplicated
- [tools/CDArchive.Tools.SeedDb/Program.cs:323](tools/CDArchive.Tools.SeedDb/Program.cs:323)
- [tests/CDArchive.Core.Tests/AlbumSaveInPlaceTests.cs:30](tests/CDArchive.Core.Tests/AlbumSaveInPlaceTests.cs:30)
- Almost certainly in other test files.

A shared `tests/CDArchive.Core.Tests.Infrastructure/` helper would consolidate. Same for `BuildAlbum`, `SeedComposerAndPieceAsync`, the `Path.GetTempPath()` ceremony.

### H18. Editor windows dynamically mutate the visual tree for mode-switching
- [AlbumEditorWindow.xaml.cs:108-109](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:108) — `MainTabs.Items.Remove(PerformersTab); MainTabs.Items.Remove(SessionsTab);`
- [TrackEditorWindow.xaml.cs:138-142](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:138) — five imperative `Visibility = Collapsed` sets for loose-track mode.
- Both also `.Items.Add(new ComboBoxItem { Content = "Mixed", ... })` to inject a "Mixed" sentinel into combos.

None of these survive a re-show. The windows are single-use today so it works. `Visibility="Collapsed"` bindings to `IsMultiEdit` / `IsLooseTrack` would be cleaner and reversible.

### H19. Editor windows validate with `MessageBox.Show` from code-behind (inconsistently)
- [AlbumEditorWindow.xaml.cs:564-572](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:564) — Title required.
- [TrackEditorWindow.xaml.cs:422-428](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:422) — track number positive integer.
- [TrackEditorWindow.xaml.cs:559-565](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:559) — same check in multi-edit save.
- [PerformerEditorWindow.xaml.cs:27-32](src/CDArchive.App/Views/PerformerEditorWindow.xaml.cs:27) — Name required.
- [VariantEditorWindow.xaml.cs:30-34](src/CDArchive.App/Views/VariantEditorWindow.xaml.cs:30) — Description required.

Worse, validation across the editor family is **inconsistent**: `RoleEditorWindow:27`, `ComposerCreditEditorWindow:32`, `InstrumentEntryEditorWindow:28`, and `ComposerEditorWindow` (no validation at all on Name/SortName) all silently no-op on missing required fields — the user clicks OK, the dialog doesn't close, nothing visible happens. Pick a pattern and apply it everywhere: ideally extract VMs and use `CanSave` + bind to `OK.IsEnabled` so the button greys out before the click.

### H20. No test asserts row-ID behavior after a Title rename
[AlbumSaveInPlaceTests.cs](tests/CDArchive.Core.Tests/AlbumSaveInPlaceTests.cs) covers content-edit, add-track, remove-track, constraint-violation rollback, multi-album batch isolation. CLAUDE.md documents that "row IDs do churn on a Title rename" as accepted. But no test asserts *what happens* on rename, so a regression that breaks the orphan-delete-and-reinsert behaviour (turning a rename into a silent duplicate) would go undetected. Add a test that asserts: after rename, one album exists with the new title, disc count preserved.

### H21. `AlbumTrack.SessionIndex` stores a position, not an ID — latent data corruption on session reorder
[TrackEditorWindow.xaml.cs:457](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:457):
```csharp
target.SessionIndex = SessionBox.SelectedIndex < 0 ? null : SessionBox.SelectedIndex;
```
The track's session reference is an `int?` position into `CanonAlbum.Sessions`. If the user reorders or deletes sessions in `AlbumEditorWindow`'s Sessions tab, every `SessionIndex` on the album's tracks now points at the wrong session — silently. CLAUDE.md describes it as "FK into the parent album's sessions" but it's actually positional. This is a model-level design issue surfaced by the editor.

Fix at the model: give `RecordingSession` a stable identifier (`Id` / `Key`) and store that on tracks instead. Migration is non-trivial — every existing AlbumTrack.SessionIndex value needs translating to the new key.

### H22. `TrackEditorWindow` Cancel does not roll back edits or new-track additions
[TrackEditorWindow.xaml.cs:420-443](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:420) — `CommitCurrentTrack` mutates `_disc.Tracks[index]` in place AND appends new tracks (`_disc!.Tracks.Add(newTrack)`) during Next-navigation. There is no clone. The parent `AlbumEditorWindow` uses a JSON-clone for Cancel safety, but `TrackEditorWindow` mutates the parent's data directly. If a user clicks Next several times adding new tracks then Cancel, those tracks survive. If a user edits track 5 then clicks Next then Cancel, track 5's edits survive.

Fix: take the same JSON-clone-on-open approach as `AlbumEditorWindow`, or explicitly snapshot the disc's track list and restore on Cancel.

### H23. `TrackEditorWindow` multi-edit silently changes "no session" to "session 0"
[TrackEditorWindow.xaml.cs:285-287](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:285):
```csharp
if (distinctIndexes.Count == 1)
{
    SessionBox.SelectedIndex = _sessions.Count == 0 ? -1 : (distinctIndexes[0] ?? 0);
}
```
When all selected tracks have `SessionIndex = null` ("no session"), the combo defaults to position 0 (the first session). Clicking OK then writes index 0 to every track — silently mapping "no session" to "session 0". Should distinguish the `null` case in the UI (e.g. add a leading "(none)" item or set `SelectedIndex = -1`).

### H24. `ItunesImportViewModel` dedup hides legitimate tracks by ignoring Label/CatalogueNumber
[ItunesImportViewModel.cs:171-182](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:171) — the "already imported" index keys on `(album-title-lowercased, disc#, track#)`. Two genuinely different albums with the same title (Karajan's Beethoven 9 and Bernstein's Beethoven 9 are both "Symphony No. 9") collide. Importing the Bernstein after the Karajan: Bernstein's track 1 looks already-imported and is hidden, the user adds nothing, and the canon silently misses the Bernstein recording.

Fix: include `Label|CatalogueNumber` in the key when present, or key on the iTunes Persistent ID once that's threaded through (see M27).

### H25. `CataloguingService.FindMp3Folder` only returns the first disc folder
[CataloguingService.cs:184-201](src/CDArchive.Core/Services/CataloguingService.cs:184):
```csharp
foreach (var disc in Directory.GetDirectories(albumPath, "Disc *").OrderBy(d => d))
{
    var mp3InDisc = Path.Combine(disc, "MP3");
    if (Directory.Exists(mp3InDisc))
        return mp3InDisc;  // returns ONLY Disc 1
}
```
For multi-disc albums, only Disc 1's MP3 folder is returned. `ReadAlbumTagsAsync` therefore only processes Disc 1's files — the user's "Cataloguing" workflow silently skips discs 2..N. The owner has many multi-disc box sets in the catalogue; this is a real bug, not a corner case. Fix: collect MP3 folders from every disc and process them together.

### H26. `CataloguingService` composer cache collides on shared surnames
[CataloguingService.cs:79-100](src/CDArchive.Core/Services/CataloguingService.cs:79):
```csharp
var composerCache = new Dictionary<string, ComposerInfo?>(StringComparer.OrdinalIgnoreCase);
...
if (!string.IsNullOrEmpty(composerLast) && !composerCache.ContainsKey(composerLast))
{
    composerCache[composerLast] = await _reference.LookupComposerAsync(composerLast, ...);
}
```
The cache key is `composerLast` alone, but the lookup uses `(lastName, firstName)`. An album with both Johann Strauss II and Richard Strauss tracks: the second encountered uses the first's cached result. Same for J.S. Bach vs C.P.E. Bach, the Scarlattis, the Couperins. **Silently wrong composer data written to the tag.** Key the cache by `(lastName, firstName)`.

### H27. `CataloguingService` writes tags with no backup of the original
[CataloguingService.cs:272-299](src/CDArchive.Core/Services/CataloguingService.cs:272) — `WriteFileTag` overwrites the tag fields directly. If the user runs the formatting pipeline and is unhappy with the result (a bad MusicBrainz match, a misparsed work title), the original tags are gone. Recovery requires re-reading from CD or restoring from a separate file backup. At minimum, snapshot the original tag values to a sidecar JSON before the first write so the user has an undo path.

### H28. `ArchiveScannerService.ScanArchiveAsync` is async-in-name-only — runs synchronously on the calling thread
[ArchiveScannerService.cs:19-75](src/CDArchive.Core/Services/ArchiveScannerService.cs:19) — the method signature returns `Task<List<AlbumInfo>>` but the body does all the I/O synchronously and returns `Task.FromResult(albums)`. Walking a 3,000-CD archive on a slow drive (network share, USB, sleepy hard disk) hangs the UI thread for tens of seconds with no await yielding. By contrast, the sibling `ValidateArchiveAsync` *does* wrap its work in `Task.Run(...)` ([:131](src/CDArchive.Core/Services/ArchiveScannerService.cs:131)) — the asymmetry suggests one method was added later without copying the pattern. Either add `Task.Run` (cheap) or restructure to genuinely async I/O.

### H29. `ArchiveScannerService` orders disc folders lexicographically — breaks for 10+ disc sets
[ArchiveScannerService.cs:39](src/CDArchive.Core/Services/ArchiveScannerService.cs:39) — `discDirs.OrderBy(d => d)` string-sorts full paths. `Disc 10` sorts before `Disc 9` (`'1' < '9'`). Multi-disc box sets at 10+ discs — Beethoven complete symphonies, Mahler complete works, Bach cantatas — scan in wrong order, which silently mis-numbers the discs in the resulting `AlbumInfo.Discs[]` (Disc 10 becomes discNumber 2). The downstream effect is wrong audio-locator resolution. Use natural-sort, or extract the integer from `Disc N` and sort numerically.

### H30. `PickListsViewModel` hardcodes pick-list selection by index position
[PickListsViewModel.cs:266](src/CDArchive.App/ViewModels/PickListsViewModel.cs:266):
```csharp
IsEnsembleList = SelectedListIndex == 6;   // index 6 = Ensembles
```
And [PickListsViewModel.cs:286-299](src/CDArchive.App/ViewModels/PickListsViewModel.cs:286):
```csharp
private List<string> CurrentStringList() => SelectedListIndex switch
{
    0 => _forms,
    1 => _categories,
    ...
    9 => _labels,
    _ => [],
};
```
The same applies in `CurrentRenameDict`. The display order in `PickListNames` ([:37-39](src/CDArchive.App/ViewModels/PickListsViewModel.cs:37)) is the de-facto schema. Reordering the array silently routes every Add/Update/Remove command to the wrong list — the user picks "Forms" from a dropdown and edits Categories instead. Adding a new entry between two existing ones renumbers everything from that point. No compiler help, no test catches it.

Fix: a `PickListKind` enum + a `Dictionary<PickListKind, List<string>>` (and matching display-name map). The display order becomes a UI concern; the data routing is enum-keyed and rename-safe.

### H31. Several small editors reconstruct `Result = new(...)` instead of mutating in place — silently wipes unknown fields
The save pattern used by `PerformerEditorWindow`, `SessionEditorWindow`, `RoleEditorWindow`, `ComposerCreditEditorWindow`, `InstrumentEntryEditorWindow`, and `EnsembleEntryEditorWindow`:
```csharp
Result = new AlbumPerformer {
    Name = name, Role = role, Instrument = instrument
};
```
Concrete risks:
- **`PerformerEditorWindow`** ([:38-43](src/CDArchive.App/Views/PerformerEditorWindow.xaml.cs:38)) constructs a fresh `AlbumPerformer` from three string fields. But `AlbumPerformer` carries structured FK fields too (`PersonId`, `EnsembleId` per the EF row model). Editing a performer that was originally a structured Person reference reconstructs as a free-text-only performer — the FK link is silently lost. **The next save downgrades the structured credit to plain text.**
- Any future field added to `AlbumPerformer` / `RecordingSession` / `RoleEntry` / `ComposerCredit` / `InstrumentEntry` needs to be added to every editor's `OnOkClick` or it's silently dropped on edit.

Fix pattern: prefer mutating the existing instance in place (as `MarkerEditorWindow` and `VariantEditorWindow` correctly do — both preserve `Id` and any non-edited fields). Reserve reconstruction for true new-object construction.

### H32. `ComposerEditorWindow.OnOkClick` has no validation at all
[ComposerEditorWindow.xaml.cs:161-165](src/CDArchive.App/Views/ComposerEditorWindow.xaml.cs:161):
```csharp
private void OnOkClick(object sender, RoutedEventArgs e)
{
    SaveToComposer();
    DialogResult = true;
}
```
`SaveToComposer` then does `_composer.Name = NameBox.Text.Trim()` — accepting empty Name and empty SortName silently. `composers.name` is a `UNIQUE NOT NULL` index in the DB; saving a blank-name composer collides with any prior blank-name or generates a confusing downstream `SqliteException`. Validate Name + SortName before accepting OK.

### H33. `EnsembleEntryEditorWindow.OnOkClick` silently flips `IsEnsemble = true` on every save
[EnsembleEntryEditorWindow.xaml.cs:80-89](src/CDArchive.App/Views/EnsembleEntryEditorWindow.xaml.cs:80):
```csharp
Entry = new InstrumentEntry {
    Instrument = Entry.Instrument,
    IsEnsemble = true,                 // always
    Members = _members.Count > 0 ? new List<InstrumentEntry>(_members) : null,
};
```
Opening a non-ensemble `InstrumentEntry` in this editor and clicking OK silently converts it to an ensemble. Nothing in the ctor asserts the entry is supposed to be an ensemble. If a future caller opens this on the wrong instrument, the user's structured instrumentation is mutated unexpectedly. Either assert in the ctor or preserve the original `IsEnsemble`.

### H34. Largest trees in the app have virtualization explicitly disabled
- [CanonView.xaml:186](src/CDArchive.App/Views/CanonView.xaml:186) — `VirtualizingStackPanel.IsVirtualizing="False"` on the composer tree.
- [PiecePickerWindow.xaml:72](src/CDArchive.App/Views/PiecePickerWindow.xaml:72) — same on the piece picker.

These are the two largest trees in the app (composer tree: ~250 composers + ~5,000 pieces + ~30,000 subpieces; piece picker: same). With virtualization off, every `TreeViewItem` for every visible-or-collapsed node is realized into memory at construction and stays alive. Opening the picker on a fully-loaded catalogue creates tens of thousands of WPF containers, and scrolling is O(n) instead of O(viewport). The other two trees (`PiecesWindow`, `TracksView`) correctly enable virtualization.

It was probably disabled to work around the `Items.Refresh()` collapses-expansion-state bug CLAUDE.md describes — but that bug's fix is the expansion-state save/restore the code already does, not blanket-off virtualization. Re-enable + verify expansion state survives, or document concretely why it can't be enabled.

### H35. No application-level resource dictionary — hex colours and converters duplicated across every view
The codebase has no shared `App.xaml` resources for the design tokens or common converters. Result:
- **`BooleanToVisibilityConverter` is redeclared inline in 11 separate files** (MainWindow, PlayerBar, AlbumsView, TracksView, CanonView, PickListsView, PiecesWindow, PiecePickerWindow, CatalogueView, ValidationView, ArchiveBrowserView). One canonical declaration in [App.xaml:6-25](src/CDArchive.App/App.xaml:6) would suffice.
- **Hex colour literals everywhere**: `#007ACC` (primary blue) ~15+ uses, `#E0E0E0` (border gray) ~20+, `#555`/`#555555` (text muted), `#F0F4F8` (status background), `#FAFAFA` (alt-row), `#E65100` (provisional orange), `#2E7D32` (badge green), `#D32F2F` (danger red), `#CCE5FF` (selected), `#888888`, `#333333`, `#2D2D30`. A theming change requires touching every view individually. No dark-mode story; the dark sidebar (`#2D2D30`) + light content (`#F5F5F5`) is hand-coded.
- **No shared styles** for the repeated patterns: status bar `Border` + `TextBlock`, toolbar buttons with hardcoded padding/font-size, "(provisional)" badge formatting.

Add `App.xaml` resource dictionary with named colour brushes (`{StaticResource ProvisionalBadgeBrush}`, etc.), shared converters, and common styles. Pays for itself the first time a design tweak lands.

### H36. 39 `Click="OnFoo"` event handlers across 13 XAML views bypass VM RelayCommands
CLAUDE.md flagged this once as a bug pattern; the audit shows it's systemic. The split:
- `AlbumsView.xaml` (2): all toolbar buttons + context menu items use `Click="OnXxx"`, not `Command="{Binding XxxCommand}"`.
- `TracksView.xaml` (2): same.
- `CanonView.xaml` (1 in tree handlers, plus every nav/sort/show combo via `SelectionChanged` handlers): every action button + the entire context menu.
- `ItunesImportView.xaml` (1): `Click="OnImportSelectedClick"` for the primary action even though `LoadCommand` next to it uses `Command="{Binding ...}"` — **inconsistent within the same toolbar**.

The editor windows (`AlbumEditorWindow`, `PieceEditorWindow`, `TrackEditorWindow`, the small editors) using Click for OK/Cancel is more defensible (dialog plumbing), but the main views' `Click=` pattern is exactly the trap CLAUDE.md documents: status messages get overwritten by handlers, VM `CanExecute` doesn't gate the button, the action can't be tested without WPF, and naming-parallel VM commands appear dead-but-aren't-actually-bound. The right fix is in H2/H19 (extract VMs); this finding is the XAML-side proof that the problem is much wider than CanonView alone.

### H37. Custom `BoolToVisibilityConverter` shares its class name with WPF's built-in — used inconsistently
[Converters/BoolToVisibilityConverter.cs:7](src/CDArchive.App/Converters/BoolToVisibilityConverter.cs:7) declares a class named `BoolToVisibilityConverter` in `CDArchive.App.Converters`. WPF already ships `System.Windows.Controls.BooleanToVisibilityConverter` with identical behaviour.

The codebase uses **both**:
- 10 of the 11 XAML files that need bool→visibility declare `<BooleanToVisibilityConverter x:Key="..." />` with no `xmlns:` prefix — that's the framework one in `System.Windows.Controls` (the default presentation namespace), so they're not using the custom converter at all.
- [ValidationView.xaml:12](src/CDArchive.App/Views/ValidationView.xaml:12) is the lone exception: `<converters:BoolToVisibilityConverter x:Key="BoolToVisibilityConverter" />` — qualified with `converters:` so it picks up the custom one.

Net effect: the custom converter exists, is built into the assembly, but has exactly one usage that's indistinguishable in behaviour from the 10 framework usages. Future maintainers will assume both are interchangeable until one of them subtly diverges (e.g. someone adds a `Hidden` mode to the custom one). Delete the custom converter and switch ValidationView to the framework one, or rename it to `NullableBoolToVisibilityConverter` and give it actual distinct behaviour. Pairs with H35 — once a shared App.xaml resource declares the converter once, the inconsistency surfaces naturally.

### H38. Tests silently `return` on missing preconditions instead of skipping — false-pass risk
- [SqliteRoundTripTests.cs:71](tests/CDArchive.Core.Tests/SqliteRoundTripTests.cs:71): `if (leventail is null) return;`
- [SqliteRoundTripTests.cs:112](tests/CDArchive.Core.Tests/SqliteRoundTripTests.cs:112): `if (leventail is null) return;` (different test)
- [SqliteRoundTripTests.cs:174](tests/CDArchive.Core.Tests/SqliteRoundTripTests.cs:174): `if (!jsonPaths.All(File.Exists)) return;`
- [CanonDataServiceTests.cs:62](tests/CDArchive.Core.Tests/CanonDataServiceTests.cs:62): `if (!File.Exists(dbFile)) return;`

A regression that silently makes the precondition fail (data file deleted, migration not run, etc.) turns the test into "passes by skipping" rather than "fails because the contract is violated". xUnit reports it as a pass. Use `[SkippableFact]` + `Skip.If(...)` from `Xunit.SkippableFact`, or `Assert.Skip(...)` in xUnit 2.5+, so the test result is explicitly `Skipped` not `Passed`.

### H39. Zero test coverage for any ViewModel
The 31 test files cover Core models (`CanonModelDisplayTests`, `MarkerTests`, `PieceSortingTests`, `ComposerSortingTests`), Core services (`SqliteRoundTripTests`, `AlbumSaveInPlaceTests`, `CanonRejectCascadeTests`, etc.), and a few helpers (`StringSimilarityTests`, `TagParserTests`). **No tests for any `*ViewModel`** — `CanonViewModel`, `AlbumsViewModel`, `TracksViewModel`, `PickListsViewModel`, `ItunesImportViewModel`, `PlayerViewModel`, `MainViewModel`, `SettingsViewModel`, `ConversionViewModel`, etc.

CLAUDE.md's "Find the actual UI click handler" lesson is exactly the kind of bug a VM-level test would catch (status message overwritten, fire-and-forget save). C3 (bare catch blocks), C14 (fire-and-forget rename save), H21 (SessionIndex by position), H24 (album-title-only dedup), H30 (hardcoded combo indices), M45 (init-order coupling) — all are VM-layer bugs identified in this review with zero existing test infrastructure to detect a regression. WPF code-behind is hard to test; VMs aren't. Adding a `CDArchive.App.Tests` project with one test per major VM unblocks all the VM-layer refactors recommended elsewhere in this review.

### H40. None of this review's Critical/High findings have regression tests
The 412 existing test attributes are dominated by data-layer round-trips. The Critical findings (C2 NAudio SyncContext, C5 cross-save atomicity, C8 ffmpeg command injection, C9 MusicBrainz exception swallowing, C10 MusicBrainz rate-limit thread safety, C12 TagLib write failure handling, C13 non-atomic file write, C14 fire-and-forget save, C15 live-data mutation) have no tests. If they're fixed, no test guards against re-introducing the bug — and there's no test to drive the fix (no failing-test-first option). Adding even one regression test per Critical finding before fixing it is the cheapest way to lock in the improvements.

### H41. `PieceReferenceIndex.RegisterPiece` silently drops duplicate-title pieces via `TryAdd`
[PieceReferenceIndex.cs:495-496](src/CDArchive.Core/Services/PieceReferenceIndex.cs:495):
```csharp
foreach (var key in EnumerateTitleKeys(p))
    titleMap.TryAdd(NormalizeTitle(key), new IndexEntry(p, ancestors));
```
CLAUDE.md describes the Op. 2 / Op. 10 / Op. 31 "Three Piano Sonatas" collision and how the fix (registering set members under their catalog-bearing titles) avoids it for *that* known case. But the underlying `TryAdd` pattern is still there — any future title collision under the same composer silently drops every piece after the first. Examples that could collide:
- Two pieces with the same `DisplayTitleShort` because they share form + number + key but differ only in catalog (rare for primary catalog but possible with secondary catalogs like B./H./M.).
- A piece whose `Title` happens to equal another piece's `DisplayTitle` or `DisplayTitleShort` under the same composer.

The collision is partially mitigated by `OrderBy(p => p.IsProvisional)` at [:137](src/CDArchive.Core/Services/PieceReferenceIndex.cs:137) (approved wins over provisional), but ties WITHIN approved pieces are silent. Album refs to the loser silently fail to resolve and badges drop to zero with no diagnostic.

Fix: change `Dictionary<string, IndexEntry>` to `Dictionary<string, List<IndexEntry>>` and have `TryResolve` use the subpath / catalog to disambiguate; or detect collisions at build time and log them so they can be reported in the unresolved-refs summary the seeder already produces.

### H42. `CanonDbSeeder` silently discards JSON `IsProvisional` values — every reseed resets approvals
[CanonDbSeeder.cs:73-86 (composers)](src/CDArchive.Core/Data/CanonDbSeeder.cs:73), [:204-236 (pieces)](src/CDArchive.Core/Data/CanonDbSeeder.cs:204), [:515-525 (albums)](src/CDArchive.Core/Data/CanonDbSeeder.cs:515), [:585-592 (tracks)](src/CDArchive.Core/Data/CanonDbSeeder.cs:585) — none of the row builders set `row.IsProvisional = src.IsProvisional`. Each `new XxxRow { ... }` constructor falls through to the C# field default: `public bool IsProvisional { get; set; } = true;`.

Net effect: **every seed marks every row as provisional, regardless of what the JSON `is_provisional` field says.** This matches the documented "everything starts provisional" contract for NEW rows — but for rows that have been previously approved by the user and re-exported via `--export` (which writes `is_provisional: false` to JSON), those approvals are silently lost on reseed.

Concrete impact: a user curates their canon over months — approving Beethoven, Mozart, Bach, hundreds of others — then runs the documented recovery path (`delete data/ClassicalCanon.db && dotnet run --project tools/CDArchive.Tools.SeedDb`). The recovery succeeds, all data is present, but **every Approve action is undone**. The user has to re-approve every composer and piece individually through the Canon view's right-click menu. With H43 (no transaction wrapping), C15 (tests mutating live data), and M21 (seeder deletes DB without confirmation), a few accidental seeds eliminate the user's curation history with no warning.

Fix: in each row builder, set `IsProvisional = src.IsProvisional` to preserve the JSON value. Add a test that seeds, marks something approved, exports, reseeds, and asserts the approval survives.

### H43. `CanonDbSeeder.SeedAsync` calls three sequential `SaveChangesAsync` without a wrapping transaction
[CanonDbSeeder.cs:55, :58, :61](src/CDArchive.Core/Data/CanonDbSeeder.cs:55) — composers commit, then pieces commit, then albums commit. If `SeedAlbums` throws (e.g. duplicate `(Label, CatalogueNumber)` against the filtered unique index), the first two SaveChanges are already committed. The seeder's contract is "atomic seed from JSON" — a partial state is much worse than no state, since the user can't tell from `--export` afterward that anything's missing.

Fix: wrap the three calls in `await using var tx = await db.Database.BeginTransactionAsync(); ... await tx.CommitAsync();`. The whole seed becomes one transaction; a crash anywhere rolls back to the pre-seed empty DB.

### H44. `CanonDbSeeder.MapPerformer` always sets `PersonId = null` and `EnsembleId = null`
[CanonDbSeeder.cs:792-801](src/CDArchive.Core/Data/CanonDbSeeder.cs:792) — comment says "linking is done later via the editor". But the editor (`PerformerEditorWindow`) ALSO doesn't write `PersonId`/`EnsembleId` (per H31). And the runtime save path takes whatever the model has and writes it back. So the structured FK references in `people` / `ensembles` tables are NEVER populated unless a future workflow lands. The schema exists but is dead.

For the current single-user case it's fine — the user works in free-text. But H7 (throwaway-resolver) and H26 (same-surname composer collision) both surface the cost of free-text-only: every dedup is fuzzy, every lookup risks collision. Either prune the unused tables from the schema (the `EnsembleNameRow`, `EnsembleMembershipRow`, `PersonRow` tables and their FK columns on `AlbumPerformerRow`), or commit to populating them in the editor + seeder.

### H45. `MainViewModel` (Singleton) captures `SettingsViewModel` and `ImportExportViewModel` (Transient) — captive dependency
[MainViewModel.cs:8-14, :29-46](src/CDArchive.App/ViewModels/MainViewModel.cs:8) — `MainViewModel` is registered as `Singleton` ([App.xaml.cs:22](src/CDArchive.App/App.xaml.cs:22)) and ctor-injects 8 other VMs. Two of those VMs are registered `Transient`:
- `SettingsViewModel` — [App.xaml.cs:23](src/CDArchive.App/App.xaml.cs:23): `services.AddTransient<SettingsViewModel>();`
- `ImportExportViewModel` — [App.xaml.cs:28](src/CDArchive.App/App.xaml.cs:28): `services.AddTransient<ImportExportViewModel>();`

`MainViewModel` resolves both ONCE at its own construction (which happens once for the singleton's entire lifetime) and stores them in private fields. The "Transient" registration is silently violated — both VMs become de-facto Singletons in practice. The user navigates to Settings, edits, navigates away, navigates back — same VM instance, all prior state retained, no fresh load. If the developer ever relied on Transient semantics (e.g. assumed Settings re-reads from disk on every navigation), the assumption is silently wrong.

Two fixes:
1. **Match lifetimes.** If the VMs are meant to be fresh per-navigation, inject them via `Func<SettingsViewModel>` / `IServiceProvider` and resolve at `NavigateToSettings()` time. (Microsoft's `Microsoft.Extensions.DependencyInjection` doesn't error on captive transients today, but the analyzer `Microsoft.Extensions.DependencyInjection.Abstractions` warning `IDE0079`-style does flag this in newer versions.)
2. **Or accept Singleton.** If the VMs are fine as singletons (which the current behavior already proves they are), change the registration to `Singleton` — same actual behavior, no surprise.

### H46. `AlbumScaffoldingService` pads disc folder names but `ArchiveAudioLocator` doesn't — silent playback breakage for 10+ disc sets
[AlbumScaffoldingService.cs:19-25](src/CDArchive.Core/Services/AlbumScaffoldingService.cs:19):
```csharp
public string GetDiscFolderName(int discNumber, int totalDiscs)
{
    if (totalDiscs >= 10)
        return $"Disc {discNumber:D2}";
    return $"Disc {discNumber}";
}
```
For a 10+ disc box set, scaffolding creates `Disc 01`, `Disc 02`, ..., `Disc 10`. But [ArchiveAudioLocator.cs:105](src/CDArchive.Core/Services/ArchiveAudioLocator.cs:105) constructs the lookup path as:
```csharp
var defaultDir = Path.Combine(albumDir, $"Disc {disc.DiscNumber}");
```
— `disc.DiscNumber` is an `int`, default formatting, no padding. So the locator looks for `Disc 1`, `Disc 9`, `Disc 10`. For discs 1-9 in a padded box set, **the locator's convention path doesn't match the scaffolded folder** (looks for `Disc 1`, folder is `Disc 01`). Playback silently fails to find the file; the user has to manually set `AlbumDisc.FolderName` for every padded disc to override.

This pairs with H29 (lexicographic ordering at 10+ discs) and M35 (three places hardcode "Disc *") — together they make the 10+ disc box set a fragile case across the entire pipeline. Pick one convention (padded or unpadded) and apply it everywhere via a shared `DiscFolderConventions.Format(discNumber, totalDiscs)` helper that scaffolding, locator, and scanner all call.

### H47. `PiecesWindow.xaml.cs` duplicates CanonView's piece-tree machinery — third implementation of "sort pieces"
[PiecesWindow.xaml.cs:157-197](src/CDArchive.App/Views/PiecesWindow.xaml.cs:157) — re-implements the entire piece sort logic (Catalogue / Title / Category / Year tie-breaker chains) that already exists in two other places:
- [PieceSorting.cs:44-125](src/CDArchive.Core/Models/PieceSorting.cs:44) — the UI-free helper extracted to Core (per the H2 lesson), unit-testable.
- [CanonView.xaml.cs](src/CDArchive.App/Views/CanonView.xaml.cs) — calls `PieceSorting.Sort`, but maintains its own `_pieceSortField` state and column-header click handling.

And it re-implements the expansion-state save/restore dance:
- [PiecesWindow.xaml.cs:100-142](src/CDArchive.App/Views/PiecesWindow.xaml.cs:100) — `SaveExpansionState` / `RestoreExpansionState` / `ApplyExpandedItems`.
- [CanonView.xaml.cs](src/CDArchive.App/Views/CanonView.xaml.cs) — `SaveAllExpansionState` / `RestoreAllExpansionState` / etc. (similar but uses 3 separate hash sets).

Three implementations of the same domain logic. Adding a new sort field requires three coordinated edits; fixing a sort bug requires touching three files (none of which share tests). The `PieceSorting` helper IS the right abstraction — both views should call it. The expansion-state machinery has no extracted helper at all; pairs with H2 (CanonView code-behind sprawl) as the second site that would benefit from a `TreeExpansionStateService`.

---

## 🟡 Medium

### M1. `IsScrubbing` is a one-way state used from code-behind, not from XAML
[PlayerViewModel.cs:117-118](src/CDArchive.App/ViewModels/PlayerViewModel.cs:117) — public getter, private setter, used by `BeginScrub()` / `EndScrub()` called from `PlayerBar.xaml.cs`. Works fine, but bypasses `ObservableProperty` — neither testable through bindings nor visible via PropertyChanged. Make it `[ObservableProperty]` or scope `internal`.

### M2. `ImportExportViewModel` is `Transient` but most other VMs are `Singleton` — risk of stale state
[App.xaml.cs:28](src/CDArchive.App/App.xaml.cs:28) — every navigation creates a fresh instance. If it caches last-used file paths, state is lost across navigations. Verify intent; singleton would match the rest.

### M3. `PieceRow.AlbumRefs` inverse navigation exists but `EndPiece` has none
[CanonDbContext.cs:619-625](src/CDArchive.Core/Data/CanonDbContext.cs:619) — deliberate per the comment, but deleting a piece referenced as `end_piece_id` won't be detected by `Composer.Pieces` walk. Reject cascade hits FK Restrict and rolls back (fail-safe), but the user sees a generic SQLite error. Add an inverse or pre-check in `CanonRejectCascade.RejectPieceAsync`.

### M4. `ItunesImporter` uses `ref int` counters across multiple helpers
[ItunesImporter.cs:237-247](src/CDArchive.Core/Services/ItunesImporter.cs:237) — passing `ref int newComposers, ref int newPieces, ref int newSubpieces` through three call layers is awkward and easy to miss-increment. Wrap in a `class Counters { int Composers, Pieces, Subpieces }` and pass once.

### M5. iTunes import has no album dedup
`ItunesImporter` creates a fresh `CanonAlbum` for every iTunes Album group. Re-importing the same data creates duplicates. Dedup by `(Title, Label, CatalogueNumber)` or surface duplicates in the import preview.

### M6. `PieceRow.Title` and several "logical key" columns lack `.IsRequired()` and no min-identity CHECK
[CanonDbContext.cs:229](src/CDArchive.Core/Data/CanonDbContext.cs:229) — pieces with null Title are intentional (structured-form). But the schema would silently accept `Title=null AND Form=null AND CatalogInfo=null AND Composers=null` — an identity-less row. A `CHECK at_least_one_identity` would be cheap insurance.

### M7. Bare `ObservableCollection` reassignment causes UI flicker on filter/sort
Throughout the VMs — `Albums = new ObservableCollection<CanonAlbum>(sorted)` wipes the collection and re-attaches every item. For 3k+ items this is a perceivable scroll-reset/flicker. Particularly bad in [ItunesImportViewModel.cs:218](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:218) where `ApplyFilter` runs on every keystroke (`partial void OnFilterChanged → ApplyFilter`), rebuilding a potentially-large collection for each typed character. Use clear-and-re-add or `CollectionView`/`ICollectionView` with `Refresh()`. Debounce the filter text update.

### M8. `BuildSequence` ordering uses `VolumeNumber ?? 0` which collapses null volumes
[PlayerViewModel.cs:255-257](src/CDArchive.App/ViewModels/PlayerViewModel.cs:255) — null-volume discs sort as 0, ahead of `Vol 1` discs. A mixed album would interleave oddly. Pick null-volumes-last (`?? int.MaxValue`) or document.

### M9. `ItunesLibraryReference.IsTaggedTrue` won't catch all non-music kinds
[ItunesLibraryReference.cs:70-77](src/CDArchive.Core/Services/ItunesLibraryReference.cs:70) — hardcoded list of `Podcast`, `Movie`, etc. iTunes has added kinds (`Voice Memo`, `iTunes Extra`, `iTunes U`). Document or invert the check ("only include rows where Genre is classical-y").

### M10. `PreferredAudioFormat` is `enum?` in SettingsData but `enum` on interface
[ArchiveSettings.cs:73](src/CDArchive.Core/Services/ArchiveSettings.cs:73) — settings.json with `"PreferredAudioFormat": "Wma"` throws `JsonException` (caught silently) and reverts to default. Use `[JsonConverter(typeof(JsonStringEnumConverter))]` + a permissive default.

### M11. `MusicBrainzReference` — User-Agent verification, caching
Verified: User-Agent is set (good — `CDArchive/1.0`). No response caching — repeated lookups for the same composer re-hit the API.

### M12. `EnsureColumnAsync`/`EnsureColumnNullableAsync` execute SQL with string interpolation of table/column names
[SqliteCanonDataService.cs:199](src/CDArchive.Core/Services/SqliteCanonDataService.cs:199) — `$"ALTER TABLE {table} ADD COLUMN ..."`. All names come from string literals today (no injection), but if user input ever flows in, this is a footgun. Wrap with a `[A-Za-z_][A-Za-z0-9_]*` validator.

### M13. Backup-file mess in `data/` suggests no automated safe-backup story
Git status shows `Classical Canon albums.json.bak.20260422_160109`, `ClassicalCanon.db.bak,pre-loose-tracks` (typo with comma!), `ClassicalCanon.db.empty-after-stash.20260516`, and many more. "Panic backup before risky operation" pattern. A `--backup` flag on the seeder that writes a dated backup to `~/.cdarchive/backups/` with retention would replace the in-tree mess.

### M14. `CanonDataService` singleton freezes the data dir at construction time
Not disposable today, but the path resolver runs once. A user changing archive location mid-session would not be reflected.

### M15. EF Core `.Include` chains aren't `.AsSplitQuery()`
For album loads with `.Include(...).ThenInclude(...).ThenInclude(...)`, the Cartesian explosion can be significant. EF Core 8 has `AsSplitQuery()` to issue separate SELECTs. Measure for the full-catalogue load.

### M19. `FfmpegConversionService.ConvertAlbumAsync` uses fixed concurrency formula
[FfmpegConversionService.cs:46](src/CDArchive.Core/Services/FfmpegConversionService.cs:46) — `Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4))`. Reasonable default, but ffmpeg is I/O-bound for FLAC→MP3. Move to a setting.

### M20. SeedDb's `FindRepoRoot` accepts the JSON-file-or-DB-file marker
[tools/CDArchive.Tools.SeedDb/Program.cs:336-352](tools/CDArchive.Tools.SeedDb/Program.cs:336) — necessary post-migration. Add a `--data-dir` override flag for explicitness.

### M21. SeedDb default mode deletes the DB without confirmation
[Program.cs:70-74](tools/CDArchive.Tools.SeedDb/Program.cs:70):
```csharp
if (File.Exists(dbPath))
{
    Console.WriteLine("Deleting existing database file…");
    File.Delete(dbPath);
}
```
Add a `--force` flag or y/n prompt. Otherwise an accidental `dotnet run --project tools/CDArchive.Tools.SeedDb` thinking it was idempotent loses all work since the last `--export`.

### M22. SeedDb writes nothing to a log file
Everything to stdout. Unresolved-refs reports can be hundreds of lines. Add `--log path.log` or always tee to a dated file in `data/logs/`.

### M23. `AlbumEditorWindow` uses `JsonSerializer.Serialize`/`Deserialize` as the deep-clone for Cancel semantics
[AlbumEditorWindow.xaml.cs:65-66](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:65) — works (and CLAUDE.md flagged it once). But clones the whole album graph including every track, every piece-ref, every performer. For a multi-disc box set, open-editor latency is noticeable. Either clone only what's editable, use copy-on-write, or snapshot/restore for Cancel.

### M24. `TagParser.RestoreDiacritics` runs N+M regex passes per string
[TagParser.cs:256-275](src/CDArchive.Core/Services/TagParser.cs:256) — for every string, iterates every multi-word + every single-word diacritic entry. ~20 entries today; at 100+ this is real work per string. Build a single `Regex` with alternation once, or use a trie. Matters because TagParser is called per-track on import scan (50k+ invocations at 3k CDs).

### M25. `TagParser.WorkNumberRegex` matches `No` — collides with `Nocturne`?
[TagParser.cs:46-48](src/CDArchive.Core/Services/TagParser.cs:46) — `\bNo\.?\s*(\d+)`. Won't match `Nocturne` alone (no trailing digits), but `Nocturnes Op. 27 No. 1` works because `No. 1` has digits. Worth a defensive test: `Nocturne in C, No. 1`.

### M26. `AlbumEditorWindow.TrackRow` is a private class inside a View file
[AlbumEditorWindow.xaml.cs:44-51](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:44) — `record class TrackRow(...)` — works but is a window-level projection invisible to tests. A `TrackEditorRow` model in the VM layer would let tests build it.

### M27. iTunes XML's `Persistent ID` is captured but apparently unused
[ItunesLibraryReference.cs:85](src/CDArchive.Core/Services/ItunesLibraryReference.cs:85) — parsed but unreferenced. It's the stable identity iTunes uses across renames. If you want incremental import (only new-since-last-sync), PersistentId is the durable key. Either start using it or remove the column.

### M28. `MusicBrainzReference` returns the first match, ignoring count/quality
MB search returns a `count` field; search is paginated. We ask for `limit=5` and take the first. For a generic name like "Strauss" you might get the wrong one. Surface matches as a picker when ambiguous.

### M29. `TrackEditorWindow` uses `JsonSerializer.Serialize` to detect mixed-state
[TrackEditorWindow.xaml.cs:211-214](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:211), [:238-241](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:238):
```csharp
var pieceRefFingerprints = tracks
    .Select(t => JsonSerializer.Serialize(t.PieceRefs ?? []))
    .Distinct()
    .ToList();
```
Full JSON serialization just to detect equality across N tracks' piece-ref / performer collections. Expensive at scale (multi-edit on 50+ tracks each with multi-ref means hundreds of serialize calls per open), and brittle — JSON property ordering or nullable handling could flip mixed-status. A proper `Equals`/`IEqualityComparer` on `TrackPieceRef` and `AlbumPerformer` (or `SequenceEqual` with a custom comparer) would be both faster and more semantically correct.

### M30. `ItunesImportViewModel` has no cancellation token
Neither `LoadAsync` nor `ImportTracksAsync` takes a `CancellationToken`. A long iTunes XML parse (hundreds of MB) or a large batch import (thousands of tracks) can't be cancelled — the user clicks Load, waits 30 seconds, decides they wanted a different filter, and is stuck staring at "Reading iTunes library…". Add cancellation, wire a Cancel button to `IsBusy`.

### M31. `ItunesImportViewModel.ImportTracksAsync` has no progress reporting
[ItunesImportViewModel.cs:117](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:117) — "Importing N track(s)…" is the only status message until completion. For a batch of 500+ tracks (re-import after iTunes rescan, library migration), this looks frozen. Add `IProgress<(int done, int total, string current)>` and update the status message as the import progresses through composers → pieces → albums → loose tracks.

### M32. `CataloguingService.FormatEntriesAsync` silently wipes Artist / Genre / Year / DiscNumber / DiscCount
[CataloguingService.cs:168-176](src/CDArchive.Core/Services/CataloguingService.cs:168):
```csharp
entry.Album = albumName;
entry.Artist = "";
entry.Genre = "";
entry.Year = null;
entry.DiscNumber = null;
entry.DiscCount = null;
```
The comment hints at intent ("unreliable in raw classical CD tags"), but the user sees their existing tag values disappear on running Format with no confirmation and no undo (especially without H27's snapshot). At minimum, only wipe when the field is currently empty/default; ideally, surface the planned wipes in a preview UI and let the user opt out per-field.

### M33. `CataloguingService` MusicBrainz lookups serialise per track
[CataloguingService.cs:81-96](src/CDArchive.Core/Services/CataloguingService.cs:81) — composer lookups inside the `foreach` loop. Each MB call is rate-limited to 1/sec (C10). For an album with N distinct composers, that's N+ seconds minimum, in series. Pre-collect the distinct `(lastName, firstName)` tuples up front, run all lookups in parallel through the rate limiter, then walk entries with results already cached. Combined with H26's cache-key fix.

### M34. `CataloguingService` workGroups lookup is O(n²) per album
[CataloguingService.cs:128-131](src/CDArchive.Core/Services/CataloguingService.cs:128):
```csharp
var workKey = NormalizeWorkKey(tag.RawWork ?? "");
var totalMovements = workGroups
    .FirstOrDefault(g => g.Key == workKey)?.Count() ?? 1;
```
`FirstOrDefault` over `workGroups` per entry, then `.Count()` (re-enumerates the group) per hit. For a 20-track album this is fine; for a 100-track box set it adds up. Convert once: `var movementCount = workGroups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);` then O(1) lookup.

### M35. The "Disc *" folder-naming convention is hardcoded in three places
- [CataloguingService.cs:190](src/CDArchive.Core/Services/CataloguingService.cs:190) — `Directory.GetDirectories(albumPath, "Disc *")`.
- [ArchiveScannerService.cs:8](src/CDArchive.Core/Services/ArchiveScannerService.cs:8) — `DiscFolderRegex = new(@"^Disc \d+(-\d+)?$", ...)`.
- [ArchiveAudioLocator.cs:105](src/CDArchive.Core/Services/ArchiveAudioLocator.cs:105) — `Path.Combine(albumDir, $"Disc {disc.DiscNumber}")`.

Three different services with three different representations (glob, regex, string-format) of the same convention. Anything else (`CD1`, `Disc One`, `Part 1`, language-localised) is invisible to all three. Lift to a single `IArchiveSettings.DiscFolderPattern` (or a shared `ArchiveConventions` helper). Without it, the next user / a renamed folder silently produces "empty album" symptoms in scan + lost playback in the locator + skipped tagging.

### M36. Archive-walking services have no error handling — one bad folder crashes the whole pass
- [ArchiveScannerService.cs:27, :139](src/CDArchive.Core/Services/ArchiveScannerService.cs:27) — `_fs.EnumerateDirectories(archiveRoot)` and per-album sub-enumerations bubble exceptions all the way out.
- [DuplicateDetectionService.cs:27](src/CDArchive.Core/Services/DuplicateDetectionService.cs:27) — same shape; a network blip or permissions error during dedup throws to the caller.

A single problem folder (network share blip, permissions error, antivirus quarantine, symlink loop, corrupt directory entry) aborts the operation with no partial result. With 3,000 albums one bad folder kills the whole pass. Wrap the per-album loop body in try/catch, log + add a `ValidationIssue` (or surface a "skipped N folders" tally for dedup), continue.

### M37. `ArchiveScannerService.ShouldSkip` uses an undocumented magic prefix
[ArchiveScannerService.cs:77-78](src/CDArchive.Core/Services/ArchiveScannerService.cs:77):
```csharp
private static bool ShouldSkip(string name) =>
    name.StartsWith("aa", StringComparison.Ordinal);
```
No comment, no setting, no test, no mention in CLAUDE.md. Presumably the user's "draft / staging / not-yet-finalised" convention. Future you / future maintainer won't know why an album folder named `aardvark concertos` is silently invisible. Document the convention, or lift to `IArchiveSettings.SkipFolderPrefixes`.

### M38. `ArchiveScannerService.ValidateDiscNaming` only checks padding consistency
[ArchiveScannerService.cs:231-246](src/CDArchive.Core/Services/ArchiveScannerService.cs:231) — flags mixed padded/unpadded names, nothing else. Doesn't detect:
- Gaps: `Disc 1`, `Disc 3` (Disc 2 missing) — silent.
- Duplicates: `Disc 1`, `Disc 1` — silent (theoretically prevented by filesystem, but case-insensitive collisions on case-sensitive volumes are possible).
- Off-by-one starts: `Disc 0`, `Disc 1` or `Disc 2`, `Disc 3`.

These are exactly the cases a user would want flagged before an iTunes import or a tagging pass. Add gap-detection (`max - min + 1 != count`) and start-at-1 check.

### M39. `DuplicateDetectionService` Levenshtein threshold doesn't scale with string length
[DuplicateDetectionService.cs:42](src/CDArchive.Core/Services/DuplicateDetectionService.cs:42) — `LevenshteinDistance(...) <= 3`. A fixed threshold of 3 over-matches short names and under-matches long ones:
- "Bach" vs "Back" — distance 1, flagged as potential duplicate (false positive).
- "Symphony 1" vs "Symphony 4" — distance 1, both real, unrelated (false positive).
- "Mahler Symphony 5 — Bernstein, NYP, 1963" vs "Mahler Symphony 5 — Bernstein, Wiener Philharmoniker, 1987" — distance >3, two recordings of the same work missed (false negative — and this is the *real* use case for dedup, two recordings of the same work).

Switch to normalized distance (e.g. `distance / max(len(a), len(b)) <= 0.15`) or a token-set / Jaccard similarity that compares word bags rather than character edits.

### M40. `StringSimilarity.Normalize` strips non-ASCII alphanumerics → mangles classical composer names
[StringSimilarity.cs:13](src/CDArchive.Core/Helpers/StringSimilarity.cs:13):
```csharp
var cleaned = Regex.Replace(lower, @"[^a-z0-9\s]", "");
```
`é`, `ñ`, `ü`, `š`, `č`, `ř` — every diacritic just gets deleted. "Dvořák" becomes "dvok", "Schönberg" becomes "schnberg", "Bartók" becomes "bartk". Two recordings of Dvořák's *New World Symphony* normalize differently depending on which one's name was rendered with `dvorak` vs `dvořák`. The dedup similarity check then mis-classifies them.

Fix: Unicode-normalize to NFKD and strip the combining-marks category, so `é` → `e`, `ř` → `r`, etc. Standard helper:
```csharp
var nfkd = input.Normalize(NormalizationForm.FormKD);
var folded = new string(nfkd.Where(c =>
    CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
```

### M41. `PickListsViewModel.ApplyRenamesToPiece` doesn't recurse into `Versions`
[PickListsViewModel.cs:332-361](src/CDArchive.App/ViewModels/PickListsViewModel.cs:332) — recurses into `piece.Subpieces` but not `piece.Versions`. A `CanonPieceVersion` has the same Form / Category / Key / Catalog fields as a piece (the editor shares the UI), and they can reference pick-list values. After a Form rename, every version still has the old form name. The dropdown in the version editor shows "Concertino" greyed-out as a stale value; the user has no way to know it's a leftover from the rename.

Fix: walk `piece.Versions` (and `version.Subpieces`) in `ApplyRenamesToPiece`.

### M42. `PickListsViewModel.AddItem` silently rejects duplicates
[PickListsViewModel.cs:111-112, :119](src/CDArchive.App/ViewModels/PickListsViewModel.cs:111) — `if (list.Contains(value, ...)) return;` with no status message. The user types "Symphony", clicks Add, nothing happens, no visual feedback. Show "\"Symphony\" already exists" in StatusMessage so the user understands.

### M43. `PickListsViewModel.UpdateItem` allows renaming to an existing name → silent list duplicates
[PickListsViewModel.cs:160-162](src/CDArchive.App/ViewModels/PickListsViewModel.cs:160) — `list[i] = newValue` is unconditional. If `newValue` is already in the list at another index, the list now contains two identical entries (which then sort adjacently and look like one row in the UI). Also poisons the rename chain — both old entries map to the same new name. Either reject or merge.

### M44. `PickListsViewModel.PickListsForDialog` returns a snapshot with only 2 of the 10 lists populated
[PickListsViewModel.cs:235-243](src/CDArchive.App/ViewModels/PickListsViewModel.cs:235) — populates `Instruments` and `Ensembles` only. Every other list (`Forms`, `Categories`, `CatalogPrefixes`, `KeyTonalities`, `CreativeRoles`, `VoiceTypes`, `PerformerRoles`, `Labels`) is left at its default-init state (likely null or empty). A dialog needing CreativeRoles gets an empty dropdown with no warning. The XML comment hints at the intent ("suitable for passing to dialogs that need the instrument list") but the typed signature suggests a complete `CanonPickLists`. Either rename to `IngredientsListsForDialog` to make the scope obvious, or fully populate it.

### M45. `PickListsViewModel` is coupled to `CanonViewModel` with no init-order safety
[PickListsViewModel.cs:48-51](src/CDArchive.App/ViewModels/PickListsViewModel.cs:48) — ctor-injects `CanonViewModel` and writes to `_canonVm.Pieces` and `_canonVm.PickLists` on save. Both are singletons (per `App.xaml.cs`), but if the user navigates to PickLists before ever loading Canon, `_canonVm.Pieces` is empty. The rename pass walks an empty list, persists nothing, and the user sees "Saved" while every piece in the DB still references the old form name. The next Canon load reads from DB and the user sees stale values back.

Fix: trigger `_canonVm.LoadDataCommand.ExecuteAsync(null)` before applying renames if `_canonVm.Pieces.Count == 0`, or pull pieces directly from the data service (bypass the VM cache) so the rename always runs against the persistent state.

### M46. `SessionEditorWindow.OnOkClick` accepts an all-null session
[SessionEditorWindow.xaml.cs:27-43](src/CDArchive.App/Views/SessionEditorWindow.xaml.cs:27) — every field is allowed to be empty. Clicking OK on an unedited New Session dialog adds a row with `Dates=null, Venue=null, City=null, Country=null, Engineers=null, Producers=null` to the album. Subsequent rendering shows a blank list item the user can't easily distinguish from "loading…". Require at least one field non-empty.

### M47. `PiecePickerWindow` rebuilds the entire tree on every search keystroke + synchronous open
[PiecePickerWindow.xaml.cs:84-88, :92-154](src/CDArchive.App/Views/PiecePickerWindow.xaml.cs:84) — `OnSearchChanged` calls `RebuildTree` per keystroke. `RebuildTree` does composer-filter, search-filter (walks every piece + every version + every subpiece recursively), sort, group-by composer, build composer/piece tree nodes, then auto-expand on search hits. For a 5,000-piece catalogue this rebuilds the entire tree per character typed. Compound with the constructor calling `RebuildTree` synchronously — opening the picker also freezes for the duration. Debounce the search (200-300ms), and consider async open via `Task.Run` for the initial build.

### M48. `PieceRefDetailsWindow.PopulateMarkerCombo` disambiguates markers by `(Kind, Value)` fallback
[PieceRefDetailsWindow.xaml.cs:256-266](src/CDArchive.App/Views/PieceRefDetailsWindow.xaml.cs:256) — when matching a `MarkerReference` (which has `Id = 0` for legacy/un-persisted markers), the fallback matches by `Kind == selected.Kind && Value == selected.Value`. If two markers in the same subpiece share both kind and value (two `Tempo: Allegro` markers at different bar numbers — uncommon but legitimate for sectional pieces), the later one silently wins. Tighten the fallback to also compare `BarNumber` and `Number`, or refuse the fallback when ambiguous.

### M49. `CanonView.xaml` has 9 near-identical `HierarchicalDataTemplate`s
[CanonView.xaml:278-523](src/CDArchive.App/Views/CanonView.xaml:278) — nine separate templates for `ComposerTreeNode`, `CanonPiece`, `SubpieceDisplayNode`, `PieceOriginalNode`, `VersionDisplayNode`, `ContributedRoleGroupNode`, `CrossComposerSubpieceNode`, `ContributedPieceNode`, plus the level-1 composer. Each repeats: `<Border Width="65">` (pieces column placeholder) + `<Grid Width="16" Height="16">` (expander cell) + `<Path Style="{StaticResource ExpanderArrow}">` + `<Border Width="4">` (splitter spacer) + `<TextBlock>` with binding + provisional badge + catalog text + hit badge. About 80% identical XAML by line count.

Refactor: a single shared `<DataTemplate>` for the row chrome that binds to common properties (`DisplayTitle`, `HasChildren`, `Catalog`, an interface like `ITreeRowNode`), plus a `DataTemplateSelector` if visual differentiation is needed. Drastically shrinks the file and removes the drift risk (e.g. one template using `Foreground="#444"` and another using `#333` is harder to spot at 533 lines than at 200).

### M50. `MainWindow` hardcodes Height/Width with no persistence
[MainWindow.xaml:10](src/CDArchive.App/MainWindow.xaml:10) — `Height="700" Width="1120"` literal, no `WindowState` binding, no save-on-close. Every launch starts at the same fixed size in the centre of the primary monitor regardless of where the user resized/moved/maximised it last time. Annoying for daily use on a multi-monitor setup. Persist via `ArchiveSettings` (or a separate `WindowSettings`) and restore on startup.

### M51. `SelectedIndex="0"` + `SelectionChanged` handler is repeated across 6+ views
- [CanonView.xaml:80, :102, :127, :141](src/CDArchive.App/Views/CanonView.xaml:80) — sort combos + show combos.
- [AlbumsView.xaml:86](src/CDArchive.App/Views/AlbumsView.xaml:86) — show combo.
- [TracksView.xaml:64](src/CDArchive.App/Views/TracksView.xaml:64) — show combo.
- Plus the editor windows' sentinel-Mixed combos that rely on hardcoded `SelectedIndex == 3`.

Same brittleness as H30: reorder a `<ComboBoxItem>` and the code-behind's `switch (SelectedIndex)` silently routes to the wrong filter / sort. Use `Tag` (or strongly-typed enum source) instead of index-based dispatch.

### M52. `Status bar` pattern (Border + TextBlock + StatusMessage binding) duplicated across 6 views
- [CanonView.xaml:20-24](src/CDArchive.App/Views/CanonView.xaml:20), [AlbumsView.xaml:98-102](src/CDArchive.App/Views/AlbumsView.xaml:98), [TracksView.xaml:76-80](src/CDArchive.App/Views/TracksView.xaml:76), [ItunesImportView.xaml:57-61](src/CDArchive.App/Views/ItunesImportView.xaml:57), [NewAlbumView.xaml:68-82](src/CDArchive.App/Views/NewAlbumView.xaml:68), [ImportExportView.xaml:69-74](src/CDArchive.App/Views/ImportExportView.xaml:69) — slightly different colors and paddings but the same shape. A `<views:StatusBar Message="{Binding StatusMessage}" />` UserControl would consolidate.

### M53. `ConversionView` has no guard on the Convert/Cancel buttons
[ConversionView.xaml:41-58](src/CDArchive.App/Views/ConversionView.xaml:41) — `Convert` and `Cancel` buttons have no `IsEnabled` binding. Clicking Convert with no album selected runs `ConvertCommand` against a null `SelectedAlbum`. Clicking Cancel when nothing is running runs `CancelCommand` against a non-existent operation. Bind `IsEnabled` to VM-side `CanConvert` / `CanCancel` properties (or use `IRelayCommand.CanExecute`).

### M54. Color converters allocate a new `SolidColorBrush` on every call
- [ConversionStatusToColorConverter.cs:16-21](src/CDArchive.App/Converters/ConversionStatusToColorConverter.cs:16) — `new SolidColorBrush(Colors.Gray)` etc. on every conversion.
- [ValidationSeverityToColorConverter.cs:16-19](src/CDArchive.App/Converters/ValidationSeverityToColorConverter.cs:16) — same.

For a DataGrid with N rows each binding the converter, that's N short-lived `SolidColorBrush` instances per render pass. Brushes are freezable WPF resources meant to be created once and reused. Pre-declare `static readonly SolidColorBrush Gray = new(Colors.Gray); Gray.Freeze();` (and friends) and return the same instance every call. Frozen brushes are also thread-safe and slightly cheaper to render. The hardcoded colors then also become a single edit-site for theme changes (extends H35).

### M55. `HitCountBadgeConverter` silently returns 0 for unknown node types
[HitCountBadgeConverter.cs:27-39](src/CDArchive.App/Converters/HitCountBadgeConverter.cs:27) — a `switch` over 9 known node types with `_ => 0` for the default. If someone adds a new node type to the Canon tree (e.g. for a new piece-grouping concept) and forgets to add a case here, the new node's badge silently reads 0 — looks like "no albums reference this" rather than "we forgot to wire it up". Either throw `NotSupportedException` for the default arm (loud failure) or introduce an `IHasHitCount` interface that every node implements (compile-time check). Pairs with the H7 risk: when `PieceReferenceIndex.Current` is stolen by a throwaway resolver, this converter also silently returns 0 for everything.

### M56. `StringSimilarityTests` don't cover the diacritic case (M40)
[StringSimilarityTests.cs](tests/CDArchive.Core.Tests/Helpers/StringSimilarityTests.cs) — 7 Normalize tests cover lowercasing, punctuation removal, whitespace collapse, digits, null/empty — but never assert anything about non-ASCII. Adding `Normalize("Dvořák").Should().Equal(Normalize("dvorak"))` would explicitly document the dedup-correctness behaviour. With the current implementation that test fails (both normalize to different strings — "dvork" vs "dvorak"), which is exactly the M40 bug.

### M57. `DuplicateDetectionServiceTests` covers the happy path but not the false-positive/negative cases (M39)
[DuplicateDetectionServiceTests.cs](tests/CDArchive.Core.Tests/Services/DuplicateDetectionServiceTests.cs) — `FindPotentialDuplicates_CloseLevenshteinMatch_IsDetected` proves the distance-1 case works. Missing:
- **False-positive test**: `FindPotentialDuplicates("Bach")` against `["Back"]` — should NOT match for a music app, but does with threshold-3.
- **False-negative test**: `FindPotentialDuplicates("Mahler 5 — Bernstein NYP 1963")` against `["Mahler 5 — Bernstein Wiener Philharmoniker 1987"]` — these are two different recordings of the same work; the dedup should flag them but won't (distance > 3).
- **Diacritic test**: `FindPotentialDuplicates("Dvorak New World")` against `["Dvořák New World"]` — should match; doesn't.

These tests would fail today, which is the M39/M40 evidence captured executably.

### M58. Test `Dispose` swallows IOExceptions in cleanup
- [NAudioPlayerServiceTests.cs:27](tests/CDArchive.Core.Tests/NAudioPlayerServiceTests.cs:27): `try { Directory.Delete(_tempDir, recursive: true); } catch { }`
- [CanonRejectCascadeTests.cs:36](tests/CDArchive.Core.Tests/CanonRejectCascadeTests.cs:36): same with `/* best-effort */` comment.
- Likely several others. Same L18 pattern — temp directories accumulate in `%TEMP%` over time when file locks delay cleanup.

### M59. No tests for converters
None of the 4 converters (`BoolToVisibilityConverter`, `ConversionStatusToColorConverter`, `ValidationSeverityToColorConverter`, `HitCountBadgeConverter`) have test coverage. The first three are trivial enough that a 2-line test apiece is sufficient; `HitCountBadgeConverter`'s type-switch (M55) is genuinely worth testing — a test that round-trips every known node type catches the silent-default-0 regression at compile time.

### M60. `PieceReferenceIndex.RebuildInternal` is not thread-safe — readers can observe inconsistent state mid-rebuild
[PieceReferenceIndex.cs:165-170](src/CDArchive.Core/Services/PieceReferenceIndex.cs:165):
```csharp
_hitsForPiece    = hitsForPiece;
_hitsForOriginal = hitsForOriginal;
_hitsForVersion  = hitsForVersion;
_hitsForComposer = hitsForComposer;

Indexed?.Invoke(this, EventArgs.Empty);
```
Four sequential reference assignments with no memory barrier. C# reference writes are individually atomic, but a reader executing `HitsForPiece(p)` concurrently with `RebuildInternal` may see `_hitsForPiece` already swapped while `_hitsForOriginal` is still the old one (or vice versa) — particularly likely now that `Rebuild` can be invoked from `LoadDataAsync` (potentially background thread) while the UI thread's badge converter is reading the static `Current`. Visible symptom: badge counts inconsistent across siblings until the next full repaint.

Fix: wrap the dictionary swap in a `lock`, or use an atomic single-reference container (e.g. a `SnapshotState` record assigned via `Interlocked.Exchange`) so readers see all-old or all-new but never a mix.

### M61. Three recursive piece-tree walks have no cycle guard
- [PieceReferenceIndex.cs:179-205](src/CDArchive.Core/Services/PieceReferenceIndex.cs:179) — `AggregateSetHits`
- [PieceReferenceIndex.cs:487-513](src/CDArchive.Core/Services/PieceReferenceIndex.cs:487) — `RegisterPiece`
- [CanonDbSeeder.cs:249-286](src/CDArchive.Core/Data/CanonDbSeeder.cs:249) — `MapPiece` recurses into both `Subpieces` and `Versions.Subpieces`

All walk `Subpieces` recursively without tracking visited nodes. The data model doesn't prevent a piece from being its own ancestor (a corrupt seeded record, a user edit that creates a cycle, or a hand-edited JSON file). One bad row → `StackOverflowException`, which is unrecoverable (process-killing). Add a `HashSet<CanonPiece>` visited guard or a max-depth check (~30 levels is far beyond any real classical work).

### M62. `PieceReferenceIndex` constructor unconditionally captures `Current` static
[PieceReferenceIndex.cs:28](src/CDArchive.Core/Services/PieceReferenceIndex.cs:28):
```csharp
public PieceReferenceIndex() { Current = this; }
```
Every `new PieceReferenceIndex()` — including throwaway resolvers — captures the static. The Rebuild-reclaim pattern works for the singleton DI instance, but means anywhere a throwaway is constructed without immediately Rebuild'ing leaves `Current` stale. See the H7 detail. The minimum fix is a separate ctor for throwaway use that skips the assignment; the right fix is removing the static accessor entirely.

### M63. `PieceReferenceIndex.Indexed` event fires on whichever thread called Rebuild
[PieceReferenceIndex.cs:170](src/CDArchive.Core/Services/PieceReferenceIndex.cs:170) — `Indexed?.Invoke(this, EventArgs.Empty)` runs on the calling thread. The Canon view subscribes to this event (in `CanonView.xaml.cs.OnIndexRebuilt`, which calls `Dispatcher.BeginInvoke`) — but that defensive Dispatcher hop is the View's, not the service's. Future subscribers (the rest of the app) will get hit on a background thread the first time Rebuild is invoked from one. Either marshal via a captured `SynchronizationContext` (the same pattern `NAudioPlayerService` uses) or document that subscribers must dispatch themselves.

### M64. `PieceReferenceIndex.TryResolve` is O(subpieces × depth) per call
[PieceReferenceIndex.cs:619-620](src/CDArchive.Core/Services/PieceReferenceIndex.cs:619) — at every subpath segment, `subpieces.FirstOrDefault(s => StrictSubpieceMatch(s, segment)) ?? subpieces.FirstOrDefault(s => LooseSubpieceMatch(s, segment))`. Two linear scans per segment, each calling `NormalizeTitle` (4 string replaces) on the segment and on each candidate's `Title`/`DisplayTitle`/`SubpieceDisplayTitle`. For a 100-movement opera that's 200+ string allocations per segment. Pre-index each piece's subpieces by NormalizeTitle once at Rebuild time and the per-segment cost drops to dictionary lookup.

### M65. `ItunesImportInference.ComposerWithDatesRegex` doesn't accept em-dash year separator
[ItunesImportInference.cs:17](src/CDArchive.Core/Services/ItunesImportInference.cs:17):
```csharp
@"^\s*(?<name>.+?)\s*\((?<birth>\d{3,4})\s*[–\-]\s*(?<death>\d{3,4})?\s*\)\s*$"
```
Accepts en-dash (`–`, U+2013) and ASCII hyphen (`-`) but not em-dash (`—`, U+2014). iTunes can produce em-dash in copy-pasted composer fields (especially when the source was Wikipedia or autocorrected Word). If the field is `"Puccini, Giacomo (1858—1924)"`, the regex falls through to `ParsePrincipalSegment`'s fallback `new ParsedComposer(text, null, null)` — silently dropping both birth and death years. The user sees the composer record imported without dates, with no indication of why. Add `—` (em-dash) to the character class, and any other Unicode dash variants (figure dash, horizontal bar) that might appear.

### M66. `NormalizeContributorName` heuristic mangles compound surnames
[ItunesImportInference.cs:104-120](src/CDArchive.Core/Services/ItunesImportInference.cs:104) — comment acknowledges it: "wrong for compound surnames like 'van Beethoven' or 'De Sabata'". For a contributor like `"Sir Charles Mackerras"`, the code yields `"Mackerras, Sir Charles"` — wrong (`Sir` is an honorific, not a given name). For `"Anne-Sophie Mutter"`, yields `"Mutter, Anne-Sophie"` — correct by accident.

The known cases the user has to manually fix on every import: `"van Beethoven"`, `"De Sabata"`, `"Da Capo"`, `"von Karajan"`, `"Le Roux"`, `"di Stefano"`, `"des Prés"`. For a classical archive these aren't edge cases — they're staples. Worth either a lookup table of known particles (`van`, `von`, `de`, `da`, `du`, `le`, `la`, `di`, `des`, `del`) so the surname extends backward past them, or surfacing the parsed contributor in a confirmation UI before commit.

### M67. `ParseTrackName`'s `". "` split mishandles `"St. Peter"` / `"Mt. Etna"` / `"No. 5"`
[ItunesImportInference.cs:163, comment at :159-161](src/CDArchive.Core/Services/ItunesImportInference.cs:163) — the doc acknowledges this and points the user at manual post-import edits. But the affected names recur often in classical: `"St. Paul's Suite"` (Holst), `"St. Anthony Chorale"` (Brahms), `"St. Matthew Passion"` (Bach), `"Mt. Tabor"` (Liszt's *Années de pèlerinage*), `"No. 5 Andante"` if iTunes encodes movement numbers as `"No. 5"`. Each silently over-splits the path into multiple subpieces.

Fix: tokenize on `". "` only when the preceding char isn't a known abbreviation prefix (`St`, `Mt`, `No`, `Op`, `Vol`, `M`, `D`, `K`, `BWV` — the catalog abbreviations); or use a more conservative split like `". "` followed by an uppercase letter to suggest a sentence boundary. Or accept the limitation and add a "see parsed-as-X, edit Y" preview in the import VM (M31 territory).

### M68. No handling of `&`-joined composers ("Lennon & McCartney", "Gilbert & Sullivan")
The composer field regex ([:17](src/CDArchive.Core/Services/ItunesImportInference.cs:17)) only matches the `"Name (YYYY-YYYY)"` shape. Joint composers without dates and without `compl.`/`arr.` role markers (e.g. `"Gilbert & Sullivan"`, `"Rodgers & Hammerstein"`) bypass the regex, fall through to `ParsePrincipalSegment`'s fallback, and create a single `CanonComposer` named `"Gilbert & Sullivan"`. Subsequent works by either composer alone won't match. Detect `" & "` / `" and "` separators at the composer field level and either split into principal + contributor with a `coauthor` role or surface in the import preview.

### M69. `CanonDbSeeder.SeedAlbums` has no album dedup
[CanonDbSeeder.cs:511-619](src/CDArchive.Core/Data/CanonDbSeeder.cs:511) — every album in the input JSON inserts a fresh `AlbumRow`. Two JSON entries with the same `(Label, CatalogueNumber)` both both set → the filtered unique index `ux_albums_label_catalogue_number` rejects the second on `SaveChangesAsync`. Since this is the third sequential SaveChanges (per H43), composers and pieces have already committed; the failure leaves a partial-seed DB. Either dedup at the input layer or pre-check for collisions and report them in `SeedResult.UnresolvedRefs`-style format.

### M70. `CanonDbSeeder.SeedAsync` has no `CancellationToken`
[CanonDbSeeder.cs:45-64](src/CDArchive.Core/Data/CanonDbSeeder.cs:45) — the seeder is interactive (CLI tool), so the only way to abort a long seed is Ctrl+C (process kill). That kill happens at any point inside the recursive `MapPiece` walk or the `SeedAlbums` loop, leaving a partial-state DB (per H43). Plumb a `CancellationToken` from the tool's `Main` (where the user's Ctrl+C is captured by the host) through `SeedAsync` and check it before each `SaveChangesAsync` and inside the album loop.

### M71. `ResolvePieceRefs` description-fallback is lossy for multi-ref tracks
[CanonDbSeeder.cs:689-694](src/CDArchive.Core/Data/CanonDbSeeder.cs:689):
```csharp
if (track.PieceRefs!.Count > 0 && trackRow.PieceRefs.Count == 0 &&
    string.IsNullOrWhiteSpace(trackRow.Description))
{
    trackRow.Description = track.PieceRefs[0].DisplaySummary;
}
```
When a track has multiple refs that all fail to resolve, only `PieceRefs[0].DisplaySummary` ends up in the description. The other refs vanish (they're in `SeedResult.UnresolvedRefs` so visible in the report, but not in the persisted row). Either join all DisplaySummary values with `" / "` (mirroring how the runtime joins multi-ref display) or write each ref as a comment in a JSON-extra column.

### M72. `MainViewModel` navigation methods fire-and-forget `LoadDataCommand`
[MainViewModel.cs:57, :74, :83, :92, :109](src/CDArchive.App/ViewModels/MainViewModel.cs:57) — every navigation that triggers a load uses `_ = ...Command.ExecuteAsync(null);`. Same pattern as C14 (PickListsViewModel.ApplyRenames). The receiving VM's `LoadDataAsync` typically has its own `try { ... } catch (Exception ex) { StatusMessage = ... }` (the C3 broad-catch pattern), so most exceptions surface in the destination view's status bar — but anything that escapes that catch (an `OperationCanceledException`, a bug in the catch arm itself, an exception from `IsLoading` setters) vanishes silently. Combined with C4 (no logging), the developer has no record. Either await the load (which means making the Navigate command async — currently they're sync per CLAUDE.md's "AsyncRelayCommand silent no-ops" lesson) or wrap the fire-and-forget in `.ContinueWith(t => _logger.Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted)`.

### M73. `MainViewModel` re-fires `LoadDataCommand` on every nav click — including the active tab
[MainViewModel.cs:51-110](src/CDArchive.App/ViewModels/MainViewModel.cs:51) — `NavigateToCanon` / `NavigateToAlbums` / `NavigateToTracks` / `NavigateToItunesImport` / `NavigateToPickLists` always execute `_ = ...LoadDataCommand.ExecuteAsync(null)`. Clicking the radio button for the currently-active screen re-fires the load, wasting a full database round-trip (full-graph album load per C11 is multi-second on a large catalogue). The `[ObservableProperty]` setter for `CurrentView` *does* short-circuit when the value is unchanged, but the unconditional `ExecuteAsync` call below it doesn't.

Fix: guard with `if (CurrentView == _albumsViewModel) return;` (or equivalent for IsCanonViewActive) before the load. Or add a `LoadIfStale()` helper to each child VM that checks an internal "last loaded" timestamp and skips the work.

### M74. `MainViewModel` keeps navigation state in three independently-set properties
[MainViewModel.cs:17-24, :52-110](src/CDArchive.App/ViewModels/MainViewModel.cs:17) — each `Navigate*` method assigns `IsCanonViewActive`, `CurrentView`, and `CurrentViewTitle` separately. Three sites per navigation, eight navigations = 24 hand-paired assignments. Adding a new screen requires touching at least three properties consistently; forgetting one (e.g. omitting the title) results in inconsistent UI state.

Cleaner: an `enum AppScreen { Canon, Settings, Albums, Tracks, ItunesImport, ImportExport, PickLists }` + a single `ActiveScreen` property with computed `IsCanonViewActive` / `CurrentView` / `CurrentViewTitle` getters that switch on it. Adding a screen becomes one enum value + one switch case.

### M75. `NewAlbumViewModel.OnAlbumNameChanged` runs a sync filesystem scan per keystroke
[NewAlbumViewModel.cs:36-40](src/CDArchive.App/ViewModels/NewAlbumViewModel.cs:36) → `CheckDuplicates()` → `_duplicateDetectionService.FindPotentialDuplicates(AlbumName)`. The dedup service is sync (per L24) and enumerates the entire archive root with N Levenshtein computations per call. As the user types `"Abbey Road"`, the dedup runs 10 times (one per character) — each scan is the full archive. For a 3,000-album library on a slow drive, every keystroke blocks the UI for hundreds of ms.

Fix: debounce the rebuild (e.g. 250-300ms after last keystroke) and make `FindPotentialDuplicatesAsync` async (per L24).

### M76. `ImportExportViewModel.RestoreFromJsonAsync` only restores composers and pieces — misleadingly named "Restore"
[ImportExportViewModel.cs:193-241](src/CDArchive.App/ViewModels/ImportExportViewModel.cs:193) — handles `composersPath` and `piecesPath` only. Albums, pick lists, and loose tracks are not touched. CLAUDE.md's documented recovery path covers all subsystems; this UI button restores only two of them. After a "Restore" the user has stale albums / picklists / looseTracks that may not match the freshly-restored composers and pieces. Either expand the dialog to cover all subsystems or rename the button (e.g. "Restore composers + pieces") to set correct expectations.

### M77. `ImportExportViewModel.ImportPiecesAsync` re-imports null-composer pieces on every run → silent duplicates
[ImportExportViewModel.cs:170-172](src/CDArchive.App/ViewModels/ImportExportViewModel.cs:170):
```csharp
var toAdd = incoming
    .Where(p => p.Composer == null || p.Title == null || !existingKeys.Contains(PieceKey(p)))
    .ToList();
```
A piece with null Composer (e.g. the `(Various)` sentinel collaborative work, per the stashed work mentioned in CLAUDE.md) or null Title is **always** added — never matched against existing. Re-importing the same file creates duplicate `L'éventail de Jeanne` rows every time. Either fold null Composer + Title into the key (`PieceKey` returns `"|"` for both-null, making them collide so only the first is added) or use a stricter equality check including catalog info + composers list.

### M78. `SettingsViewModel.Save` doesn't notify other VMs that settings changed
[SettingsViewModel.cs:43-59](src/CDArchive.App/ViewModels/SettingsViewModel.cs:43) — mutates `_settings.ArchiveRootPath`, `FfmpegPath`, `Mp3Bitrate`, `PreferredAudioFormat` and calls `Save()`. Other VMs holding the same `IArchiveSettings` singleton (PlayerViewModel via `_settings.PlayerVolume`, ArchiveAudioLocator via `_settings.ArchiveRootPath`, FfmpegConversionService via `_settings.FfmpegPath`/`Mp3Bitrate`) have no event to subscribe to. They see the mutation on next read, but cached state doesn't refresh.

Concrete symptom: change the archive root in Settings; the locator picks up the new root on next `Resolve` (good — it reads each time), but `PlayerViewModel.CurrentFilePath` for the playing track still references the old root. Add `event Action? SettingsChanged;` on `IArchiveSettings`, raise from `Save()`, subscribers refresh.

### M79. `CompositeCatalogueReference._lastSourceUsed` is mutable shared state on a Singleton — racy
[CompositeCatalogueReference.cs:15-21, :33-43](src/CDArchive.Core/Services/CompositeCatalogueReference.cs:15) — `_lastSourceUsed` is a private mutable field on a Singleton-registered service. Each lookup sets it to `null` at the start, then to the source name on hit. Two concurrent callers race:
- Caller A enters `LookupComposerAsync("Bach", null)` and clears `_lastSourceUsed = null`.
- Caller B enters `LookupComposerAsync("Mozart", null)`, also clears, then finds iTunes hit → sets `_lastSourceUsed = "iTunes"`.
- Caller A's local lookup hits, sets `_lastSourceUsed = "Local Catalogue"`.
- The thread that read `LastSourceUsed` to display in `CatalogueViewModel.ReferenceSource` ([:114](src/CDArchive.App/ViewModels/CatalogueViewModel.cs:114)) gets whichever finished last — possibly the wrong source for the displayed result.

Today's call site (`CatalogueViewModel.LoadTagsAsync`) is single-threaded UI work, so this hasn't surfaced. But the moment a future batch import or parallel cataloguing pass uses the composite reference, the display becomes nondeterministic. Fix: return `(result, sourceName)` tuples from each `Lookup*Async` instead of stashing on the singleton, or use `AsyncLocal<T>` if the shared-singleton-state API needs to stay.

### M80. `CompositeCatalogueReference` lookup loop aborts on the first throw — doesn't try the next source
[CompositeCatalogueReference.cs:34-42](src/CDArchive.Core/Services/CompositeCatalogueReference.cs:34) — `foreach` over sources with no try/catch. If iTunes XML parsing throws (corrupt library file), MusicBrainz is never consulted. Today MusicBrainz's swallow-everything `catch` (C9) hides the symptom on that source, but a future Local catalogue that throws (DB locked) would prevent both iTunes and MB from running.

Fix: wrap each `await source.LookupXxxAsync(...)` in try/catch, log the failure (per C4), continue to the next source. The user gets MusicBrainz's result instead of an exception, with a status note that iTunes was skipped.

### M81. `ICatalogueReference` has no `CancellationToken` parameter
[ICatalogueReference.cs:14-15](src/CDArchive.Core/Services/ICatalogueReference.cs:14) — `Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null);` — no token. So a chain that falls through to MusicBrainz waits for the full 1-second rate-limit + HTTP round-trip with no way to abort. A user closing the dialog mid-lookup or cancelling a batch import leaves the chain running. Add `CancellationToken ct = default` to both interface methods, propagate through implementations.

### M82. `AlbumScaffoldingService.CreateAlbumStructure` doesn't validate the album name
[AlbumScaffoldingService.cs:27-32](src/CDArchive.Core/Services/AlbumScaffoldingService.cs:27) — accepts any string and passes it straight to `CreateDirectory`. Names that fail silently or with confusing errors:
- **Windows reserved names**: `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9` — directory creation succeeds on some FS but Windows itself refuses to open them.
- **Invalid characters**: `\`, `/`, `:`, `*`, `?`, `"`, `<`, `>`, `|` — common in classical titles ("Beethoven: Complete Symphonies", "Mahler / Bernstein"). `CreateDirectory` throws `ArgumentException`/`IOException` with messages that don't mention which character is bad.
- **MAX_PATH overflow**: archive root + album name + `\Disc 12\FLAC\` can exceed 260 chars without long-path support enabled. Silent truncation or PathTooLongException.
- **Trailing space or dot**: `"Beethoven 9 "` and `"Beethoven 9"` create different folders on case-preserving FS; Windows may also strip the trailing space silently.

Add a pre-validation step (`Path.GetInvalidFileNameChars()` check, reserved-name lookup, length cap) before `CreateDirectory` and surface a specific error.

### M83. `AlbumScaffoldingService.CreateAlbumStructure` doesn't validate `discCount`
[AlbumScaffoldingService.cs:27-82](src/CDArchive.Core/Services/AlbumScaffoldingService.cs:27) — `discCount = 0` skips both branches (no discs created, but the album folder + AlbumInfo with empty Discs returned). `discCount = -1` falls into the `else` branch with a no-op for loop. `discCount = int.MaxValue` loops 2 billion times creating directories until the FS gives up. Add `if (discCount < 1) throw new ArgumentOutOfRangeException(nameof(discCount));` and a sensible upper bound (50 covers any real classical box set).

### M84. `AlbumScaffoldingService.GetDiscFolderName` pads to 2 digits regardless of disc count — mixed padding at 100+
[AlbumScaffoldingService.cs:21-25](src/CDArchive.Core/Services/AlbumScaffoldingService.cs:21) — `if (totalDiscs >= 10) return $"Disc {discNumber:D2}";`. For totalDiscs=10..99: `Disc 01`...`Disc 99`, consistent. For totalDiscs=100+: `Disc 01`...`Disc 99`, `Disc 100` — mixed padding within one set, which `ArchiveScannerService.ValidateDiscNaming` (per M38) would flag as a warning. Bach cantata complete editions can run 50-60 discs, so 100+ isn't a common case but isn't unheard of either. Compute the digit count from `totalDiscs.ToString().Length` and pad to that width.

### M85. `ConversionStatusService` detects missing MP3s by filename only — doesn't catch stale or corrupted MP3s
[ConversionStatusService.cs:14-39](src/CDArchive.Core/Services/ConversionStatusService.cs:14) — checks `HashSet` of MP3 filename stems against FLAC filename stems. "Missing" means "no MP3 file with this name exists". Cases the service silently misses:
- **Stale MP3**: FLAC re-ripped (different audio, same filename), MP3 not regenerated. Service reports MP3 exists; user listens to the old version.
- **Corrupted MP3**: A truncated or zero-byte MP3 exists. Service reports it as present.
- **Wrong-format MP3**: A file named `01.mp3` that's actually a WAV / FLAC / placeholder. Same — present-by-name, not validated.

For the user's actual workflow (re-rip an album with a different copy of the CD, regenerate MP3s), the second-rip's stale MP3 would silently survive. At minimum, compare mtime (FLAC mtime > MP3 mtime ⇒ stale) and surface both "missing" and "stale" counts. Filename-only semantics worth documenting either way.

### M86. `PiecesWindow.xaml.cs` uses `async void` event handlers
[PiecesWindow.xaml.cs:212, :230, :288](src/CDArchive.App/Views/PiecesWindow.xaml.cs:212) — `OnNewPieceClick`, `OnPieceDoubleClick`, and `OnDeletePieceClick` are all `async void`. Canonical hazard: exceptions thrown inside `async void` propagate to `SynchronizationContext.Current`'s unhandled-exception handler and either crash the app or get swallowed silently. The bodies call `await SaveAllAsync()` (which itself runs two save commands per H43/C5), so any save failure becomes an unhandled exception. The rest of the codebase correctly uses `async Task` for VM commands; the event-handler convention (`async void` is the only signature WPF event hooks accept) is unavoidable here, but the body should be wrapped in `try { ... } catch (Exception ex) { /* log + status */ }` or factored into an `async Task` helper that's invoked via `_ = HandleClickAsync()` with explicit exception handling.

### M87. `AlbumFieldPropagator` uses `JsonSerializer.Serialize` for performer-list equality AND for cloning
[AlbumFieldPropagator.cs:75-83](src/CDArchive.Core/Helpers/AlbumFieldPropagator.cs:75):
```csharp
public static List<AlbumPerformer>? ClonePerformers(List<AlbumPerformer>? src)
{
    if (src is null || src.Count == 0) return null;
    var json = JsonSerializer.Serialize(src);
    return JsonSerializer.Deserialize<List<AlbumPerformer>>(json);
}

private static string FingerprintPerformers(List<AlbumPerformer>? src) =>
    src is null || src.Count == 0 ? "" : JsonSerializer.Serialize(src);
```
Same pattern as M29 (`TrackEditorWindow` uses JSON fingerprinting for mixed-state detection). Two problems:
1. **Brittle equality.** `[A, B]` and `[B, A]` have different fingerprints — a no-op reorder triggers "performers changed" and overwrites every track. Adding a new field to `AlbumPerformer` invalidates every cached fingerprint and forces a "changed → push" on the next save.
2. **Allocation churn.** Every editor-open serializes the performer list once; every editor-save serializes it again to compare; cloning runs full serialize+deserialize per track on the album. For a long-credit album (orchestra + multiple soloists + conductor) × tens of tracks, that's hundreds of JSON allocations per save.

The clone case is *somewhat* defensive (JSON-roundtrip preserves any future `AlbumPerformer` field automatically — relevant to H31/H44's concerns about Person/Ensemble FK fields being lost on manual copies). The fingerprint case has no such excuse — use a structural `IEqualityComparer<List<AlbumPerformer>>` instead.

### M88. `FileSystemService.GetDirectoryName` returns `Path.GetDirectoryName(path)!` — silent NRE risk
[FileSystemService.cs:18](src/CDArchive.Core/Services/FileSystemService.cs:18):
```csharp
public string GetDirectoryName(string path) => Path.GetDirectoryName(path)!;
```
`Path.GetDirectoryName` legitimately returns null for root-only inputs (`"C:\"`, `"/"`, `""`) and for null input. The null-forgiving operator silences the compiler warning but doesn't prevent the NRE at runtime. Any caller that does `var dir = _fs.GetDirectoryName(somePath); _fs.DirectoryExists(dir);` against a root-path NREs deep in the call stack, far from the bad input.

The interface declares the return as `string` (non-nullable), so the contract is "always returns a non-null string" — but the implementation can't honour that without throwing on root inputs. Either:
1. Change the return type to `string?` and require callers to handle null.
2. Throw `ArgumentException` for root-only inputs explicitly with a clear message ("path has no parent directory").

Pairs with H8 / H28 — FileSystemService is the lowest layer of the I/O stack; surprising failure modes here amplify upward.

---

## 🟢 Low

### L1. `ServiceProvider` is a `public static` field with `null!`
[App.xaml.cs:10](src/CDArchive.App/App.xaml.cs:10) — common-but-ugly pattern. A `MainWindow.GetService<T>()` extension would be cleaner. The null-forgiving operator hides startup races.

### L2. `MainWindow.PreviewKeyDown` reaches into `MainViewModel.PlayerViewModel`
[MainWindow.xaml.cs:33-36](src/CDArchive.App/MainWindow.xaml.cs:33) — works, documented. Just noting the cross-VM coupling pattern is repeated elsewhere (`TracksViewModel` ctor-injects `AlbumsViewModel`), inviting circular references.

### L3. `ItunesProbe` and `MigrateIsProvisional` tooling projects may be obsolete
Both in `tools/`. `MigrateIsProvisional`'s functionality is now in `ApplySchemaUpgradesAsync` — shouldn't be needed for new installs. Delete or document as historical-reference-only.

### L4. The 17 `*_wpftmp.csproj.AssemblyInfo.cs` files in `obj/` indicate IDE temp-files leaking out
Caused by WPF's temp-file build pattern (esp. VS hot-reload). Add `*_wpftmp.csproj` to .gitignore (the source one IS in git status) and consider `obj/` cleanup before commits.

### L5. `App.xaml.cs` writes `startup_error.txt` to the assembly directory
[App.xaml.cs:43](src/CDArchive.App/App.xaml.cs:43) — if installed to `Program Files`, that directory isn't writable for non-admin users. Use `Environment.SpecialFolder.LocalApplicationData` instead.

### L6. The repo has no CI configuration
No `.github/workflows/`, no Azure pipeline, no GitLab CI. A "build + test on push" workflow would catch regressions before they hit master. Even a 20-line `dotnet test` action.

### L7. `Scripts/` directory contains ad-hoc Python scripts with no documentation
`scripts/__pycache__/` in git status suggests they're used. `extract_mahler.py`, `generate_beethoven_symphonies_albums.py`, etc. Not under tests, version-pinned, or referenced from CLAUDE.md. Opaque to a new contributor.

### L8. No assembly version / app version visible anywhere
For crash reports, knowing which build is running matters. Set `<Version>` in the .csproj, surface in an About dialog or window title.

### L9. The seeder tool's output goes only to stdout
Recovery operations are critical; a dual-output (stdout + timestamped log in `data/logs/`) would help debug "what did the seeder do last Tuesday?" Closely related to M22.

### L10. `ArchiveSettings` defaults to `@"D:\CD archive"`
Defaults fine, but first run on a different machine creates a settings.json with the default that doesn't exist. Prompt on first run or detect-and-warn.

### L11. `AlbumEditorWindow` populates lists via `ItemsSource = null; ItemsSource = _list`
[AlbumEditorWindow.xaml.cs:228-229](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:228), many others. Known WPF workaround for "ItemsSource doesn't notice list contents changed". `ObservableCollection<T>` would notice changes without this.

### L12. Multi-edit "Mixed" sentinel is a magic-string `"Mixed"`
[AlbumEditorWindow.xaml.cs:674-675](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:674):
```csharp
if (_mixedFields.Contains(fieldName) &&
    (newValue == "Mixed" || string.IsNullOrEmpty(newValue)))
```
If a user types `Mixed` as a real title, it's silently dropped. Use a sentinel constant + the placeholder's marker visibility instead.

### L13. The seeder uses `args.Contains(...)` for option parsing
[Program.cs:46-52](tools/CDArchive.Tools.SeedDb/Program.cs:46) — fine for 4 flags. If it grows, use `System.CommandLine`.

### L14. `MusicBrainzReference` URL-encoding uses `HttpUtility.UrlEncode` (System.Web)
[MusicBrainzReference.cs:38](src/CDArchive.Core/Services/MusicBrainzReference.cs:38) — works but `System.Web` is legacy. `System.Net.WebUtility.UrlEncode` or `Uri.EscapeDataString` are modern equivalents.

### L15. `TagParser.CommonDiacritics` mixes French musical terms with composer first names
[TagParser.cs:80-109](src/CDArchive.Core/Services/TagParser.cs:80) — two unrelated dictionaries in one. Split into `FrenchTermDiacritics` and `ComposerFirstNameDiacritics`.

### L16. SeedDb `--apply` flag could match wrong intent
[Program.cs:50-52](tools/CDArchive.Tools.SeedDb/Program.cs:50) — `args.Contains("--apply")` means `--apply` on its own (without `--promote-loose-tracks`) does nothing visible. Emit a warning if `--apply` is passed without a mode that uses it.

### L17. `AlbumEditorWindow.OnTabSelectionChanged` uses `ReferenceEquals` for tab comparison
[AlbumEditorWindow.xaml.cs:316](src/CDArchive.App/Views/AlbumEditorWindow.xaml.cs:316) — works (controls have identity equality by default), but `==` is more idiomatic and safer for future custom-tab subclasses.

### L18. Tests do filesystem cleanup in `finally` but don't fail loudly if it doesn't
[AlbumSaveInPlaceTests.cs:128-132](tests/CDArchive.Core.Tests/AlbumSaveInPlaceTests.cs:128) — `if (File.Exists(dbPath)) File.Delete(dbPath);` is silent on `IOException`. Over time, %TEMP% accumulates orphaned test DBs.

### L19. `TrackEditorWindow.SetOrMixedEditableCombo` is dead code
[TrackEditorWindow.xaml.cs:330-341](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:330) — defined but unused. The album editor has the same helper (used). Either remove or extract to a shared helper if both editors will eventually need it.

### L20. `ItunesImportViewModel` shows the full exception stack trace to the user
[ItunesImportViewModel.cs:156-157](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:156):
```csharp
MessageBox.Show(ex.ToString(), "Import error", ...);
```
`ex.ToString()` includes the full stack trace. Useful for the developer-user this app was built for; ugly for anyone else. Once logging exists (C4), log `ex.ToString()` and show `ex.Message` to the user with a "See log for details" hint.

### L21. `CataloguingService.WriteFileTag` doesn't check writability before opening the file
[CataloguingService.cs:274](src/CDArchive.Core/Services/CataloguingService.cs:274) — `TagLib.File.Create(filePath)` immediately tries to write. If the file has the read-only attribute, is locked by iTunes/Windows Media Player/an antivirus scan, or is on a write-protected drive, the failure surfaces as a TagLib exception with no useful context. A pre-check (`File.GetAttributes` + a quick `FileStream` open-for-write probe) lets the service skip-with-reason rather than throw.

### L22. `ArchiveScannerService.ScanArchiveAsync` computes `hasFlacFolder` / `hasMp3Folder` and never uses them
[ArchiveScannerService.cs:42-43](src/CDArchive.Core/Services/ArchiveScannerService.cs:42) — both locals are assigned and immediately dropped. Dead computation. Remove, or use them (e.g. to skip the disc-scan when neither marker folder is present).

### L23. `ArchiveScannerService.ValidateArchiveAsync` progress reporting is coarse
[ArchiveScannerService.cs:161](src/CDArchive.Core/Services/ArchiveScannerService.cs:161) — `progress?.Report((int)((i + 1) * 100.0 / totalAlbums))` reports an integer percentage. For 3,000 albums that's at most 100 reports across what could be many minutes — the progress bar feels stuck between reports. Use `IProgress<(int done, int total, string currentAlbumName)>` so the UI can show "412 of 3000 — Beethoven Symphonies" with smooth motion.

### L24. `DuplicateDetectionService.FindPotentialDuplicates` is sync and runs on the calling thread
[DuplicateDetectionService.cs:16-49](src/CDArchive.Core/Services/DuplicateDetectionService.cs:16) — `IDuplicateDetectionService.FindPotentialDuplicates` returns `List<string>` (not `Task<...>`), so callers can't even await. The body enumerates the entire archive root and runs Levenshtein per folder. For a 3,000-album archive on a slow drive this hangs the UI thread. Make the interface async and wrap the body in `Task.Run`, or pre-build an index of normalized names once and look up against that on each query (the dedup index is reusable across many lookups in a session).

### L25. `PickListsViewModel.OnSelectedItemIndexChanged` overwrites in-progress edit text
[PickListsViewModel.cs:95-99](src/CDArchive.App/ViewModels/PickListsViewModel.cs:95):
```csharp
partial void OnSelectedItemIndexChanged(int value)
{
    if (value >= 0 && value < CurrentItems.Count)
        EditText = RawNameAt(value);
}
```
If the user is mid-edit in the EditText field and clicks a different row by mistake, their in-progress text is silently replaced. No "dirty" check, no confirm. Either guard with "if EditText was empty or matched the previously-selected name", or prompt on selection change when EditText is dirty.

### L26. `ComposerEditorWindow` and `PickListsViewModel` silently reject duplicate Add — duplication of M42 across editors
- [ComposerEditorWindow.xaml.cs:84-88](src/CDArchive.App/Views/ComposerEditorWindow.xaml.cs:84) (aliases), [:122-126](src/CDArchive.App/Views/ComposerEditorWindow.xaml.cs:122) (catalog prefixes) — clear input, return silently.

Same pattern as M42 (PickListsViewModel.AddItem) and M43 (UpdateItem). Apply a consistent "duplicate rejected: '\<value\>' already exists" status across the editor family. Cheap UX win.

### L27. No accessibility metadata anywhere
None of the XAML files set `AutomationProperties.Name`, `AutomationProperties.HelpText`, or similar on icon-only or pictogram controls. Examples:
- The transport buttons (`⏪`, `▶`, `⏸`, `⏩`, `⏹`) in PlayerBar — screen readers announce "Button" with no role.
- The `↻` refresh buttons across CanonView, AlbumsView, TracksView, ItunesImportView — same.
- The 🔈 volume glyph.
- The expander arrows in CanonView (custom `<Path>` instead of standard expander) — completely opaque to assistive tech.

If the app is single-user and the user doesn't need a11y, fine — but worth noting as a deliberate omission. The remediation cost is small (`AutomationProperties.Name="Play/pause"`).

### L28. No app-level keyboard shortcuts beyond Space
[MainWindow.xaml.cs:27-38](src/CDArchive.App/MainWindow.xaml.cs:27) wires Space → play/pause. Nothing else: no F5 to refresh, no Ctrl+F to focus the filter, no Enter to invoke the default action in lists, no Esc to clear filter, no Ctrl+S in editors (some have OK via Enter via `IsDefault`, but no Save shortcut for the main views). Mouse-only workflow. Add via `KeyBinding`s on the relevant `InputBindings` collections.

### L29. Color converters hardcode colors in C# instead of resolving from resources
- [ConversionStatusToColorConverter.cs:16-21](src/CDArchive.App/Converters/ConversionStatusToColorConverter.cs:16)
- [ValidationSeverityToColorConverter.cs:16-19](src/CDArchive.App/Converters/ValidationSeverityToColorConverter.cs:16)

Hardcoded `Colors.Gray`, `Colors.DodgerBlue`, etc. Changing the conversion status palette means recompiling. Combined with M54's frozen-brush fix, store the colors as `App.xaml` `SolidColorBrush` resources and have the converter look them up via `Application.Current.Resources["StatusBrushPending"]`. Then a theme change touches XAML, not C#.

### L30. `SimpleDbContextFactory` boilerplate duplicated across 9 test files (extension of H17)
Same `private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>` declaration appears in (at least): `AlbumSaveInPlaceTests`, `AlbumIdentityTests`, `AlbumTracksNullableDiscIdMigrationTests`, `AlbumTrackIsStereoTests`, `CanonRejectCascadeTests`, `LooseTrackRoundTripTests`, `TrackCascadeTests`, `SingletonAlbumPromotionTests`, `SqliteRoundTripTests`. Each test file also re-implements `Path.GetTempPath()` + `Directory.CreateDirectory(...)` + `try { Directory.Delete(...) } catch {}` cleanup. A shared `SqliteTestHarness : IDisposable` helper in `tests/CDArchive.Core.Tests.Infrastructure/` consolidates the boilerplate to ~10 lines per test class.

### L31. `PieceReferenceIndex.DistinctContainerCount` allocates two HashSets per call
[PieceReferenceIndex.cs:252-263](src/CDArchive.Core/Services/PieceReferenceIndex.cs:252) — called per visible tree node per refresh (via the badge converter). For a Canon tree with hundreds of visible nodes, hundreds of HashSet allocations per repaint. Pre-compute and cache the distinct-container counts during `Rebuild` (the same loop that builds `_hitsForPiece` can build a `_distinctContainerCount` dict). Saves the allocations and turns the converter into a pure dictionary lookup.

### L32. iTunes contributor roles are stored as abbreviations (`"compl."`, `"arr."`)
[ItunesImportInference.cs:69](src/CDArchive.Core/Services/ItunesImportInference.cs:69) — `boundaries[i].Groups["role"].Value.ToLowerInvariant() + "."`. The role string on the resulting `ParsedContributorCredit` is `"compl."`, `"arr."`, `"orch."`, etc. — abbreviations, with the period. If the editor / Canon UI displays "Other Contributors" with the role inline, the user sees "Alfano (compl.)" rather than "Alfano (completer)". Worth mapping to full role names (or storing the source abbreviation and resolving display at render time) so the UI is readable.

### L33. `CanonDbSeeder.AddMarkers` and `AddSubMarkers` are 99% identical
[CanonDbSeeder.cs:370-388](src/CDArchive.Core/Data/CanonDbSeeder.cs:370) vs [:390-408](src/CDArchive.Core/Data/CanonDbSeeder.cs:390) — same body modulo the target collection parameter. They could be a single recursive method (the only "difference" is that one is called from external scopes and the other from itself). Collapse them.

### L34. `CataloguingRules.HyphenateKeys` doesn't normalize Unicode accidentals
[CataloguingRules.cs:11-30](src/CDArchive.Core/Services/CataloguingRules.cs:11) — regex `\b([A-Ga-g])\s+(flat|sharp)\b` only matches the ASCII spelled-out form. A work title written as `"Symphony in B♭"` (Unicode ♭, U+266D) passes through `HyphenateKeys` unchanged. The runtime resolver's `PieceReferenceIndex.NormalizeTitle` handles the OTHER direction (Unicode → ASCII for lookup), so the resolver finds the piece — but the tag written via `CataloguingService.WriteFileTag` carries the Unicode glyph rather than the canonical hyphenated form, breaking the user's "ASCII tags" convention silently. Either extend the regex to also match `♭`/`♯`, or run a normalize-Unicode-accidentals pass before HyphenateKeys.

### L35. `CatalogueViewModel.PadWorkNumbers` accepts the catalogue size as a `string?` from XAML `CommandParameter`
[CatalogueViewModel.cs:201-218](src/CDArchive.App/ViewModels/CatalogueViewModel.cs:201) — `[RelayCommand] private void PadWorkNumbers(string? maxCountText)`. Parameter comes via XAML `CommandParameter="{Binding ElementName=MaxCountBox, Path=Text}"` (or similar). If the binding ever breaks or the user types non-numeric, the only signal is the StatusMessage "Enter the max count…". Use a proper VM property (`[ObservableProperty] private int _catalogueMaxCount;`) with a numeric input control, then no string parsing in the command body and the command can have a `CanExecute` gate.

### L36. `CatalogueViewModel.ApplyBulkFields` always overwrites `entry.Year` even when `BulkYear` is empty
[CatalogueViewModel.cs:128-150](src/CDArchive.App/ViewModels/CatalogueViewModel.cs:128) — Artist/Album/Genre are gated on `!string.IsNullOrWhiteSpace(BulkX)`, but Year is unconditionally set to `null` when `BulkYear` doesn't parse:
```csharp
int? year = int.TryParse(BulkYear, out var y) ? y : null;
foreach (var entry in Entries) {
    ...
    entry.Year = year;  // always
}
```
A user who fills in BulkArtist/Album/Genre but leaves BulkYear blank silently has `Year = null` written to every track. Inconsistent with the gate on the other fields. Apply the same `!string.IsNullOrWhiteSpace(BulkYear)` guard.

### L37. `AlbumTrackRow.Composer` and `PerformerSummary` recompute per access; `Piece` is precomputed — asymmetric
[AlbumTrackRow.cs:85-112](src/CDArchive.App/ViewModels/AlbumTrackRow.cs:85) — the constructor doc explicitly notes that `Piece` is precomputed so "filter / sort runs read the stored string — no per-row re-resolution". But `Composer` ([:88-96](src/CDArchive.App/ViewModels/AlbumTrackRow.cs:88)) and `PerformerSummary` ([:102-112](src/CDArchive.App/ViewModels/AlbumTrackRow.cs:102)) are getters that do LINQ + Distinct + null checks on every binding read. For the Tracks view's filter / sort cycle, every row's `Composer` getter re-runs per character typed in the filter box. Pre-compute these in the constructor like `Piece` is, or document why they're per-access on purpose.

### L38. `LocalCatalogueReference` has been a `return null;` stub long enough to be permanent
[LocalCatalogueReference.cs:21-29](src/CDArchive.Core/Services/LocalCatalogueReference.cs:21) — both `LookupComposerAsync` and `LookupWorkAsync` return `Task.FromResult<...>(null)`. The class is registered as a Singleton (ServiceCollectionExtensions:21) and chained FIRST in `CompositeCatalogueReference` (so every lookup wastes a virtual call dispatching through it). The comment says "Stub for a future user-maintained catalogue database" — but the canon DB (`CanonComposer`, `CanonPiece`) already IS the user-maintained catalogue, accessed via `ICanonDataService`. The stub has been there since at least the initial commit, with no work toward implementation.

Three options: (1) implement it by querying `ICanonDataService` for composers/pieces; (2) remove it from the chain and the DI container (one less indirection per lookup); (3) leave the stub but add a `// TODO(superseded): the canon DB serves this role` to clarify intent. Either (1) or (2) — leaving a permanent-stub in the chain is YAGNI-style dead weight.

### L39. `ConversionStatusService` returns a tuple — duplicates `MissingConversionItem` in the VM
[IConversionStatusService.cs:7](src/CDArchive.Core/Services/IConversionStatusService.cs:7) returns `Task<List<(AlbumInfo Album, DiscInfo Disc, List<string> MissingMp3s)>>`. The VM then converts to its own `MissingConversionItem` record ([ConversionStatusViewModel.cs:8](src/CDArchive.App/ViewModels/ConversionStatusViewModel.cs:8)) and re-maps the same fields. Define a single `MissingConversion(AlbumInfo Album, DiscInfo Disc, IReadOnlyList<string> MissingMp3s)` record in Core, return it from the service, bind the VM to it directly (or map to a display-only record at the row-projection level). Removes the tuple + duplicate definition.

### L40. No audit timestamps (`Created` / `Modified`) on any row entity
None of the 21 `*Row` classes in `src/CDArchive.Core/Data/` carry a `Created` / `Modified` timestamp column. For a curation database where the user spends months building approved canon entries, knowing "when was this composer last edited?" / "when did I approve this piece?" matters for incident response (e.g. after H42's silent-reset, the user has no log of which entries were originally approved). Adding two timestamp columns to each row (or just to `ComposerRow`, `PieceRow`, `AlbumRow`, `AlbumTrackRow` — the top-level entities) via a base class + EF Core `SaveChanges` interceptor is ~50 lines and unlocks both audit trail and a future "recently edited" filter.

### L41. `PieceSorting.Sort` uses `List<object>` + type-switches instead of a shared interface
[PieceSorting.cs:44-63](src/CDArchive.Core/Models/PieceSorting.cs:44) — merges `CanonPiece` and `CrossComposerSubpieceNode` into `List<object>`, then every accessor (`CatPrefix`, `CatNumber`, `CatSuffix`, `Title`, `Category`, `Year` — six of them) does `switch o { CanonPiece p => ..., CrossComposerSubpieceNode ccn => ..., _ => default }`. Six near-identical switches plus a silent `_ =>` default arm in each (pairs with M55 — same risk of silently mis-routing a new node type).

An `IPieceSortable` interface with the six getters, implemented by both types, would let `Sort(IEnumerable<IPieceSortable>, field)` be properly typed and remove the type-erasure. Same runtime cost, much cleaner contract, compile-time exhaustiveness when a new sortable node type is added.

### L42. `PiecesWindow.OnFilterChanged` drops expansion state on every keystroke
[PiecesWindow.xaml.cs:47-50](src/CDArchive.App/Views/PiecesWindow.xaml.cs:47) — calls `ApplyFilter()` with the default `preserveExpansion: false`. Only the explicit editor-dialog flows (`OnPieceDoubleClick`, `OnNewPieceClick` after edit) preserve. The user types into the filter box; every keystroke collapses the entire tree. Pass `preserveExpansion: true` from the filter handler too, or distinguish "filter-changed" (preserve) from "data-changed" (rebuild) at the call site.

### L43. `AlbumFieldPropagator.Propagate` mutates without returning what changed
[AlbumFieldPropagator.cs:48-68](src/CDArchive.Core/Helpers/AlbumFieldPropagator.cs:48) — `void Propagate(...)`, no return value. After the call, an unknown subset of tracks may have had their SparsCode / IsStereo / Performers overwritten. The caller (AlbumEditorWindow) can't show a "the following N tracks had their performer list replaced" preview or undo summary. For a feature that's deliberately invasive (one album-level edit cascades to every track), a `PropagationResult { TracksUpdated, FieldsChanged }` return type would let the editor surface what just happened.

### L44. `IFileSystemService` is incomplete — `ReadAllText` / `WriteAllText` / `FileExists` / `Delete` etc. are bypassed via direct `System.IO` calls
[IFileSystemService.cs:3-13](src/CDArchive.Core/Services/IFileSystemService.cs:3) declares 8 methods, all focused on directory operations + path helpers. Missing the file-level operations everyone else needs:
- `ArchiveSettings.Save/Load` uses `File.WriteAllText` / `File.ReadAllText` directly.
- `CataloguingService.WriteFileTag` uses `TagLib.File.Create` directly (bypassing the abstraction entirely).
- `ImportExportViewModel.ExportComposersAsync` uses `File.WriteAllTextAsync` directly.
- `NAudioPlayerService.Load` uses `File.Exists` directly.

The abstraction exists for testability (DuplicateDetectionService mocks it via NSubstitute) but is bypassed inconsistently. Result: only some I/O paths are mockable; the rest hit the real filesystem in tests. Expand the interface to cover the missing operations or commit to direct `System.IO` everywhere and remove the abstraction.

### L45. `DispatcherHelper.cs` is dead code — defined but never called from anywhere
[DispatcherHelper.cs:6-19](src/CDArchive.App/Helpers/DispatcherHelper.cs:6) — declares a `static class DispatcherHelper` with one method `RunOnUiThread(Action action)` that wraps `Application.Current.Dispatcher.CheckAccess()` / `Invoke`. A grep for `DispatcherHelper` across the whole `src/` tree returns only the definition file — zero callers.

The helper was probably added for a planned cross-thread refresh that ended up using `Dispatcher.BeginInvoke` inline (e.g. `CanonView.xaml.cs.OnIndexRebuilt` does its own `Dispatcher.BeginInvoke`). Either:
1. **Delete it.** 19 lines of unused code in `Helpers/` confuses new contributors who assume "helper exists, must be the right pattern to use".
2. **Use it.** Replace the inline `Application.Current.Dispatcher.BeginInvoke(...)` sites (CanonView and likely others) with `DispatcherHelper.RunOnUiThread(...)` so the helper has real callers and the call sites are uniform.

Path 2 is also a stepping-stone toward fixing C2 / H7 / M63 (NAudio sync-context capture, `PieceReferenceIndex.Indexed` event fires on calling thread, etc.) — a single chokepoint for "marshal to UI thread" makes those threading fixes one-file edits.

### L46. `ArchiveAudioLocatorTests` defines `FakeSettings` by hand instead of using NSubstitute
[ArchiveAudioLocatorTests.cs:28-37](tests/CDArchive.Core.Tests/ArchiveAudioLocatorTests.cs:28) — 11-line inline class implementing every `IArchiveSettings` property with mutable defaults. Inconsistent with the 3 other test files that already use NSubstitute (`DuplicateDetectionServiceTests`, `AlbumScaffoldingServiceTests`, `ConversionStatusServiceTests`). Either consolidate on NSubstitute (delete FakeSettings, use `Substitute.For<IArchiveSettings>()`) or commit to hand-rolled fakes (and explain why FakeSettings exists when NSubstitute is already a project dep). The cost of inconsistency is mostly cognitive — a new contributor wonders which pattern to follow for the next test.

---

## ⚪ Nit

- **N1.** `CanonComposer.IsProvisional` documentation comment is repeated verbatim 4× in `CanonDbContext.cs`. DRY the comment.
- **N2.** `IDHandle` is `private sealed class { public long Id; }` — could be `public long Id { get; set; }` for consistency with the rest of the codebase.
- **N3.** `ItunesImporter.CaseInsensitivePairComparer.GetHashCode` does `obj.Item1?.ToLowerInvariant()` — `ToLowerInvariant` allocates; use `StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1)` instead.
- **N4.** `PlayerViewModel.SliderValue.set` updates time displays but `OnPositionChanged` sets `SliderValue = ...` directly, bypassing the property's notification path. Subtle — works because the setter is wired through `SetProperty`, but the indirection through one setter doing two things is confusing.
- **N5.** Many `[ObservableProperty]` backing fields use `_filterText = ""` initializer instead of relying on default. Minor inconsistency.
- **N6.** `CanonView.xaml.cs` line 1,436 — the file is large enough that GitHub's diff view paginates. Painful for code review.
- **N7.** `IDbContextFactory<>` is registered without specifying lifetime (defaults to singleton, which is correct for a factory but worth being explicit).
- **N8.** `PieceEditorWindow.cs:67` — `_variants = _piece.Variants?.Select(CloneVariant).ToList() ?? [];` is correct, but markers are reference-shared while variants are cloned. Verify the asymmetry is intentional and document.
- **N9.** `FfmpegConversionService.cs:131-133` — `string.Join(Environment.NewLine, stderr)` could be very long ffmpeg output. Truncate or store only the last N lines.
- **N10.** `TagParser.WorkNumberRegex` replacement `#N` removes the space. Some users prefer `No. 1` to `#1` — could be a settings preference.
- **N11.** Seeder's "Done." line is the only success signal — a checkmark or count summary would be friendlier.
- **N12.** Window titles don't handle the count==1 case: `AlbumEditorWindow.cs:105` (`"Edit 1 Albums"`), `TrackEditorWindow.cs:99` (`"Edit 1 Tracks"`). Pluralize.
- **N13.** `_originalInheritable = AlbumFieldPropagator.Snapshot(_album);` captures at construction, applies at OnSaveClick. If the user opens Editor, leaves it for hours, opens it again (multiple windows), and saves — propagation runs once per window. Probably fine but worth knowing.
- **N14.** `TrackEditorWindow` parses TrackNumber twice — `int.TryParse(...)` in `CommitCurrentTrack` for validation, then `int.Parse(...)` again in `ApplyUiToTrack` ([:447](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:447)). Pass the parsed value through instead of re-parsing.
- **N15.** `TrackEditorWindow._pieceRefs.CollectionChanged += (_, _) => {...}` ([:227-231](src/CDArchive.App/Views/TrackEditorWindow.xaml.cs:227)) is never unsubscribed. Not catastrophic (ObservableCollection has no external root, dies with the window), but pattern-incorrect — and a future change that retains the collection past window-close would leak.
- **N16.** `ItunesImportViewModel.ApplyFilter` calls `.ToLowerInvariant()` on every track's album title on every filter pass ([:205](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:205)). With per-keystroke filtering against 10k+ tracks, that's allocation-heavy. Pre-lowercase the lookup key once.
- **N17.** `ItunesImportViewModel.LoadAsync` sort uses `t.DateAdded ?? DateTime.MinValue` ([:75](src/CDArchive.App/ViewModels/ItunesImportViewModel.cs:75)) — tracks without DateAdded sort *first* (most-recent), grouped with brand-new tracks. iTunes always sets DateAdded so this is benign in practice; document or flip to `DateTime.MaxValue` to push them last.
- **N18.** `CataloguingService.WriteFileTag` stores `tag.Performers = [entry.Artist]` ([:278](src/CDArchive.Core/Services/CataloguingService.cs:278)) — the comma-joined string from `JoinedPerformers` is wrapped back into a single-element array. Round-trip-safe (read joins again), but defeats the structured-multi-value purpose of the ID3 frame. Split on commas (or pass `entry.Performers` as a real list) so downstream consumers see one entry per artist.
- **N19.** `CataloguingService.NormalizeWorkKey` calls `.ToLowerInvariant()` ([:181](src/CDArchive.Core/Services/CataloguingService.cs:181)) which allocates. Called once per entry plus once per group-lookup probe. Use `StringComparer.OrdinalIgnoreCase` keyed dictionaries instead.
- **N20.** `CataloguingService.ReadFileTag` uses `tag.JoinedPerformers ?? ""` and `tag.JoinedGenres ?? ""` ([:229-231](src/CDArchive.Core/Services/CataloguingService.cs:229)). TagLib returns empty strings, not null, for these properties — the `??` operator is dead.
- **N21.** `ArchiveScannerService.ScanDisc` sets `HasFlacFolder = true` / `HasMp3Folder = true` even when the folder exists but is empty ([:94, :109](src/CDArchive.Core/Services/ArchiveScannerService.cs:94)). The validator separately flags empty folders as warnings — but the scanner's `HasXxxFolder` flag still reads `true`. Downstream consumers comparing the two reports could get confused. Set the flag based on `EnumerateFiles(...).Any()` instead.
- **N22.** `ArchiveScannerService.ScanDisc` has two near-identical FLAC/MP3 enumeration blocks ([:92-120](src/CDArchive.Core/Services/ArchiveScannerService.cs:92)). Parameterize: `ScanFormat(disc, discPath, "FLAC", "*.flac", AudioFormat.Flac, disc.FlacTracks);`
- **N23.** `StringSimilarity.LevenshteinDistance` allocates the full `int[m+1, n+1]` matrix ([:27](src/CDArchive.Core/Helpers/StringSimilarity.cs:27)) and has no early-exit. DuplicateDetectionService only cares whether the distance is ≤ 3, so the standard two-row optimization + early-exit when the row's min exceeds the threshold would let dedup short-circuit most non-matches. Matters because dedup runs O(n) Levenshtein calls per query.
- **N24.** `DuplicateDetectionService.FindPotentialDuplicates(string albumName)` has no defensive null check on `albumName` ([:16](src/CDArchive.Core/Services/DuplicateDetectionService.cs:16)). A null input throws `NullReferenceException` deep in `StringSimilarity.Normalize` rather than a clean `ArgumentNullException` at the boundary. Cheap fix, friendlier error.
- **N25.** `PickListsViewModel.SortedEnsembles()` is called in at least 6 places (RefreshCurrentList, RawNameAt, UpdateItem, RemoveItem, SaveAsync, SelectedEnsemble) and re-sorts the list on every call ([:310-311](src/CDArchive.App/ViewModels/PickListsViewModel.cs:310)). Memoize per refresh into a field, or maintain `_ensembles` sorted at all times (it already does via `SortEnsembles()` on add/update — make that the invariant and skip the per-call `OrderBy`).
- **N26.** `PickListsViewModel`'s Add/Update/Remove commands have no `CanExecute` — `AddItem` with empty `EditText` silently no-ops ([:107](src/CDArchive.App/ViewModels/PickListsViewModel.cs:107)). Wire `CanExecute = !string.IsNullOrWhiteSpace(EditText)` so the button greys out instead of pretending to work.
- **N27.** `App.xaml` line 10 has zero-indent on the `<DataTemplate>` for AlbumsViewModel — cosmetic indentation glitch in an otherwise clean file.
- **N28.** `SettingsView.PreferredAudioFormat` ComboBox uses `<svc:PreferredAudioFormat>Flac</svc:PreferredAudioFormat>` literal enum items in XAML ([SettingsView.xaml:106-107](src/CDArchive.App/Views/SettingsView.xaml:106)). Works because of `ToString()` round-trip, but adding a new enum value (e.g. `Wav`) requires editing XAML and C# in lockstep — `ItemsSource="{Binding Source={x:Static svc:PreferredAudioFormatList.All}}"` (or `Enum.GetValues`) would auto-track.
- **N29.** Color converters' `ConvertBack` throws `NotImplementedException` ([ConversionStatusToColorConverter.cs:29](src/CDArchive.App/Converters/ConversionStatusToColorConverter.cs:29), [ValidationSeverityToColorConverter.cs:27](src/CDArchive.App/Converters/ValidationSeverityToColorConverter.cs:27)). Convention is `NotSupportedException` for "this direction isn't meaningful" — `NotImplementedException` reads as "TODO" and invites someone to come back and implement it. `HitCountBadgeConverter.ConvertBack` correctly uses `NotSupportedException` ([:45](src/CDArchive.App/Converters/HitCountBadgeConverter.cs:45)).
- **N30.** `HitCountBadgeConverter.Convert` builds a fresh `$"({count})"` string per invocation ([:41](src/CDArchive.App/Converters/HitCountBadgeConverter.cs:41)). Called per visible tree node per refresh — for a large catalogue that's thousands of string allocations per re-render. Pre-cache common counts (1-100) into a `static readonly string[]`.
- **N31.** `PieceReferenceIndex.NormalizeTitle` always runs 4 sequential `string.Replace` calls ([:786-789](src/CDArchive.Core/Services/PieceReferenceIndex.cs:786)) even when the input has no accidental characters (the common case for English titles). Short-circuit with `if (s.IndexOfAny(new[] { '♭', '♯', 'ᴒB', 'ᴒA' }) < 0) return s.Trim();` — saves three allocations on every title not containing a flat/sharp glyph.
- **N32.** Two `ComposerWithDatesRegex` patterns exist with subtly different escape: [ItunesImportInference.cs:16-18](src/CDArchive.Core/Services/ItunesImportInference.cs:16) uses `[–\-]` (en-dash or ASCII hyphen, in a character class) and [ItunesLibraryReference.cs:15-17](src/CDArchive.Core/Services/ItunesLibraryReference.cs:15) uses `–\-` (en-dash via escape or ASCII hyphen, in a character class). Functionally identical for in-scope dashes today but diverge if either is extended (e.g. one adds em-dash, the other doesn't). Pull into a single shared `CompiledRegex` and reuse.
- **N33.** `ParseComposer` has no max-contributor cap. A malformed input with hundreds of `, compl. ` boundaries creates hundreds of `ParsedContributorCredit` entries. Practical input is 1-2 contributors; an upper bound (e.g. 8) would prevent runaway and surface a warning when hit.
- **N34.** `CanonDbSeeder._markerRowByModel` is populated for both top-level markers and sub-markers ([:386, :406](src/CDArchive.Core/Data/CanonDbSeeder.cs:386)) but `ResolveMarker` only queries it via `leaf.Markers.FirstOrDefault(...)` — never by sub-marker. The sub-marker registration is dead code (works correctly, just unused).
- **N35.** `CanonDbSeeder.RawJson(JsonElement?)` returns `null` only when `!HasValue` ([:814-815](src/CDArchive.Core/Data/CanonDbSeeder.cs:814)). A `JsonElement` of kind `Null` (an explicit `"field": null` in the JSON) returns the literal string `"null"` rather than C# null, which then round-trips as the four-character string. Cluttered but not buggy; check for `ValueKind == Null` if you want cleaner persisted JSON.
- **N36.** `CataloguingRules.MovementDigits` and `WorkNumberDigits` are byte-identical switch expressions ([:127-145](src/CDArchive.Core/Services/CataloguingRules.cs:127)). Collapse to one private helper (or one public method) — the parameter name is the only thing that varies. The same threshold table is also embedded inline in `PadWorkNumber` ([:42-47](src/CDArchive.Core/Services/CataloguingRules.cs:42)) and `PadMovementNumber` ([:66-71](src/CDArchive.Core/Services/CataloguingRules.cs:66)) — three duplicates total.
- **N37.** `ComposerTreeNode.AllItems = new List<object>()` initializer ([:50](src/CDArchive.App/ViewModels/ComposerTreeNode.cs:50)) is dead — immediately replaced by `RebuildAllItems(PieceSortField.Catalogue)` in the constructor ([:59](src/CDArchive.App/ViewModels/ComposerTreeNode.cs:59)). Either drop the initializer or move the default to a `RebuildAllItems()` no-arg overload that the constructor calls.
- **N38.** `AlbumTrackRow.AlbumTitle` returns empty string for loose tracks ([:45](src/CDArchive.App/ViewModels/AlbumTrackRow.cs:45)), but `PieceAlbumsWindow.AlbumTitle` ([Views/PieceAlbumsWindow.xaml.cs:51](src/CDArchive.App/Views/PieceAlbumsWindow.xaml.cs:51)) returns `"(loose track)"` for the same case. Inconsistent loose-track labelling across views — pick one and apply everywhere (probably "(loose track)" since it's distinguishable from a missing-album bug).
- **N40.** `ConversionStatusService.GetMissingConversionsAsync` propagates `ct` to `ScanArchiveAsync` ([:18](src/CDArchive.Core/Services/ConversionStatusService.cs:18)) but never checks it inside the per-album / per-disc loops. Cancellation lag for a large archive: scan finishes, then ~milliseconds of inner-loop work continues even after a Cancel. Add `ct.ThrowIfCancellationRequested();` at the top of the outer `foreach`.
- **N41.** `PieceRow` and `PieceVersionRow` duplicate 15+ scalar fields (`Title`, `TitleEnglish`, `Subtitle`, `Nickname`, `Form`, `Number`, `MusicNumber`, `KeyTonality`, `KeyMode`, `PublicationYear`, `InstrumentationCategory`, `NumberedSubpieces`, `SubpiecesStart`, `Notes`, plus all the `*Json` fields). Adding a new piece-shape field today requires editing both. EF Core's table-per-type pattern is awkward but a `record class` with shared scalar properties or a `[Owned]` embedded type would dedupe — at the cost of more complex EF mapping. Acceptable for clarity today but worth noting as a future maintenance hot-spot.
- **N42.** `PieceMarkerRow` imports `CDArchive.Core.Models.MarkerKind` ([:1](src/CDArchive.Core/Data/PieceMarkerRow.cs:1)) — the Data layer pulls from the Models layer. Usually layering is one-way (Models is the lower layer, Data sits atop). Functionally OK (the enum is a domain concept that both layers legitimately share), but the import direction is unusual. If the project ever introduces a strict layered-dependency analyzer, `MarkerKind` should move to a shared neutral location or the import should be acknowledged with `[assembly: InternalsVisibleTo]` or similar.
- **N43.** `EnsembleNameRow.StartDate`/`EndDate` and `EnsembleMembershipRow.StartDate`/`EndDate` are freeform strings ([:21-24](src/CDArchive.Core/Data/EnsembleNameRow.cs:21), [:25-28](src/CDArchive.Core/Data/EnsembleMembershipRow.cs:25)). SQLite can't sort or range-query them as dates — `ORDER BY start_date` gives lexicographic results that misorder `"1990"` vs `"1989-01-01"`. CLAUDE.md justifies freeform for the composer/person birth/death case (partial dates "YYYY-MM-DD" or year-only), but for tenure/name-validity dates the same compromise costs query power. Consider a `StartDate DateOnly?` + `StartDateNote string?` pair.
- **N44.** `PieceSorting.OrderByTitle` ([:89-91](src/CDArchive.Core/Models/PieceSorting.cs:89)) doesn't tie-break on anything else — just `OrderBy(Title)`. Two pieces with the same `DisplayTitle` (rare but possible: two different operas titled "Don Giovanni", a piece with no catalog/key whose Title matches another's `DisplayTitle`) sort in unstable order. Add `.ThenBy(CatPrefix).ThenBy(CatNumber)` for determinism (the other sort methods all have multi-key tiebreakers).
- **N45.** Naming inconsistency across the codebase: **Catalog** (American) appears in `PieceRow.CatalogSortPrefix`, `CataloguingRules`, `CatalogInfo`, `ComposerCatalogPrefixRow`, etc. **Catalogue** (British) appears in `CanonAlbum.CatalogueNumber`, `PieceSorting.OrderByCatalogue`, the "Catalogue" UI label in CanonView. Mixed within the same domain concept. Pick one and rename in a focused pass (CLAUDE.md predominantly uses "Catalogue" / "catalog" mixed itself).
- **N46.** `PiecesWindow.SaveAllAsync` ([:318-322](src/CDArchive.App/Views/PiecesWindow.xaml.cs:318)) unconditionally calls both `SavePiecesCommand` and `SavePickListsCommand`. The pick-list save covers the case where the editor added a new pick-list value inline — but most save flows don't touch pick lists at all. Wasteful when nothing changed there (and contributes to the C5 cross-save atomicity concern with one more sequential save). Either track a dirty bit on the pick lists or have the editor signal "pick lists changed" via an out-param/event so the save is conditional.
- **N47.** `AlbumFieldPropagator.Propagate` compares SparsCode with `StringComparison.Ordinal` ([:50](src/CDArchive.Core/Helpers/AlbumFieldPropagator.cs:50)). SPARS codes are conventionally uppercase (`"DDD"`, `"ADD"`, `"AAD"`, `"Unknown"`) and the dropdown only emits those, so this is moot today — but `"ddd"` vs `"DDD"` would falsely register as "changed" and trigger a propagation push. Defensive `OrdinalIgnoreCase` would be safer if any non-dropdown entry path is ever added.
- **N48.** `AlbumScaffoldingServiceTests` covers `Disc 01`-`Disc 10` padding ([Services/AlbumScaffoldingServiceTests.cs:41-49](tests/CDArchive.Core.Tests/Services/AlbumScaffoldingServiceTests.cs:41)) but doesn't cover the 100+ disc mixed-padding case from M84. A `[Theory]` row for `(1, 100, "Disc 01")` and `(100, 100, "Disc 100")` would expose the mixed-padding bug as a failing test. Same for `AlbumFieldPropagatorTests` and the M87 reorder-without-semantic-change case (`[A, B]` vs `[B, A]`): not covered, so the JSON-fingerprint brittleness isn't surfaced by tests.
- **N49.** `NAudioPlayerServiceTests` acknowledges in its class comment that actual playback (Play/Pause/PlaybackEnded) "is exercised manually via the UI" ([:9-11](tests/CDArchive.Core.Tests/NAudioPlayerServiceTests.cs:9)). Worth a `[Trait("Category","ManualOnly")]` so the gap is visible in test inventories, and an `[InlineData]`-driven smoke test that asserts the state machine via the `Volume = 0` + `Play()` + `Stop()` sequence (no audible output but exercises the WaveOutEvent lifecycle) would at least catch breakage in the event-pump plumbing.

---

## Areas of strength

For balance — these things are genuinely well-done and shouldn't be touched without good reason:

- **Schema design.** Proper FK cascades, CHECK constraints expressing polymorphic ownership, filtered unique indexes, snake_case for readability. The `ck_album_performers_has_owner` and `ck_piece_*_exactly_one_owner` patterns prevent whole classes of bugs at the DB layer.
- **Load-mutate-save album path.** Natural-key matching at every child level, transactional save, row-ID preservation through content edits. The pattern is reusable for other "replace the parent's children" save sites.
- **`AlbumSaveInPlaceTests`.** Real SQLite, real cleanup, real constraint-violation rollback assertions. The five tests cover the load-mutate-save invariants comprehensively.
- **Schema migration helpers.** `EnsureColumnAsync` and `EnsureColumnNullableAsync` are append-only, PRAGMA-guarded, transactional. The recreate-table dance follows SQLite's recommended recipe with `PRAGMA foreign_key_check` as a sanity gate.
- **`PieceReferenceIndex` set-aggregation logic.** The post-pass that intersects member-album sets so a set's badge only reflects albums containing every member is non-obvious and correct.
- **CLAUDE.md.** The "Lessons Learned" section is exceptional — it's the kind of project documentation that pays for itself within weeks. The pattern of capturing the failure mode AND the fix AND the why is reusable across teams.
- **Scrub coordination in `PlayerBar`.** The three-handler pattern (DragStarted/DragCompleted/PreviewMouseLeftButtonUp) + `IsScrubbing` flag is a thoughtful solution to a fiddly UX problem.
- **NAudio sync-context capture.** The pattern itself is correct (even if C2 flags the singleton-lifetime hazard). `Raise(...)` posting through the captured context is the right shape.

---

## ✅ Retired

Findings addressed and verified. Each entry should be moved here from its original severity section, with a one-line note: `[YYYY-MM-DD] <commit-hash> — <brief description of fix>`. Keeps historical context + rationale visible for revisiting.

### C4. No logging anywhere
[2026-05-18] `rework/logging` — Serilog wired through `Microsoft.Extensions.Logging`. App configures rolling daily file sink at `%LocalAppData%\CDArchive\logs\cdarchive-YYYYMMDD.log` (14-day retention, shared write) plus a Debug-window sink for dev. Unhandled dispatcher + AppDomain exceptions and the startup-error catch all route through `Log.Fatal` + `CloseAndFlush`. `ServiceCollectionExtensions.AddCoreServices` calls `AddLogging()` so `ILogger<T>` is resolvable for every service even when no host providers are wired (tests stay quiet). `SqliteCanonDataService` takes `ILogger<SqliteCanonDataService>` (optional, defaults to `NullLogger<T>` for direct test construction) and logs start + elapsed-ms for each of the five `SaveXxxAsync` entry points. Plumbing locked in by `LoggingPlumbingTests` (3 tests).

### C3. Bare `catch` blocks across VMs and services
[2026-05-18] `rework/barecatch` — All 8 VM-level bare catches (`CanonViewModel` ×4, `AlbumsViewModel` ×2, `TracksViewModel` ×2) replaced with `catch (Exception ex) { _logger.LogWarning(ex, "..."); }`. The catches were all wrapping `PieceReferenceIndex.Rebuild*` calls; each gets a context-specific log message describing which save/load it followed. `CanonViewModel` also surfaces the failure to the user via an appended `" (badge counts may be stale)"` on `StatusMessage`. `CataloguingService.ReadFileTag` (was `static`; promoted to instance) now logs the TagLib failure with the file path before falling back to the filename — the log line is important because the caller may go on to *overwrite* the unreadable file's tags with the filename-derived data. All three VMs and `CataloguingService` take an optional `ILogger<T>` constructor parameter with a `NullLogger<T>` fallback so existing direct-construction tests keep working.

### C9. `MusicBrainzReference.RateLimitedGetAsync` swallows every exception
[2026-05-18] `rework/barecatch` — Replaced the bare catch with three typed catches: `HttpRequestException` (network), `TaskCanceledException` (timeout), and `JsonException` (parse) — each logs Warning with the URL. Non-success HTTP responses now log too: 404 at Information ("no match" is legitimate), 5xx at Warning (special-cased because 503 is what MusicBrainz returns when the 1-req/sec policy is violated — relevant until C10 lands the thread-safe gate), other 4xx at Warning with the status code. Caller behaviour unchanged (still returns `null` for every failure mode), so no behavioural regression — only diagnostics added. Any *other* exception now propagates instead of being silently swallowed: that surface area was always a bug-hiding catch-all and the AppDomain handler will log fatals.

### C12. `CataloguingService.WriteTagsAsync` has no per-file error handling
[2026-05-18] `rework/tag-writes` — `WriteTagsAsync` now returns `Task<IReadOnlyList<WriteResult>>` (one result per file actually attempted: each MP3 plus each matched FLAC sibling) instead of an opaque `int` count. Each per-file failure is captured in the result list and logged as Warning with the file path; the batch never aborts on a single bad file. The single caller (`CatalogueViewModel.WriteTagsAsync`) shows `N succeeded; M failed (see log)` when failures occur. The fan-out logic (one entry → MP3 + optional FLAC sibling) moved into `EnumerateWriteTargets` so it's directly unit-testable. `WriteResult` is a new immutable record in `Core.Models`.

### C13. `CataloguingService.WriteFileTag` rewrites the audio file in place — non-atomic
[2026-05-18] `rework/tag-writes` — Introduced a `TryAtomicWrite(path, mutate)` primitive on `CataloguingService`: `File.Copy(path, tmp)` to a sibling temp file in the same directory (same volume → atomic `File.Move`), invoke the mutation delegate against the temp file, then `File.Move(tmp, path, overwrite: true)`. On any failure (copy, mutation, move) the temp file is best-effort deleted and the original is left bit-identical. `WriteFileTag` is now a thin wrapper that hands the TagLib mutation to `TryAtomicWrite`. A process kill at any point during the write — power loss, OS kill, AV quarantine — leaves the source audio untouched. Locked in by `CataloguingServiceTagWriteTests` (7 tests covering happy path, mutation-throws-preserves-original byte equality, source-missing, no-directory-component, no leaked temp files in any failure path, and the file-list expansion for MP3-only vs MP3+FLAC-sibling vs empty-FLAC-folder shapes). `InternalsVisibleTo("CDArchive.Core.Tests")` added so tests can exercise the primitive without going through TagLib.

### C1. `ServiceProvider` is never disposed → NAudio + settings leak on exit
[2026-05-19] `rework/dispose-service-provider` — `App.OnExit` now disposes the `ServiceProvider` before `Log.CloseAndFlush`. Singletons that implement `IDisposable` (notably `NAudioPlayerService`, which holds `MediaFoundationReader` + `WaveOutEvent` + a `System.Threading.Timer`) get their `Dispose()` called via the DI container's standard cascade. Disposal is wrapped in try/catch so a buggy `Dispose` on one singleton can't block the others or skip the log flush. `NAudioPlayerService.IsDisposed` is exposed as an internal property (the field already existed) so the regression test can assert the cascade fires. Locked in by `NAudioPlayerServiceTests.DisposingServiceProvider_DisposesSingletonPlayer` — builds the same `AddCoreServices()` graph App.OnStartup uses, resolves `IAudioPlayerService`, disposes the provider, and asserts `IsDisposed` flips. Test project gains a `Microsoft.Extensions.DependencyInjection` package reference for the concrete `ServiceCollection` + `BuildServiceProvider`.

### C8 + H15 + H16. `FfmpegConversionService` hardened: argument escaping, source/ffmpeg checks, process timeout, partial-output cleanup, path-segment replacement
[2026-05-19] `rework/ffmpeg-args` — Single `Arguments` string replaced with `ProcessStartInfo.ArgumentList.Add(...)` so per-argument escaping handles quotes/backslashes/spaces in source and target paths (C8). `ConvertFileAsync` now short-circuits to Failed when the source file is missing (no spurious ffmpeg invocation), catches `Win32Exception` on `Process.Start` to produce a friendly "Could not launch ffmpeg" message when the configured `FfmpegPath` is bogus, enforces a 10-minute per-file timeout via a linked `CancellationTokenSource` (kills the ffmpeg tree on expiry), and `File.Delete`s the partial `.mp3` on every failure path so a re-run doesn't see the stub as "already done" (H15). Path derivation moved to a new `DeriveMp3Path` helper that only renames the immediate parent dir when it's `FLAC`/`flac` — never a global string `.Replace("\\FLAC\\", "\\MP3\\")` — so archive roots or filenames containing "FLAC" (e.g. `D:\FLAC\Music\…`) survive intact (H16). `InternalsVisibleTo("CDArchive.Core.Tests")` added so the pure helper can be tested directly. Locked in by 8 new tests in `FfmpegConversionServiceTests`: 5 covering segment-aware path derivation across edge cases (FLAC root segment, filename containing FLAC, case-insensitive match, no FLAC segment at all), 3 covering the process-invocation error branches (missing source, missing ffmpeg with partial-output cleanup, album-path job derivation).

### C5. Cross-save sequences are not atomic (iTunes import + Tracks save)
[2026-05-19] `rework/cross-save-atomicity` — Added `ICanonDataService.SaveBatchAsync(composers?, pieces?, albums?, looseTracks?)` that opens a single `CanonDbContext`, begins one `BeginTransactionAsync`, runs each non-null subsystem inside it, and commits once. Refactored `SqliteCanonDataService`: each `Save*Async` body is now a private `Save*CoreAsync(CanonDbContext db, T input)` returning an `Action` for the post-commit CWT id updates. Public single-subsystem methods stay thin (they own their own `BeginTransaction`/`Commit` for the load-mutate-save paths that already had one). `SaveBatchAsync` is the only path where multiple subsystems share one transaction — a mid-batch failure rolls every preceding write back. Call sites updated: `ItunesImportViewModel.ImportTracksAsync` replaces its 4-call chain with one `SaveBatchAsync(composers, pieces, albums, looseTracks)`; `TracksViewModel.SaveAsync` replaces `_albumsVm.SaveAsync() + SaveLooseTracksAsync(...)` with `SaveBatchAsync(albums, looseTracks)` + a local `RebuildContainers` (drops the redundant fresh `LoadAlbumsAsync` round-trip too). PickLists' fire-and-forget pieces save is the remaining cross-save site and is now C14's territory exclusively. Locked in by 4 new tests in `SaveBatchAtomicityTests`: full 4-subsystem persist, rollback-on-mid-batch-constraint-violation (verifies the baseline `album_tracks.Id` survives byte-identical — proves true tx rollback, not delete-and-replay), null-subset no-op, partial-subset saves only specified. The `CanonDataService` JSON implementation gets a sequential-write stub of `SaveBatchAsync` with a docstring noting that the JSON service is best-effort only (used for export, not runtime), and the architectural guarantee lives on the SQLite path.

### C14. `PickListsViewModel.ApplyRenames` fires `SavePiecesAsync` as discarded Task
[2026-05-19] `rework/picklists-rename-save` — Extended `ICanonDataService.SaveBatchAsync` with an optional `CanonPickLists? pickLists` parameter; extracted `SqliteCanonDataService.SavePickListsCoreAsync` (stages the delete-and-reinsert without flushing) and wired it into the shared transaction in `SaveBatchAsync` ahead of the composers→pieces→albums→loose-tracks chain. Pick-list rows are FK-independent so order doesn't matter on the SQLite side; running first means a piece save in the same batch sees a freshly-renamed value already staged. `PickListsViewModel.ApplyRenames` no longer fire-and-forgets a pieces save — it now returns the rename count and the caller (`SaveAsync`) decides whether to bundle `pieces: _canonVm.Pieces.ToList()` into the same `SaveBatchAsync` call. A piece-side failure now rolls the pick-list change back too; success now waits for both to land before flipping `StatusMessage` to a truthful `"Pick lists saved. Renamed N piece field(s)."`. The `CanonDataService` JSON implementation forwards the new parameter to its sequential `SavePickListsAsync` (best-effort, matching the existing batch stub). Locked in by 3 new tests in `SaveBatchAtomicityTests`: pick-lists-only persists, pick-lists + pieces both land, downstream-failure rolls back the staged pick list too.

### C10 + M16 + M17 + M18. `MusicBrainzReference` hardened: thread-safe rate-limit gate, `IHttpClientFactory`, real User-Agent, retry-on-transient
[2026-05-19] `rework/musicbrainz-ratelimit` — Replaced the `DateTime.UtcNow`-based gate with a `SemaphoreSlim(1,1)` + `Stopwatch` (monotonic, immune to NTP skew). Concurrent callers now queue on the semaphore; each one observes the actual elapsed-tick deficit before firing, so the 1.1-second policy is honoured even under burst (C10). Service now consumes `IHttpClientFactory` and calls `factory.CreateClient(HttpClientName)` per request — the MS-recommended pattern for a singleton consumer with handler rotation. DI registers a named client via `services.AddHttpClient(MusicBrainzReference.HttpClientName, ...)` (M16). User-Agent is built from `AssemblyInformationalVersionAttribute` / `Assembly.GetName().Version`, paired with the real `https://github.com/jamesliddle/CDArchive` contact URL — drops the literal `CDArchive/1.0` placeholder MB threatens to ban (M17). Retry policy: hand-rolled 3-attempt loop with 500ms / 1000ms backoff for 429, 5xx, and `HttpRequestException`; 404 / other 4xx / parse failures return null without retry; cancellation propagates as `OperationCanceledException` (M18). Each retry re-enters the gate so backoff can't bypass the policy. New `Microsoft.Extensions.Http` dependency. Locked in by 11 new tests in `MusicBrainzReferenceTests` covering: gate behaviour (first call immediate, concurrent serialisation), retry behaviour (503-then-200, persistent 503 exhaust, network exception retry, 429 retry), non-retry paths (404, 400, malformed JSON), cancellation, and User-Agent composition. Tests inject a `ScriptedHandler` + `FakeDelayer` so timing is deterministic.

---

## 🟡 Deferred

Findings intentionally not being addressed (architectural cost too high, requires a feature decision the user isn't ready to make, blocked on a prior finding). Move entries here in place rather than retiring them. Each entry should include: `[YYYY-MM-DD] — <reason for deferral>`.

*(none yet)*
