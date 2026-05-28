# CDArchive Project Guide

## Overview

CDArchive is a WPF desktop application (.NET 8, C#) for managing a classical music CD library. The owner has 3,000+ physical CDs, rips them to FLAC using Exact Audio Copy, converts to MP3, and imports into iTunes with strict custom metadata formatting. This application centralizes that workflow and maintains a canonical reference database of classical composers and their works.

The application has three major subsystems:

1. **Archive Management** -- CD ripping workflow, folder scaffolding, duplicate detection, FLAC-to-MP3 conversion, archive validation, and iTunes catalogue integration.
2. **Canon** -- A curated reference database of classical music composers, their works, movements, versions, and related metadata.
3. **Music Player** -- iTunes-style persistent transport bar at the bottom of the main window. Resolves an `AlbumTrack` to an on-disk FLAC or MP3 via convention + override, plays it through NAudio, auto-advances through the album.

---

## Currently in progress / open questions

Per-session handoff. Each session updates this when stopping mid-stream so the next session reads it cold and is up to speed. Empty = no pending state.

- **`rework/ensemble-entry-editor-vm`** branch landed **H13 small-editors slice 4** — `EnsembleEntryEditorWindow` migrated (94→84 lines). Narrow scope: it manages ONLY the Members list of an ensemble `InstrumentEntry` — the parent's Instrument name + IsEnsemble flag are intentionally not editable (H31/H33 contract preserved). New `EnsembleEntryEditorViewModel` has `[ObservableProperty] string EnsembleName` (read-only, OneWay-bound to header label) + `ObservableCollection<InstrumentEntry> Members` + `LoadFromEntry` / `SaveToEntry`. **`SaveToEntry` deliberately does NOT touch Instrument or IsEnsemble** — locked by 2 regression tests covering the H31 + H33 retirements. XAML: `EnsembleNameLabel.Text` gets `OneWay` binding; `MembersList` gets `ItemsSource` + `ItemTemplate` displaying `{Binding DisplayLabel}`. Code-behind: `_members` List + `RefreshMembersList` method retire (ItemsSource binding drives re-render). Add/Remove/MoveUp/MoveDown handlers preserved; MoveUp/Down switched from tuple-swap to `RemoveAt+Insert` (works better with `ObservableCollection` for the WPF ListBox selection-bookkeeping). 10 new tests in `EnsembleEntryEditorViewModelTests`. Total: 932 tests (595 Core + 337 App). **1 small editor remains**: MarkerEditor (88 lines — multiple field types + kind enum). After it lands, H13 is effectively complete. **Action item for the user**: smoke-test — open an ensemble (e.g. string quartet), Add/Remove/Move members + OK → persists; verify Instrument name + IsEnsemble flag preserved across round-trip.
- **`rework/small-editors-bundle-2`** branch landed **H13 small-editors slice 3** — 2 more small dialogs. **PerformerEditor** (52→47 lines, 3 fields, Name required, preserves H31 mutate-in-place contract via dedicated regression test). **SessionEditor** (53→33 lines, 6 fields, no required-field validation — fully blank session is allowed). The Engineers/Producers comma-separated round-trip logic moves into the VM as a `public static SplitNames(string)` helper that's tested directly with 7 edge-case Theory inputs (trim, skip-empty, all-empty, whitespace). 17 new tests in `SmallEditorViewModelsTests`. Aggregate: 105→80 lines code-behind; ~80 lines VMs. Total: 922 tests (595 Core + 327 App). **2 small editors remain**: EnsembleEntryEditor (94 — nested Members list editing), MarkerEditor (88 — kind enum + multiple field types). Each warrants own PR. **Action item for the user**: smoke-test each — Performer: edit name+role+instrument, OK → persists; clear name → MessageBox + Name focused. Session: edit Dates/Venue/Engineers with `"Alice, Bob, Carol"` → re-open shows 3 separate names; clear all fields + OK → no validation, persists with nulls.
- **`rework/small-editors-bundle-1`** branch landed **H13 small-editors slice 2** — 4 small dialogs bundled because they all follow the same shape: model into ctor, 2-4 text/combo fields, OK validates one required field. **VariantEditor** (43→42 lines), **RoleEditor** (45→42), **ComposerCreditEditor** (46→47), **InstrumentEntryEditor** (50→46). Each got a `XxxEditorViewModel` with `[ObservableProperty]` strings + `LoadFromX`/`SaveToX` + `SaveValidationError` enum (`None` + one missing-required-field variant). XAML: TwoWay `Text` bindings on each TextBox / editable ComboBox; ItemsSource (pickLists references) stays in code-behind. Code-behind shrinks to ctor (DataContext + ItemsSource + LoadFromX) + OnOkClick (SaveToX + validation enum → MessageBox + Focus). Private static `NullIfEmpty` retires from each (moved into respective VM). InstrumentEntryEditor preserves the H31 contract: deliberately does NOT touch `IsEnsemble` / `Members` (covered by a regression test). 21 new tests in `SmallEditorViewModelsTests`. Aggregate: 184→177 lines code-behind; ~150 lines VMs. Total: 905 tests (595 Core + 310 App). **4 small editors remain**: PerformerEditor (52), SessionEditor (53) — bundle together next; EnsembleEntryEditor (94), MarkerEditor (88) — each warrants its own PR. **Action item for the user**: smoke-test each — Variant clear description→MessageBox; Role clear name→MessageBox; ComposerCredit clear name→MessageBox; InstrumentEntry clear instrument→MessageBox + verify editing an ensemble entry does NOT lose its Members list.
- **`rework/composer-editor-vm`** branch landed **H13 small-editors slice 1** (Top-5 #1 first slice of the small-editor migrations). `ComposerEditorWindow` was the biggest of the 9 small editors at 189 lines; a new `ComposerEditorViewModel` now owns all field state. 11 `[ObservableProperty]` string fields (Name, SortName, four-part Birth + Death blocks, Notes) + 2 `ObservableCollection<string>` (Aliases, CatalogPrefixes) + `LoadFromComposer` / `SaveToComposer` Load/Save pair + `SaveValidationError` enum (`None` / `MissingName` / `MissingSortName`). XAML: 11 TextBoxes get `Text="{Binding ...}"` TwoWay bindings; both ListBoxes get `ItemsSource="{Binding}"` — `ObservableCollection.CollectionChanged` drives the re-render, retiring the pre-fix `Refresh{Alias|Catalog}List` methods entirely. Code-behind retires `LoadFromComposer` + `SaveToComposer` methods + `_aliases` / `_catalogPrefixes` fields + 2 Refresh methods + `NullIfEmpty` static helper. Add/Remove/MoveUp/MoveDown handlers preserved in code-behind (own the ListBox.SelectedIndex bookkeeping after mutation — pure view-state). `OnOkClick` routes through `_vm.SaveToComposer(_composer)` and switches on the `SaveValidationError` for MessageBox + TextBox.Focus chrome. 15 new tests in `ComposerEditorViewModelTests`. Lines: ComposerEditorWindow.xaml.cs 189→133 (-56); VM 126 lines. Total: 884 tests (595 Core + 289 App). 8 small editors remain: PerformerEditor 52, SessionEditor 53, RoleEditor 45, ComposerCreditEditor 46, InstrumentEntryEditor 50, EnsembleEntryEditor 94, VariantEditor 43, MarkerEditor 88 (~471 lines total). Each follows the same shape; could bundle 2-3 per PR. **Action item for the user**: smoke-test the Composer editor — (1) Edit existing composer → all 11 fields populate; (2) Add/Remove Alias → appears/disappears immediately + persists; (3) Add Catalogue Prefix + MoveUp/Down → order persists; (4) Clear Name + OK → MessageBox + Name focused; (5) Clear SortName + OK → similar; (6) Empty optional fields round-trip to null in JSON.
- **`rework/piece-editor-vm-lists`** branch landed **H13 PieceEditor slice 4** (Top-5 #1 fourth-and-effectively-final slice of the PieceEditor portion of H13). All 8 list-shaped fields move onto `PieceEditorViewModel` as `ObservableCollection<T>` properties: Composers, CatalogEntries, PieceInstruments, Subpieces, Versions, Roles, Markers, Variants. Because PieceEditor has no multi-edit, plain `ObservableCollection<T>` suffices (no `MixedCollection<T>` needed). VM additions: 8 collections + `LoadListsFromPiece(piece)` + `SaveListsToPiece(piece)` + `SeedInheritedComposers(inheritedComposers)` for the subpiece-inherits-parent-credits pattern + private static `CloneVariant` (moved from code-behind). Storage semantics preserved: shallow copy for most lists, deep-clone for Variants (so editor edits don't bleed to source until OK), shared-instance for Markers (Ids stay attached for track-ref resolution), parse/serialize via helpers for Roles + PieceInstruments. Code-behind retirements: 8 `_xxxField` declarations + 6 ctor list-init lines (across both ctors) + CatalogInfo/Instrumentation population in LoadFromPiece + 8 `_piece.X = ...ToList()` save lines + redundant `RefreshXxxList` ctor calls + private static `CloneVariant`. All 8 `_xxx` field references throughout (Add/Edit/Remove/MoveUp/MoveDown + Refresh methods) become `_vm.Xxx`. LoadFromPiece now calls all 8 Refresh methods after the VM populates. 14 new tests in `PieceEditorViewModelTests`. Lines: PieceEditorWindow.xaml.cs 1032→988 (-44); VM 274→382 (+108). Total: 869 tests (595 Core + 274 App). H13's PieceEditor portion effectively complete — the remaining 988 lines are legitimate View concern (ListBox custom-rendering Refresh methods, modal-dialog wiring, MoveUp/MoveDown handlers, dropdown ItemsSource wiring, PieceVersionShuttle integration, OnOk validation, LoadFromPiece/SaveToPiece thin wrappers). Top-5 #1 reframes to **PieceEditor small editors** (ComposerEditor, PerformerEditor, RoleEditor, etc. — the 40-170-line modal dialogs H13 also flagged). **Action item for the user**: smoke-test PieceEditor in all 3 modes — (1) all 8 lists populate on load; (2) Add/Edit/Remove updates UI + persists; (3) MoveUp/MoveDown preserves order; (4) Variants edits don't bleed across instances; (5) Marker Ids preserved across OK; (6) Subpiece on piece with composer X opens with X pre-filled + inherited Other Contributors seeded.
- **`rework/piece-editor-vm-subpieces-numbering`** branch landed **H13 PieceEditor slice 3** (Top-5 #1 third slice). The "Numbered" checkbox + adjacent "Subpieces start at N" number field move onto `PieceEditorViewModel`. VM additions: `[ObservableProperty] bool NumberedSubpieces` (CheckBox.IsChecked TwoWay binding); `[ObservableProperty] string SubpiecesStart = "1"` (TextBox.Text TwoWay); `int EffectiveSubpiecesStart` parses to int with fallback to 1; `bool DefaultNumberedForCurrentCategory` mirrors the model's `EffectiveSubpiecesNumbered` heuristic (Opera → not numbered, everything else → numbered). LoadFromPiece resolves the model's nullable `NumberedSubpieces` to the effective bool at load time. SaveToPiece persists null when matching the category default (keeps JSON clean) and null when SubpiecesStart=1. Code-behind: 2 load lines + 8 save lines + the private `EffectiveSubpiecesStart` property retire; `RenumberSubpieces` reads `_vm.EffectiveSubpiecesStart`; `RefreshSubpieceList` reads `_vm.NumberedSubpieces`; the Checked/Unchecked + TextChanged event handlers stay (fire `RefreshSubpieceList()` to re-render). 29 new tests covering load (4 cases for NumberedSubpieces nullable resolution, Theory for SubpiecesStart), parse helpers (Theory), save normalisation (4 cases for matches-default vs differs-from-default), DefaultNumberedForCurrentCategory (8-case Theory including case-insensitive Opera), 3 round-trip tests. Lines: PieceEditorWindow.xaml.cs 1038→1032 (-6); VM 209→274 (+65). Total: 855 tests (595 Core + 260 App). H13 stays open (slice 4 of N + small editors). Top-5 #1 reframes to slice 4 (list-shaped fields). **Action item for the user**: smoke-test — (1) Chamber piece: checkbox checked by default; (2) Opera: unchecked by default; (3) Toggle "Numbered" → subpiece list re-renders with/without prefixes immediately; (4) Set Subpieces start to 13 → re-open shows 13; (5) Set to 1 or empty → JSON has no `subpieces_start` key.
- **`rework/piece-editor-vm-comboboxes`** branch landed **H13 PieceEditor slice 2** (Top-5 #1 second slice). 5 combobox-driven fields (Composer, Form, KeyTonality, KeyMode, InstrumentationCategory) move onto `PieceEditorViewModel` as plain `[ObservableProperty]` strings — still no MixedField needed since PieceEditor has no multi-edit. **XAML binding by combo type**: 4 editable combos use `Text="{Binding}"` (same as TextBoxes); KeyMode (non-editable, 3 fixed items "" / "major" / "minor") uses `SelectedValuePath="Content"` + `SelectedValue="{Binding KeyMode}"` — retires the pre-fix iteration-to-find-matching-ComboBoxItem block. **Inherited-Composer fallback** (subpiece/version inherits parent's Composer when own is blank) moves into the VM via a new `LoadFromPiece(piece, inheritedComposer)` overload. Code-behind: `LoadFromPiece` switches to the two-arg overload (retires 4 inline `xxxCombo.Text =` + 9-line KeyMode load block); `SaveToPiece` retires the 4 `_piece.X = NullIfEmpty(xxxCombo.Text)` + KeyMode SelectedItem-cast; `UpdateCatalogPrefixDropdown` reads `_vm.Composer.Trim()` instead of `ComposerCombo.Text`. 13 new tests in `PieceEditorViewModelTests`. Lines: PieceEditorWindow.xaml.cs 1056→1038 (-18); VM 153→209 (+56). Total: 826 tests (595 Core + 231 App). H13 stays open (slice 3 of N + small editors). Top-5 #1 reframes to slice 3 (NumberedSubpieces checkbox + SubpiecesStart number field). **Action item for the user**: smoke-test the Piece editor's 5 combos in both modes (Edit Piece, Edit Version) — combos populate correctly on load; typing a new value persists; dropdown picks persist; Composer change re-filters the Catalogue prefix dropdown; KeyMode round-trips correctly.
- **`rework/piece-editor-vm-text-fields`** branch landed **H13 PieceEditor slice 1** (Top-5 #1 first slice of the PieceEditor portion of H13). 10 pure text fields (Title, TitleEnglish, Subtitle, Nickname, Number as string, MusicNumber, PubYear as string, CompYears with JsonElement converter, Notes, VersionDescription) moved to a new `PieceEditorViewModel` as plain `[ObservableProperty]` string fields. **Key simplification vs Album/TrackEditor**: the PieceEditor has **no multi-edit mode** — only one piece/subpiece/version is edited at a time — so the VM doesn't need `MixedField<T>` / `MixedCollection<T>` wrappers. Just plain string properties + `LoadFromPiece(piece)` / `LoadVersionDescription(version)` / `SaveToPiece(piece)` / `SaveVersionDescription(version)`. Integer fields stored as string in the VM, parsed at save time. CompYears (JsonElement?) has a pair of static converters; `StringToCompYears` uses `JsonSerializer.Serialize(s)` for safe escaping (pre-fix `$"\"{...}\""` interpolation broke on embedded quotes — covered by a test). XAML: 10 TextBoxes get TwoWay bindings; both ctors set DataContext = _vm. Code-behind: `LoadFromPiece` / `SaveToPiece` / version-ctor / `CopyPieceToVersion` all retire the corresponding `xxxBox.Text`/parse blocks; combobox + checkbox + list code stays for later slices. 27 new tests in `PieceEditorViewModelTests`. Total: 813 tests (595 Core + 218 App). H13 stays open (slice 2 of N + small editors). Top-5 #1 reframes to slice 2 (Combobox fields). **Action item for the user**: smoke-test the Piece editor in all three modes (New Piece, Edit Piece, Edit Version) — text fields populate from existing piece/version + persist on OK; Number/PubYear handle invalid input gracefully (stored as null); empty optional fields round-trip to null in JSON.
- **`rework/track-editor-vm-save`** branch landed **H13 TrackEditor slice 5** (Top-5 #1 fifth-and-final slice of the TrackEditor portion of H13). `CommitCurrentTrack` + `ApplyUiToTrack` + `CommitLooseTrack` + `SaveMulti` orchestration moved off the TrackEditorWindow code-behind onto three new VM methods: `TrackEditorViewModel.SaveSingle(disc, trackIndex)` validates TrackNumber must parse to positive int, then writes to `disc.Tracks[trackIndex]` or appends a fresh track when `trackIndex >= disc.Tracks.Count` (add-new path); `TrackEditorViewModel.SaveLoose(track)` writes to the loose track in place + forces `TrackNumber=0` + `SessionIndex=null`; `TrackEditorViewModel.SaveMulti(tracks, allLoose)` encodes the multi-edit contract — text fields via `ApplyMixedFieldText`, combos via `!(StartedMixed && IsMixed)`, lists via `ApplyListMulti<T>` with Unanimous-replace / Mixed-append branching. New `SaveValidationError` enum (`None` / `InvalidTrackNumber`); code-behind keeps a thin `HandleSaveValidationError` helper that surfaces the MessageBox + `TrackNumberBox.Focus()`. `OnPrevClick` / `OnNextClick` / `OnOkClick` all route through the VM via the same helper. `ApplyMixedFieldText` / `SkipMixedTextWrite` / `ApplyListMulti` / `NullIfEmpty` all retire from code-behind (moved to VM). Lines: TrackEditorWindow.xaml.cs 854→762 (-92); VM 308→510 (+202 — orchestration + docstrings). 22 new tests in `TrackEditorViewModelSaveTests`. Total: 786 tests (595 Core + 191 App). H13 stays open (PieceEditor + small editors remain). The TrackEditor portion is effectively complete; what stays in the code-behind by design: modal-dialog ownership (Session/Performer/PieceRef sub-editors, PiecePicker), the session combo's view chrome (item building, `_sessionMixedSentinelIndex` position tracking), Cancel rollback (H22), mode-driven visibility properties, audio-overrides browse handlers. Top-5 #1 reframes to **PieceEditor extraction** (1114 lines, 2 ctors — the established MixedField + MixedCollection + StartedMixed + slice-per-PR pattern is directly reusable). **Action item for the user**: smoke-test save flows in all three TrackEditor modes — (1) Single-edit Duration change + OK → persists. (2) Single-edit clear TrackNumber + OK → MessageBox + TrackNumberBox focus. (3) Single-edit Prev/Next with invalid TrackNumber → validation triggers, navigation cancelled. (4) Add-new from album editor → new track appended. (5) Loose-track edit OK → persists; TrackNumber stays 0; SessionIndex stays null. (6) Multi-edit pick SPARS "AAD" → all tracks get it. (7) Multi-edit Mixed Performers + Add → each track keeps originals plus new entry. (8) Multi-edit of two loose tracks → no TrackNumber validation (allLoose flag).
- **`rework/track-editor-vm-lists`** branch landed **H13 TrackEditor slice 4** (Top-5 #1 fourth slice). The two ObservableCollection-backed list fields (PieceRefs, TrackPerformers) move onto the VM via a new `MixedCollection<T>` helper — the list-shaped equivalent of `MixedField<T>`. Unlike AlbumEditor's Performers + Sessions (slice 3), these lists ARE editable in the TrackEditor's multi-edit mode, so they need the same StartedMixed + WasEdited contract that `MixedField<T>` provides for scalars. **New helper**: `src/CDArchive.App/Helpers/MixedCollection.cs` wraps an `ObservableCollection<T> Items` + tri-state. Subscribes to its own `CollectionChanged` to flip `WasEdited` on first user mutation (with `_initializing` guard during `InitUnanimous` / `InitMixed`). Exposes `ShouldWriteOnSave => !StartedMixed || WasEdited`. **VM additions**: `MixedCollection<TrackPieceRef> PieceRefs` + `MixedCollection<AlbumPerformer> Performers`. `LoadSingle`/`LoadNew`/`LoadLoose` use `InitUnanimous(...)`; `LoadMulti` uses an internal `InitListMixed<T>` that JSON-fingerprints each track's list (order-sensitive — positional lists) and either calls `InitUnanimous` with a fresh JSON-deserialized COPY (independent of source — user Add/Remove must NOT mutate source) or `InitMixed()`. **Code-behind retirements**: `_pieceRefs` / `_trackPerformers` ObservableCollection fields retired (now `_vm.PieceRefs.Items` / `_vm.Performers.Items`); `_pieceRefsUntouched` / `_performersUntouched` retired (VM's `WasEdited` covers them); `MarkPerformersTouched` retired (the banner chrome subscribes to VM `PropertyChanged` for `WasEdited` flips); **`_mixedFields` HashSet retired ENTIRELY** — every field on the editor now tracks its own StartedMixed via the VM. `PopulateMultiFields` subscribes `OnPieceRefsWasEditedChanged` / `OnPerformersWasEditedChanged` to hide the Mixed banner on first edit. `SaveMulti` uses `_vm.PieceRefs.ShouldWriteOnSave` / `_vm.Performers.ShouldWriteOnSave` (replaces the dual `!_mixedFields.Contains(name) || !_xxxUntouched`). 19 new tests: 10 `MixedCollectionTests` (Init / mutation / Re-init / ShouldWriteOnSave / PropertyChanged-fires-once) + 9 slice-4 VM tests. Total: 764 tests (595 Core + 169 App). H13 stays open (slice 5 of N + PieceEditor + small editors). Top-5 #1 reframes to slice 5 (Save orchestration → VM methods, mirroring AlbumEditor slice 4). **Action item for the user**: smoke-test the PieceRefs + Performers lists in all three modes — (1) Single-edit: add/remove a piece ref or performer, OK, change persists. (2) Loose-track edit: same. (3) Multi-edit lists differ + leave alone → no track wiped. (4) Multi-edit lists differ + Add → all tracks get the new shared list. (5) Multi-edit lists shared → no banner, idempotent.
- **`rework/track-editor-vm-session-combo`** branch landed **H13 TrackEditor slice 3** (Top-5 #1 third slice). The Session ComboBox now drives off `TrackEditorViewModel.Session : MixedField<int?>`. **Design call**: VM owns the resolved selection (`int?` Value + `IsMixed` flag); code-behind owns the combo items + the sentinel position lookup (pure view chrome). Pre-slice the code-behind tracked `_sessionMixedSentinelIndex` + `_mixedFields.Add("SessionIndex")` to gate the save-skip; post-slice `_mixedFields.Add("SessionIndex")` retires (VM's `Session.IsMixed` covers it) but `_sessionMixedSentinelIndex` stays — `OnSessionChanged` needs the position to decide whether to push to VM or skip. **Design subtlety caught by tests**: a `null` Mixed placeholder collides with the legitimate "(no session)" Value=null — picking "(no session)" wouldn't trip the property-changed setter. Fix: `internal const int SessionMixedPlaceholder = int.MinValue` — distinct from any real index (0..N) AND from null, so user picks reliably trip the setter. VM additions: `MixedField<int?> Session`, `SessionMixedPlaceholder` constant. `LoadSingle` / `LoadNew` / `LoadLoose` use `InitUnanimous(...)` with the appropriate value (`LoadLoose` forces null). `LoadMulti(tracks, placeholder, bool hasSharedSessions = true)` gained an optional param — `!hasSharedSessions` loads Session as Mixed (models the disabled "(multiple albums)" combo state); shared-sessions distinct → Mixed, single-distinct → Unanimous. Code-behind: `PopulateMultiSessionCombo` reads `_vm.Session.IsMixed` to decide whether to append the "Mixed" sentinel ComboBoxItem; new `OnSessionChanged` SelectionChanged handler skips the sentinel position, maps idx==_sessions.Count → Value=null, maps idx<_sessions.Count → Value=idx. `ApplyUiToTrack` reads `target.SessionIndex = _vm.Session.Value` directly (no longer routes through `SessionIndexMapping.ResolveSelection`). `SaveMulti` uses combo contract `if (!(_vm.Session.StartedMixed && _vm.Session.IsMixed)) write`. 11 new tests in `TrackEditorViewModelTests`. Total: 745 tests (595 Core + 150 App). H13 stays open (slice 4 of N + PieceEditor + small editors). Top-5 #1 reframes to slice 4 (PieceRefs + TrackPerformers ObservableCollections to VM). **Action item for the user**: smoke-test the Session combo in all three TrackEditor modes — (1) Single-edit no-session track: open, verify "(no session)" selected, OK → still null; (2) Single-edit track with session, pick "(no session)" → persists null; (3) Add Session in single-edit → new session auto-selects; (4) Multi-edit different sessions → "Mixed" sentinel; pick session 1 → all tracks use it; (5) Multi-edit same session → no Mixed sentinel; (6) Multi-edit across albums → "(multiple albums — cannot edit)" disabled; OK → no changes.
- **`rework/track-editor-vm-comboboxes`** branch landed **H13 TrackEditor slice 2** (Top-5 #1 second slice of the TrackEditor portion of H13). SparsCode + IsStereo now live on `TrackEditorViewModel` as `MixedField<string>` properties using a stable string vocabulary that matches the dropdown items — mirrors AlbumEditor's slice 2. **Design call**: hybrid approach (VM owns state, code-behind imperatively syncs the non-editable ComboBoxes via `SelectionChanged` handlers) rather than pure-binding — non-editable ComboBoxes with dynamically-appended "Mixed" sentinel `ComboBoxItem`s don't fit a clean `SelectedValue` binding. VM additions: `MixedField<string> SparsCode` + `MixedField<string> IsStereo`, sentinel constants `SparsCodeMixedSentinel` / `IsStereoMixedSentinel` (both `"Mixed"`), translator helpers `SparsCodeToString`/`FromString` + `IsStereoToString`/`FromString` (all static so tests run headlessly). `LoadSingle` / `LoadNew` / `LoadMulti` / `LoadLoose` all populate the combo fields. Code-behind: `LoadTrack` / `LoadLooseTrack` / `PopulateMultiFields` drive the combos from VM state via `SparsCodeCombo.SelectValue` / `AppendMixedSentinel` (SparsCode) + new `SetStereoComboFromVm` helper (Stereo). New `OnSparsCodeChanged` / `OnStereoChanged` SelectionChanged handlers push user picks back to the VM; CommunityToolkit's `SetProperty` value-equality check means programmatic syncs don't spuriously trip WasEdited. XAML: TrackSparsCodeBox + TrackStereoBox gain the SelectionChanged handlers. `ApplyUiToTrack` / `CommitLooseTrack` read from VM via `SparsCodeFromString` / `IsStereoFromString`. `SaveMulti` uses the combo contract `if (!(field.StartedMixed && field.IsMixed)) write` — same shape as AlbumEditor slice 4; `_mixedFields.Contains("SparsCode")` / `Contains("IsStereo")` checks retire entirely along with the `SparsCodeCombo.IsMixedSentinelSelected` / `TrackStereoBox.SelectedIndex != 3` checks. 25 new tests in `TrackEditorViewModelTests`. Total: 734 tests (595 Core + 139 App). H13 stays open (slice 3 of N + PieceEditor + small editors). Top-5 #1 reframes to slice 3 (Session combo). **Action item for the user**: smoke-test the SPARS Code + Stereo dropdowns in all three TrackEditor modes — single-edit, loose track, multi-edit (mixed → sentinel shows; picking a value commits; leaving sentinel selected → save skips writing).
- **`rework/track-editor-vm-text-fields`** branch landed **H13 TrackEditor slice 1** (Top-5 #1 first slice of the TrackEditor portion of H13, following the same pattern as AlbumEditor's slices 1+4). 5 text fields (TrackNumber as string for binding, Duration, Description, FlacPath, Mp3Path) moved off the editor's code-behind onto a new `TrackEditorViewModel` as `MixedField<string>` properties. Four Load methods cover the editor's three modes: `LoadSingle(track)` for single-edit on an existing track, `LoadNew(disc)` for the add-new path (TrackNumber defaults to `disc.Tracks.Max(t => t.TrackNumber) + 1`), `LoadMulti(tracks, placeholder)` for multi-edit (Unanimous/Mixed per field via the existing `MixedField<T>` machinery), `LoadLoose(track)` for the loose-track ctor (TrackNumber loads as "0" sentinel; UI hides it). XAML: each of the 5 TextBoxes TwoWay-binds to `_vm.X.Value`; Window's `DataContext = _vm` in all three ctors. Code-behind: `LoadTrack` restructured to branch on `IsAddingNew` → `_vm.LoadNew(disc)` vs `_vm.LoadSingle(track)`; `LoadLooseTrack` calls `_vm.LoadLoose`; `PopulateMultiFields` calls `_vm.LoadMulti` + `MixedPlaceholder.Apply` for the gray-italic chrome; `ApplyUiToTrack` / `CommitLooseTrack` / `SaveMulti` read from `_vm.X.Value` instead of `xxxBox.Text`. `SaveMulti`'s TrackNumber validation reads from the VM; the skip-when-still-Mixed logic uses a new `SkipMixedTextWrite(field)` helper. New `ApplyMixedFieldText(field, setter)` replaces the old `ApplyText(name, value, setter)` — same shape as AlbumEditor's slice-1 helper. `_mixedFields` HashSet entries for the 5 text fields retired (the HashSet still holds entries for the not-yet-migrated combobox + session + list fields). `SetOrMixed` / `SetOrMixedEditableCombo` retired (no callers after migration). 13 new tests in `TrackEditorViewModelTests`. Total: 709 tests (595 Core + 114 App). H13 stays open (slice 2 of N + PieceEditor + small editors). Top-5 #1 reframes to slice 2 (SparsCode + IsStereo combobox hybrids). **Action item for the user**: smoke-test the Track editor in all three modes — (1) Single-edit existing track: open, edit Duration/Description, OK, change persists. (2) Add Track from album editor: opens with next-available TrackNumber, fill fields, OK. (3) Multi-edit (right-click multiple tracks → Edit): differing TrackNumber/Description show "Mixed" in gray italic; clear+type → applies to all; leave alone → no change.
- **`rework/album-editor-vm-orchestration`** branch landed **H13 slice 4** (Top-5 #1 fourth-and-final slice of the AlbumEditor portion of H13). `OnSaveClick` + `SaveMulti` orchestration moved off the AlbumEditorWindow code-behind onto two new VM methods: `AlbumEditorViewModel.SaveSingle(album, originalInheritable)` validates Title (returns `MissingTitle` if blank, leaving the album unmutated so the View surfaces MessageBox + tab focus + Title control focus), writes every scalar field, snapshots Performers + Sessions ObservableCollections into the album's `List<T>` fields, removes empty discs, and runs `AlbumFieldPropagator.Propagate`; `AlbumEditorViewModel.SaveMulti(albums)` applies text + combobox fields under the started-Mixed-vs-still-Mixed contract then does the per-track null-backfill pass. **New `MixedField<T>.StartedMixed` property** captures the "did this field start in Mixed state?" bit that the editor's `HashSet<string> _mixedFields` previously tracked — set true in `InitMixed`, false in `InitUnanimous`, stays true even after the user dismisses the placeholder (whereas `IsMixed` clears on edit). With StartedMixed living on each field, `_mixedFields` retires entirely. SaveMulti text-field contract: `if (field.StartedMixed && (field.IsMixed || string.IsNullOrEmpty(field.Value))) skip; else write`. Combo contract: `if (!(field.StartedMixed && field.IsMixed)) write`. Code-behind shrinks: `OnSaveClick` collapses to ~20 lines (branch on `_isMixed`, call VM, handle validation feedback); `SaveMulti` / `ApplyMixedFieldText` / `NullIfEmpty` methods retired; `PopulateMultiDetailsTab` no longer pokes the HashSet — each `MixedPlaceholder.Apply` call stands alone since the VM tracks StartedMixed via `LoadMulti → InitMixed`. Lines: AlbumEditorWindow.xaml.cs 754→637 (-117); VM 172→329 (orchestration logic + docstrings). 28 new tests: MixedField StartedMixed (5) + new `AlbumEditorViewModelSaveSingleTests` (10) + new `AlbumEditorViewModelSaveMultiTests` (13). Total: 696 tests (595 Core + 101 App). H13 stays open — TrackEditor (711 lines, 3 ctors) + PieceEditor (1114 lines, 2 ctors) + the small editors remain. Top-5 #1 reframes to **TrackEditor extraction** (the `MixedField<T>` + StartedMixed + slice-per-PR pattern is now established and directly reusable). What stays in AlbumEditor's code-behind by design: modal-dialog ownership for sub-editors, the TrackList visual-tree grid + its row class, the playback-context menu, the H21 `SessionIndexMapping.RemapTracksAfterSessionRemoval` call (it accesses `_album.Discs`; would fit the VM eventually but not in this slice). **Action item for the user**: smoke-test save flows in both modes — (1) single-edit clear-Title → MessageBox + Title focus; (2) single-edit edit SparsCode → propagates to every track; (3) single-edit untouched SparsCode + track with null SparsCode → backfills; (4) multi-edit Title left as Mixed → no album wiped to "Mixed"; (5) multi-edit Title cleared with Backspace but not retyped → no album wiped to empty; (6) multi-edit pick SPARS code → all albums + all their tracks updated.
- **`rework/album-editor-vm-lists`** branch landed **H13 slice 3** (Top-5 #1 third-slice of the AlbumEditor extraction). Performers + Sessions list state moved off the AlbumEditorWindow code-behind onto `AlbumEditorViewModel` as `ObservableCollection<AlbumPerformer> Performers` / `ObservableCollection<RecordingSession> Sessions`. `LoadSingle` clears each collection then copies from the album's lists; `LoadMulti` clears both (Performers + Sessions tabs are hidden in multi-edit per H18). XAML: `PerformerList` / `SessionList` ListViews gain `ItemsSource="{Binding Performers}"` / `ItemsSource="{Binding Sessions}"` — `ObservableCollection`'s `CollectionChanged` notification means manual `ItemsSource = null; ItemsSource = list` refresh dances are gone. Code-behind: `_performers` / `_sessions` fields retired, every read/write rewritten as `_vm.Performers` / `_vm.Sessions`; the H21 defensive `RemapTracksAfterSessionRemoval` call still runs (now via `_vm.Sessions.IndexOf(...)` before `Remove`); `SaveSingle` writes `_album.Performers = _vm.Performers.ToList()` (the album model still holds `List<T>` for JSON round-trip); two leftover `PopulateSessionList()` refresh-trigger calls after `TrackEditorWindow.ShowDialog()` returns were removed (no longer needed — `CollectionChanged` auto-refreshes). **TrackEditorWindow signature relaxation**: sessions parameter widened from `List<RecordingSession>` to `IList<RecordingSession>` (and the nullable form) so the VM's `ObservableCollection<T>` passes through directly. TrackEditor's `OnAddSession` mutator now runs against the shared instance — additions propagate back to the parent AlbumEditor's UI immediately. The `_sessionsSnapshotForRollback` field stays `List<RecordingSession>?` (deep-cloned JSON snapshot via `DeepClone` returns `List<T>`). 4 new tests in `AlbumEditorViewModelTests` (LoadSingle hydrates lists, null lists leave empty, re-hydrate replaces, LoadMulti clears). Total: 668 tests (595 Core + 73 App). H13 stays open (slice 4 of N). Top-5 #1 reframed to point at slice 4 (`OnSaveClick` + `SaveMulti` orchestration → VM methods + `AlbumFieldPropagator.Propagate` integration moves with them). After slice 4 the AlbumEditor code-behind is mostly modal-dialog wiring and the track grid; Track + Piece editor extractions are separate multi-PR efforts after. **Action item for the user**: smoke-test the Performers + Sessions tabs in single-edit: (1) Add/Edit/Remove a performer — list updates, OK persists. (2) Add/Edit/Remove a session — same. (3) Inside the TrackEditor, click Add Session — new session should appear in the parent AlbumEditor's Sessions tab (shared collection). (4) Remove a middle session that has tracks pointing at it — tracks should re-anchor correctly (H21's RemapTracksAfterSessionRemoval still runs).
- **`rework/album-editor-vm-comboboxes`** branch landed **H13 slice 2** (Top-5 #1 second-slice of the AlbumEditor extraction). Added SparsCode + IsStereo to `AlbumEditorViewModel` as `MixedField<string>` properties using a stable string vocabulary that matches the dropdown items. **Design call**: used a hybrid approach — VM owns state, code-behind imperatively syncs the non-editable ComboBoxes via SelectionChanged handlers — rather than pure XAML bindings. Non-editable ComboBoxes with dynamically-appended Mixed sentinel ComboBoxItems don't fit a clean SelectedValue binding (ordering between "add item" and "set value" gets fragile; bound value doesn't match yet → binding fails). New VM constants: `SparsCodeMixedSentinel`, `IsStereoMixedSentinel`. New translator helpers: `SparsCodeToString`/`FromString`, `IsStereoToString`/`FromString` for bool? ↔ string. New `SparsCodeCombo.AppendMixedSentinel` public helper (factored out of `PopulateMixed`). New `OnSparsCodeChanged` / `OnStereoChanged` SelectionChanged handlers push user picks to VM; CommunityToolkit's SetProperty value-equality check means programmatic syncs don't spuriously trip WasEdited. `OnSaveClick` and `SaveMulti` read from VM via the translator helpers and use `_vm.X.IsMixed` instead of `SparsCodeCombo.IsMixedSentinelSelected` / `StereoBox.SelectedIndex != 3`. 23 new tests. Total: 664 tests (595 Core + 69 App). H13 stays open (slice 3 of N). Top-5 #1 reframed to point at slice 3 (Performers + Sessions lists). **Action item for the user**: smoke-test the SPARS Code + Stereo dropdowns in both single-edit and multi-edit modes (mixed → sentinel shows; picking a value commits; leaving the sentinel selected → save skips writing).
- **`rework/album-editor-vm-text-fields`** branch landed **H13 slice 1** (was Top-5 #1) — first slice of the multi-PR `AlbumEditorViewModel` extraction. Pre-fix the editor was 720 lines of code-behind reading/writing TextBox.Text directly + tracking multi-edit "Mixed" state via a `HashSet<string>` + per-field `MixedPlaceholder.Apply()` wiring. New files: `Helpers/MixedField.cs` (generic tri-state wrapper with `Value`/`IsMixed`/`WasEdited`; an `_initializing` flag suppresses WasEdited during programmatic loads), `ViewModels/AlbumEditorViewModel.cs` (7 `MixedField<string>` properties + `LoadSingle(album)` / `LoadMulti(albums, placeholder)`). XAML: TextBoxes TwoWay-bind to `X.Value`; Window's `DataContext = _vm` in both ctors. Code-behind: `PopulateDetailsTab` calls `_vm.LoadSingle/LoadMulti`, `MixedPlaceholder.Apply()` still does UI chrome on Mixed fields, `OnSaveClick` reads from `_vm.X.Value`, `SaveMulti` uses new `ApplyMixedFieldText` helper that preserves pre-fix "skip if still Mixed or cleared-without-typing" semantics. The old `SetOrMixed` / `SetOrMixedEditableCombo` / `ApplyText` helpers retired. **Later H13 slices**: (2) SparsCode + IsStereo combobox machinery, (3) Performers + Sessions lists to ObservableCollection, (4) OnSaveClick + SaveMulti orchestration moves to VM. Track + Piece editor extractions follow after. 12 new App.Tests (6 MixedFieldTests + 6 AlbumEditorViewModelTests). Total: 641 tests (595 Core + 46 App). Counts unchanged (H13 still open). Top-5 #1 reframed to point at slice 2. **Action item for the user**: smoke-test single-edit (open one album → all 7 fields populate, edit Title, OK, change persists) and multi-edit (open multiple albums with differing Titles → Title shows "Mixed" in gray italic; click + type → placeholder clears + value applies to all; click + Backspace → placeholder clears but field stays empty and OK does NOT wipe all albums' Titles).
- **`rework/itunes-importer-counters`** branch retired M4 (was Top-5 #3 — pivoted to here from #1 H13/AlbumEditor since that's genuinely multi-session work and a fragmentary "3-field slice" wouldn't really retire it). `ItunesImporter` had been threading three counter values (`newComposers`, `newPieces`, `newSubpieces`) through four helper methods (`PopulatePieceRefs`, `GetOrCreateComposer`, `ResolveOrCreateTopPiece`, `EnsureSubpiecePath`) as separate `ref int` parameters — awkward to thread, easy to miss-increment, ugly at call sites. Wrapped in a small private `Counters` class with three mutable int fields; helpers bump counts in place, outer `Import` method reads totals into `ImportResult`. Pure refactor — no behaviour change, no new tests (existing 10 `ItunesImporterTests` cover the counter values via `ImportResult` assertions). Total: 629 tests (595 Core + 34 App). Counts: Medium 81→80, Total 178→177. Top-5 reshuffle: H13/AlbumEditor stays #1, H36/CanonView stays #2, H21 remainder promotes to #3, M5 promotes to #4, M3 (PieceRow EndPiece inverse-navigation) becomes new #5. **Action item for the user**: none — pure refactor.
- **`rework/itunes-dedup-performer`** branch retired H24 (was Top-5 #1). Pre-fix the "already imported" filter keyed on `(album-title, disc, track)`; two same-titled albums (Karajan's Beethoven 9 vs Bernstein's Beethoven 9) collided. The original finding suggested adding `Label|CatalogueNumber` to the key — but `ItunesTrack` doesn't carry those (iTunes XML lacks pressing-specific catalogue info). Right discriminator: `AlbumArtist` on iTunes side, first `AlbumPerformer.Name` on canon side. Both normalise through a new public `ItunesImportViewModel.NormalisePerformer(string?)` helper (lowercase → strip non-alphanumeric → sort tokens; "Karajan, Herbert von" matches "Herbert von Karajan"). Key widens from 3-tuple to 4-tuple. Albums with no performer info dedup as before (empty performer string). The deeper "thread iTunes Persistent ID through" path remains M27. Diacritic-aware normalisation NOT done (Dvořák ≠ Dvorak under current scheme) — pragmatic limitation, documented in tests. 8 new tests in `ItunesImportDedupTests`. Total: 629 tests (595 Core + 34 App). H24 retires; counts High 6→5, Total 179→178. Top-5: H13/AlbumEditor → #1, H36/CanonView → #2, M4 → #3, H21-remainder → #4, M5 (album-level dedup on re-import) → new #5. **Action item for the user**: smoke-test by importing one same-titled album, then looking for a DIFFERENT performer's same-titled album — its tracks should still appear as not-imported.
- **`rework/sessionindex-defensive-remap`** branch retired the **bug class** of H21 (was Top-5 #1). Pre-fix: removing a recording session in the Album Editor's Sessions tab left every track's positional `SessionIndex` silently mis-pointing — tracks at the removed slot referenced whatever now occupied that index, tracks at later slots referenced the session one earlier than intended. Fix: new `SessionIndexMapping.RemapTracksAfterSessionRemoval(removedIndex, tracks)` helper. Pure logic, unit-tested. Walks every track on the album and translates: removed-slot → null, later-slot → decrement, earlier-slot → unchanged, null-track → unchanged. `AlbumEditorWindow.OnRemoveSession` calls it BEFORE removing the session from `_sessions`. The "reorder sessions" symptom isn't surfacable today (no reorder UI), but if one's ever added the same helper fits. 6 new tests in `SessionIndexMappingTests` covering each branch + a mixed-tracks-all-cases case + a defensive `-1` no-op. **H21 stays open** at reduced scope: the architectural cleanup (give `RecordingSession` a stable `Id` and store it on tracks instead of an index) is multi-PR work and now demoted from Top-5 #1 to #5 since no active corruption is possible. Top-5 reshuffles: H24 → #1, H13/AlbumEditor → #2, H36/CanonView → #3, M4 → #4, H21 architectural-remainder → #5. Counts unchanged (H21 still listed in High). Total: 621 tests (595 Core + 26 App). **Action item for the user:** smoke-test by opening an album with multiple sessions, removing one in the middle, and confirming track session references re-anchor correctly.
- **`rework/observable-isscrubbing`** branch retired M1 + M2 (PlayerViewModel.IsScrubbing → [ObservableProperty]; M2 stale-housekeeping — Top-5 #5). (M1) `IsScrubbing` was a bare `public bool { get; private set; }`. The CommunityToolkit `[ObservableProperty]` source generator now emits a public property with PropertyChanged. `BeginScrub()` / `EndScrub()` keep their existing semantics (the only callers are themselves). Public-set visibility is slightly relaxed (was `private set;`), but the trade is intentional — tests can now exercise scrub state without reflection. 2 new tests in `PlayerViewModelIsScrubbingTests` (BeginScrub raises PropertyChanged; EndScrub clears + raises). (M2) Already retired in spirit — H45 (`rework/wpf-di-hygiene`) promoted `SettingsViewModel` and `ImportExportViewModel` from `AddTransient` to `AddSingleton` back on 2026-05-21. Moving M2 to Retired as paperwork cleanup. Counts: Medium 83→81, Total 181→179. Total tests 615 (589 Core + 26 App). **Note on Top-5 #4 reframing**: while scoping the next H36 slice I discovered the "small remaining views" (PickListsView/RolePickerWindow with 1 Click handler each) are actually legitimate modal-dialog ownership — `Window.GetWindow(this)` for editor Owner — same pattern as AlbumsView's New/Edit/Delete kept by design. So Top-5 #4 becomes **H36 scoped to CanonView** (10 handlers, the only meaningful remaining target). Top-5 #5 becomes M4 (ItunesImporter ref int counters). **Action item for the user:** none — minor refactor only.
- **`rework/canonview-expansion-helper`** branch retired the long-pending H47 follow-up (Top-5 #4). Scoped down to what actually deserves extraction: the **level-3+ generic subpiece walks** in CanonView were duplicate-shaped against PiecesWindow's, so those route through `TreeExpansionState.CollectExpanded` / `ApplyExpanded` (promoted from internal). CanonView's `CollectExpandedSubpieces` / `ApplyExpandedSubpieces` collapse from ~30 lines of recursive walks to two-line delegations. The view still owns its `SubpieceKey(item)` predicate. **The level-1 (composer) and level-2 (piece-under-composer) walks stay in CanonView by design** — they recurse through a heterogeneous top-level structure (`ComposerTreeNode`'s Pieces / CrossComposerNodes / ContributedGroups, each with different key logic). A generic facade for that wouldn't be simpler than the explicit code. No new unit tests: the walker requires a real WPF visual tree (`ItemContainerGenerator` is virtualised). Build clean, all 613 tests pass. Top-5 #4 promotes to H36 scoped to PickListsView/RolePickerWindow; #5 becomes M1 (PlayerViewModel.IsScrubbing → [ObservableProperty]). **Action item for the user:** smoke-test the Canon view's tree — expand a composer, expand a piece, expand a subpiece/version, change the filter, confirm expansions survive the rebuild.
- **`rework/itunesimportview-relay-command`** branch retired the ItunesImportView slice of H36 (Top-5 #4, third H36 slice). The lone `Click="OnImportSelectedClick"` next to a `Command="{Binding LoadCommand}"` on the same toolbar — the inconsistency the original review specifically flagged — is now `Command="{Binding ImportSelectedTracksCommand}"`. Surfaced `ImportSelectedTracksCommand(IList? selection)` as a thin `[RelayCommand]` wrapper around the existing `ImportTracksAsync(IReadOnlyList<ItunesTrack>)`. The wrapper projects the non-generic WPF `IList` selection to `ItunesTrack` via `OfType<>` defensively (DataGrid's `SelectedItems` is non-generic `IList`). XAML: `CommandParameter="{Binding ElementName=TracksGrid, Path=SelectedItems}"`. `OnImportSelectedClick` retired from code-behind. `OnGridSorting` (multi-key sort) stays — it's view-only sort-state management against `DataGridSortingEventArgs` chrome. 3 new tests in `ItunesImportViewModelCommandTests` (empty / null / mixed-selection-filter). Total: 613 tests (589 Core + 24 App). H36 stays open (~19 Click handlers remaining across 6 non-editor views: CanonView=10, PiecesWindow=3, PieceAlbums=2, PiecePicker=2, PickLists=1, RolePicker=1). Top-5 #4 promotes to the long-standing CanonView expansion-state migration (H47 follow-up); #5 becomes "next H36 slice (PickListsView or RolePickerWindow)". **Action item for the user:** smoke-test the iTunes Import view's "Import Selected" button.
- **`rework/tracksview-relay-commands`** branch retired the TracksView slice of H36 (Top-5 #4 was scoped to TracksView, second slice of the wider migration). Surfaced two new `[RelayCommand]` properties on `TracksViewModel`: `ApproveTracksCommand(IList? selection)` (delegates to the existing `ApproveRowsAsync`, rebuilds rows, applies filter, status; errors via `IDialogService.ShowError`) and `RejectTracksCommand(IList? selection)` (preserves the album-bound vs loose breakdown in the confirmation prompt — e.g. `"2 album track(s) and 1 loose track(s)"` — prompts via `IDialogService.Confirm`, delegates to `RejectRowsAsync`). `TracksViewModel`'s ctor gained an `IDialogService` parameter; DI auto-resolves (registered as singleton when H3 landed). XAML: toolbar Refresh binds `LoadDataCommand`; context-menu Approve/Reject use the `PlacementTarget.DataContext.XxxCommand` + `PlacementTarget.SelectedItems` pattern AlbumsView established. Three code-behind handlers retired (`OnRefreshClick`, `OnContextApproveTrack`, `OnContextRejectTrack`). **Reselect-after-approve dropped intentionally** — the pre-fix `ReselectTracks(...)` call after approve doesn't fit cleanly into a RelayCommand round-trip (no way to push state back to the View); AlbumsView's slice didn't have an equivalent. If user feedback misses it, a future PR can add an attached behaviour listening to a "LastApprovedTrackIds" property on the VM. The View's `ReselectTracks` helper itself stays — it's still called from the modal-edit code path. 6 new tests in `TracksViewModelCommandTests`. Total: 610 tests (589 Core + 21 App). H36 stays open (10 views remaining); Top-5 #4 promotes to **H36 scoped to ItunesImportView** (smallest remaining slice — single Click handler next to a Command in the same toolbar). **Action item for the user:** smoke-test TracksView — right-click a provisional track → Approve / Reject menu items work (Reject shows the count-aware confirmation dialog); the toolbar Refresh button works.
- **`rework/albumsview-relay-commands`** branch retired the AlbumsView slice of H36 (Top-5 #4 was scoped to AlbumsView). Surfaced three new `[RelayCommand]` properties on `AlbumsViewModel`: `ApproveAlbumsCommand(IList? selection)` (multi-select Approve, filters to provisional defensively), `RejectAlbumsCommand(IList? selection)` (multi-select Reject, confirms via `IDialogService.Confirm` with count-aware message, removes confirmed rows), and `CheckReferencesCommand` (empty-albums case shows info via `IDialogService.ShowInfo`; routes the report through `ShowInfo` on pass / `ShowError` on fail). XAML changes: toolbar Refresh button binds `Command="{Binding LoadDataCommand}"`; Check References binds `Command="{Binding CheckReferencesCommand}"`. Context-menu Approve / Reject use the standard `PlacementTarget.DataContext.XxxCommand` + `PlacementTarget.SelectedItems` pattern via `RelativeSource AncestorType=ContextMenu` (ContextMenu lives outside the visual tree so ElementName binding won't reach back). Four code-behind handlers retired (`OnContextApproveAlbum`, `OnContextRejectAlbum`, `OnRefreshClick`, `OnCheckReferencesClick`). What stays in code-behind by design: `OnNewAlbumClick` / `OnEditAlbumClick` / `OnDeleteAlbumClick` / `OnContextPlayAlbum` — all open modal dialogs needing `Window.GetWindow(this)` as Owner, a legitimate View concern. 7 new App.Tests in `AlbumsViewModelCommandTests` documenting the pattern for subsequent view migrations. Total: 604 tests (589 Core + 15 App). H36 stays open in the High section (11 views remaining); Top-5 #4 promotes to **H36 scoped to TracksView** as the natural continuation. **Action item for the user:** smoke-test AlbumsView — right-click a provisional album → Approve / Reject menu items should work (Reject shows a confirmation dialog); the toolbar Refresh and Check References buttons should work.
- **`rework/pieces-window-dedup`** branch retired H47 (PiecesWindow no longer carries its own sort + expansion-state machinery — Top-5 #3). **Sort dedup**: `PiecesWindow.ApplySort` + its `CatalogAsc` / `CatalogDesc` helpers (~40 lines) replaced by a single call to `PieceSorting.Sort(pieces, crossComposerNodes: null, field).Cast<CanonPiece>()`. Added a `ParseSortField` string→enum mapper for the view's `"Catalog"` column tag (PieceSorting uses `Catalogue`). Ascending order matches the previous impl exactly. Descending uses `.Reverse()` over the ascending result, which flips tiebreakers too — visible change for "Category descending" / "Year descending" where pieces within the same category now order by reverse catalogue. Acceptable: dedup payoff outweighs the change; documented inline. **Expansion-state dedup**: extracted `CDArchive.App.Helpers.TreeExpansionState` static class (`Save(root, keyOf, into)` / `Restore(root, keyOf, from)`). PiecesWindow's ~50 lines of recursive walk collapses to two-line delegations; the view keeps only its `ExpansionKey(item)` predicate. `CanonView`'s more complex 4-hashset expansion state stays in place — migrating it needs a multi-set facade on top of the helper, flagged as Top-5 #5 follow-up. No new unit tests in this PR: PieceSorting already has comprehensive coverage in `PieceSortingTests` (the sort half is now covered transitively); the expansion-state walk requires a real WPF visual tree (`ItemContainerGenerator` is virtualised) so it can't be unit-tested headlessly. Build clean, all 597 tests pass. **Action item for the user:** smoke-test the Pieces window — sort by Title / Catalog / Category / Year, toggle ascending/descending; expand subpieces, change the filter, confirm expansions survive the rebuild.
- **`rework/piece-index-collision-detection`** branch retired H41 (PieceReferenceIndex collision detection — Top-5 #3). Pre-fix `RegisterPiece` called `titleMap.TryAdd(...)` which silently dropped the second registrant when two pieces shared a composer + normalized title key. Now the drop is recorded as a `TitleCollision(Composer, NormalizedKey, KeptPiece, DroppedPiece)` on a new public `IReadOnlyList<TitleCollision> Collisions` property. The first-write-wins behaviour is preserved (combined with the existing approved-first OrderBy so the approved piece keeps winning), but each collision is now surfaced. Per-key granularity: a piece emits up to 5 title variants and can collide on one while differing on others — each colliding key is a separate record. Self-collisions (a single piece emitting the same key from multiple variants, e.g. Title equals DisplayTitle when there's no catalog) are suppressed via `ReferenceEquals` so they don't pollute the report. `SeedResult.IndexCollisions` populates from `_resolver.Collisions` after `BuildResolver`; the SeedDb tool grew a "Title-key collisions in the piece resolver index" section in its report. The deeper "convert to `Dictionary<string, List<IndexEntry>>` and disambiguate at resolve time" path the finding suggested is deferred — it'd need TrackPieceRef to carry a discriminator, which is a much larger model change. Detect-and-log retires the symptom without that surgery. 6 new tests in `PieceReferenceIndexCollisionTests`. Build clean, all 597 tests pass (589 Core + 8 App). **Action item for the user:** re-run `dotnet run --project tools/CDArchive.Tools.SeedDb` and check the new "Title-key collisions" section. Any entries are duplicate-piece pairs in your data worth investigating.
- **`rework/piece-version-shuttle`** branch retired H14 (PieceVersionShuttle extracted + reflection contract test — Top-5 #5). Extracted the piece↔version property-shuttle logic out of `PieceEditorWindow.xaml.cs` into a new `CDArchive.Core.Helpers.PieceVersionShuttle` static class. Pre-fix the shuttle lived as two ~25-line manual property lists (`VersionToPiece` + `CopyPieceToVersion`) — every new model field needed two coordinated edits or it was silently dropped on save. While extracting, found a latent bug: `TextAuthor` (librettist/lyricist credit) exists on both `CanonPiece` and `CanonPieceVersion` but was missing from BOTH directions of the original shuttle — editing a version wiped the librettist data. Now in the shuttle in both directions; covered by a dedicated regression test. The reflection contract test (`SharedProperties_RoundTrip_PreservesValues`) enumerates every public settable property matching name+type on both classes, applies explicit `PieceOnlyPropertyNames` / `VersionOnlyPropertyNames` exception lists, and round-trips a sentinel value through `FromVersion → IntoVersion`. Future drift fails the test with a pointed error message telling the next contributor to add the property to the shuttle OR to one of the exception lists. Sentinel-value helper is typed: string / int? / bool? / JsonElement? / List<T> each have a sentinel generator; the helper throws on unknown types so the test can't silently pass over an untested property type. PieceEditorWindow's two methods now delegate to the helper (10 lines each, down from ~30); the editor still owns the version-only `Description` field directly. 4 new tests in `PieceVersionShuttleTests` (reflection contract, TextAuthor regression, NumberedSubpieces explicit-override, version-only fields unclobbered). Build clean, all 591 tests pass (583 Core + 8 App). **Action item for the user:** smoke-test by opening any piece that has a version with a `text_author` field in its JSON, edit something else, and click OK. The TextAuthor should still be present on the version after save — pre-fix this would have silently nulled.
- **H44 deferred** [2026-05-21]: user wants to revisit the `PersonId` / `EnsembleId` dead-schema question as a feature after the rework backlog is worked down (rather than pruning the unused schema now). See the H44 entry in `Rework.md`'s 🟡 Deferred section for the three paths considered and the rationale. Re-open when feature work is ready to start; the right entry point will be the back-population heuristic for extracting structured People/Ensembles from existing free-text `DisplayName` values.
- **`rework/app-tests-foundation`** branch retired H39 + H40 (VM test foundation + first regression tests for H3's dialog flows — Top-5 #5). Created `tests/CDArchive.App.Tests/` project targeting `net8.0-windows` (must match App's TFM to reference WPF-flavoured assemblies, but `UseWPF` is NOT set so no WPF runtime initialisation in headless tests). Project references `CDArchive.App` + `CDArchive.Core`, pulls in xUnit 2.5.3 + NSubstitute 5.3.0. Test doubles in `Infrastructure/`: `RecordingDialogService` (records every `Confirm` / `ShowInfo` / `ShowError` call, scriptable `ConfirmResponse`) and `ScriptedFileDialogService` (queues for save/open path responses). 8 starter tests cover the three H3 sites: `AlbumsViewModelDialogTests` (3 tests — confirm prompt + respect cancel + respect confirm), `ItunesImportViewModelDialogTests` (2 — empty-selection info + load-throws error path), `ImportExportViewModelDialogTests` (3 — both-picks-cancelled bypasses confirmation, confirmation prompt appears for restore, user-cancel respects). (H39) Zero VM coverage is no longer literal — three VMs covered with the headless-test pattern. (H40) The H3 retirement now ships with regression tests in the same PR cycle; the "regression test for every Critical/High" gap is bounded — future Rework PRs land tests alongside the fix. Total: 587 tests (579 Core + 8 App), all pass. Note: with no `.sln` at the root, `dotnet test` runs one project at a time; the convention is now two paths (`tests/CDArchive.Core.Tests/...csproj` and `tests/CDArchive.App.Tests/...csproj`). **Action item for the user:** none — fix is test-infrastructure only.
- **`rework/dialog-service`** branch retired H3 (VMs no longer reference `System.Windows` / `Microsoft.Win32` — Top-5 #5). Introduced two thin abstractions over the WPF modal-dialog surface: `IDialogService { Confirm, ShowInfo, ShowError }` and `IFileDialogService { PickSaveFile, PickOpenFile }` in `CDArchive.App.Services`. `WpfDialogService` and `WpfFileDialogService` are the production implementations and the only classes in the app (outside editor windows) allowed to call `MessageBox.Show` / instantiate `SaveFileDialog`/`OpenFileDialog`. Both registered as DI singletons in `App.OnStartup`. Five call sites refactored: `AlbumsViewModel.RejectAlbumAsync` (Reject confirmation), `ItunesImportViewModel.ImportTracksAsync` ("Nothing to import" info + "Import error" with stack dump), `ImportExportViewModel.RestoreFromJsonAsync` (Restore confirmation), `ImportExportViewModel.PickSaveFile/PickOpenFile` (now thin VM-side wrappers carrying the JSON filter constant; the dialog construction itself moved into `WpfFileDialogService`). Stale `using System.Windows;` / `using Microsoft.Win32;` directives removed from all three VM files plus `CanonViewModel.cs` (which had a leftover import but no actual usage). `SettingsViewModel`'s pre-existing `BrowseXxxRequested` event pattern stays as the alternative "View handles the dialog" pattern — the new abstractions complement it. VM-level test coverage (H39) is now unblocked for the dialog flows: an `App.Tests` project could ship `RecordingDialogService` + `ScriptedFileDialogService` and assert "VM asked for confirmation before rejecting" without spinning up WPF. Build clean, all 579 tests pass. **Action item for the user:** smoke-test (a) right-click "Reject Album" on a provisional album — confirmation dialog should appear; (b) iTunes import with no selection → "Import Selected" → info MessageBox; (c) iTunes import flow that throws → error MessageBox; (d) Import/Export Restore from JSON → save/open dialogs + restore confirmation.
- **`rework/dead-code-cleanup`** branch retired H10 + H11 + L45 (three trivial dead-code findings bundled — Top-5 #5). (H10) Removed the empty `Loaded += (_, _) => { };` handler from `CanonView`'s constructor — the real `OnLoaded` is wired in XAML. (H11) Removed the `OnSelectedComposerChanged` partial method body in `CanonViewModel` whose body was just a comment; the CommunityToolkit source generator handles the no-op for free. (L45) Deleted `src/CDArchive.App/Helpers/DispatcherHelper.cs` — `grep -rn DispatcherHelper src/ tests/ tools/` confirmed zero callers. The "use this as a chokepoint for cross-thread refresh" path the Rework note mentioned isn't load-bearing — C2 already shipped its own `SynchronizationContext` capture via `NAudioPlayerService`. Build clean, all 579 tests pass. **Action item for the user:** none — pure dead-code removal, no behavioural change.
- **`rework/editor-mode-and-validation`** branch retired H18 + H19 (editor window mode-switching via XAML bindings + consistent validation — Top-5 #5). (H18) Replaced imperative visual-tree mutation in `AlbumEditorWindow` and `TrackEditorWindow` with `Visibility="{Binding ShowXxx, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BoolToVis}}"` bindings driven by public auto-properties on the window code-behind. `AlbumEditorWindow` gained `ShowPerformersTab` / `ShowSessionsTab` (multi-edit ctor sets them false instead of `MainTabs.Items.Remove(...)`). `TrackEditorWindow` gained `ShowNavigation` / `ShowTrackNumber` / `ShowSession` — multi-edit ctor sets `ShowNavigation=false`, loose-track ctor sets all three false, replacing six `xxx.Visibility = Collapsed` assignments across two constructors. Properties are read-only (set once in ctor before render); the binding evaluates once at load — no INPC needed. `OnTabSelectionChanged` already used reference equality on the tab item, so collapsed tabs work fine. Anticipatory fix: the editors are single-use today, but the imperative pattern didn't survive a re-show. (H19) Surfaced `MessageBox.Show` in three small editors that previously silently `return`'d on missing required fields: `RoleEditorWindow` (Role name), `ComposerCreditEditorWindow` (Contributor name), `InstrumentEntryEditorWindow` (Instrument). The 4th site H19 listed — `ComposerEditorWindow` Name/SortName — was already MessageBox-validated by H32. Validation across the editor family is now consistent. Build clean, all 579 tests pass. The "ideal" `CanSave` + `OK.IsEnabled` pattern needs VM extraction (deferred to H13). **Action item for the user:** smoke-test by (a) opening the Album editor on a multi-album selection — Performers/Sessions tabs should be hidden; (b) opening the Track editor on a loose track — Navigation, Track # field, and Session row should all be hidden; (c) opening RoleEditor / ComposerCreditEditor / InstrumentEntryEditor and clicking OK with the required field blank — should now show a MessageBox instead of doing nothing.
- **`rework/app-shared-resources`** branch retired H35 + H37 + L29 (App.xaml shared resources foundation — Top-5 #5 bundled with related Low). (H37) Deleted the custom `Converters/BoolToVisibilityConverter.cs` — it had identical behaviour to WPF's framework `System.Windows.Controls.BooleanToVisibilityConverter` and exactly one use site (`ValidationView.xaml`) that wasn't actually using it (declared but never referenced). (H35) Established `App.xaml` as the resource dictionary: one canonical `<BooleanToVisibilityConverter x:Key="BoolToVis" />` replaces 11 inline declarations spread across `MainWindow.xaml`, `PlayerBar.xaml`, `AlbumsView.xaml`, `TracksView.xaml`, `CanonView.xaml`, `PickListsView.xaml`, `PiecesWindow.xaml`, `PiecePickerWindow.xaml`, `CatalogueView.xaml`, `ValidationView.xaml`, `ArchiveBrowserView.xaml`. The inline declarations used three different `x:Key` spellings (`BoolToVis` / `BoolToVisConverter` / `BoolToVisibilityConverter`); 24 binding references were renormalised to the single canonical `BoolToVis`. Added named status-palette brushes (`StatusBrushPending/InProgress/Completed/Failed`, `SeverityBrushWarning/Error`) plus four semantic accent brushes (`AccentBrush`, `ProvisionalBadgeBrush`, `ApprovedBadgeBrush`, `DangerBrush`). (L29) `ConversionStatusToColorConverter` and `ValidationSeverityToColorConverter` no longer hardcode `Colors.Gray` / `Colors.DodgerBlue` / etc. — they resolve via `Application.Current.Resources[brushKey]`, so palette changes touch XAML, not C#. The bulk sweep of ~280 hex literals across views into named brushes is deferred as a new Low finding (L47) — kept this PR mechanical and tractable; the foundation is in place. Build clean, all 579 tests pass. **Action item for the user:** smoke-test the Conversion view (Pending/InProgress/Completed/Failed rows should keep their existing gray/blue/green/red palette) and the Validation view (Warning orange / Error red).
- **`rework/test-infra-and-rename`** branch retired H17 + H20 + L30 (SimpleDbContextFactory consolidated + Title-rename regression test — Top-5 #5 bundled with related Low). (H17 + L30) The 6-line `SimpleDbContextFactory : IDbContextFactory<CanonDbContext>` class moved into `CDArchive.Core.Data` — both tests and the SeedDb tool already reference Core, so one canonical location replaces 12 identical inline copies (11 test files + `tools/CDArchive.Tools.SeedDb/Program.cs`). Each call site already imports `CDArchive.Core.Data`, so no `using` churn. L30 is the same-finding-different-severity duplicate flagged as "extension of H17" — retired together. (H20) Added two tests in `AlbumSaveInPlaceTests`. `RenamingAlbumTitle_LeavesOneAlbum_NoDuplicates` exercises the AlbumEditorWindow JSON-clone-and-substitute path (save → load → JSON-clone → rename → save the clone with no CWT entry); asserts one album with the new title, disc/track counts preserved, and row ID churned (documented behaviour — orphan-delete and reinsert is what makes the rename work). `RenamingAlbumTitle_WithLabelAndCatalogue_StillEndsAsOneAlbum` repeats with Label + CatalogueNumber set — non-trivial because the `UNIQUE` filtered index on (label, catalogue_number) requires the orphan-DELETE to be ordered before the INSERT in the same transaction; reaching the assertion proves EF Core's ordering is correct. All 579 tests pass (577 baseline + 2 new). **Action item for the user:** smoke-test the album editor's rename flow — open an album, change Title, click OK. The renamed album should appear once in the list (no duplicate), with all tracks intact.
- **`rework/test-skip-preconditions`** branch retired H38 (tests silently `return` on missing preconditions instead of skipping — Top-5 #5). Two of the four originally-listed sites had already been removed during prior refactors (the `SaveOperations_DoNotTouchJsonFiles` test now builds its own temp-dir fixture; `FindRepoDataDirectory_*` tests likewise use a synthetic temp tree). The two remaining sites in `SqliteRoundTripTests` (`LeventailDeJeanne_MovementsHaveIndividualComposers` and `CrossComposerSubpieceFinder_SurfacesLeventailMovementsUnderTheirComposers`) both gated on whether the canonical "(Various)" L'éventail data had been seeded. Both `[Fact]`s now use `[SkippableFact]` and `if (leventail is null) return;` becomes `Skip.If(leventail is null, "L'éventail de Jeanne not present in seeded data.")`. xUnit reports an explicit `Skipped` result instead of a silent green pass if the precondition fails. Added the `Xunit.SkippableFact` package (v1.4.13) — chosen over `Assert.Skip` because xUnit 2.5.3 (the pinned version) doesn't ship that API (`Assert.Skip` is xUnit 3). All 577 tests pass with the L'éventail data present (none skip in the current seeded state). **Action item for the user:** none — fix is test-infrastructure only.
- **`rework/picklists-named-kinds`** branch retired H30 (`PickListsViewModel` hardcoded pick-list selection by index position — Top-5 #4). Pre-fix the VM identified its ten lists (Forms, Categories, Catalogues, Keys, Instruments, CreativeRoles, Ensembles, VoiceTypes, PerformerRoles, Labels) by display index — `SelectedListIndex == 6` (= Ensembles) appeared as a literal in two places, and `CurrentStringList()` / `CurrentRenameDict()` were positional `switch` expressions over the same display order. Reordering / inserting / removing a kind in the `PickListNames` array silently routed every Add/Update/Remove command to the wrong list. New `Core/Helpers/PickListKinds` exports a `PickListKind` enum, an `OrderedKinds` array (the display ordering, the single source of truth for position-to-kind mapping), a `DisplayName(kind)` map, `KindAt(index)` / `IndexOf(kind)` round-tripping helpers, and `IsStringList` / `IsRenamable` predicates. The VM now stores its data as `Dictionary<PickListKind, List<string>>` + `Dictionary<PickListKind, Dictionary<string, string>>` for renames; the magic-6 literal becomes `CurrentKind == PickListKind.Ensembles` via a new `CurrentKind` computed property. The XAML side keeps binding `SelectedIndex={Binding SelectedListIndex}` and `ItemsSource={x:Static vm:PickListsViewModel.PickListNames}` (no UI churn — `PickListNames` now forwards `PickListKinds.OrderedDisplayNames`). Reordering the display now only changes presentation; routing is enum-keyed and rename-safe. 8 new tests in `PickListKindsTests` lock the contract (OrderedKinds covers every enum value uniquely, KindAt round-trips through IndexOf, IsStringList false only for Ensembles, IsRenamable true for exactly Forms/Categories/Catalogues/Keys, the default display order is pinned). All 577 tests pass. **Action item for the user:** smoke-test by opening the Pick Lists screen, selecting "Ensembles" from the dropdown — the "Members…" button should still appear; adding/renaming/removing in each list should still target the right list.
- **`rework/disc-folder-conventions`** branch retired H46 + M35 (disc-folder convention centralised + padded folders resolve — Top-5 #4 bundled with a Medium). All four "Disc N" sites — `AlbumScaffoldingService.GetDiscFolderName`, `ArchiveAudioLocator.ResolveDiscDirectory`, `ArchiveScannerService.DiscFolderRegex`, `CataloguingService.FindMp3Folders` — now route through a new `Core/Helpers/DiscFolderConventions` (`Format` for scaffolding, `CandidateNames` for locator lookup, `IsDiscFolderName` for scanner match, `SearchPattern` for cataloguer glob). The H46 bug itself: the locator only tried unpadded `Disc 1`, so 10+ disc box sets scaffolded with `Disc 01` silently lost playback on discs 1-9. Now walks `CandidateNames` (unpadded first, then padded for disc 1-9) and returns whichever exists. The user no longer has to set `AlbumDisc.FolderName` manually for every padded disc. Paired with the existing `DiscFolderOrdering` from H29's retirement, the entire "Disc N" surface area is now single-sourced. 24 new tests in `DiscFolderConventionsTests` + an `ArchiveAudioLocatorTests.Resolve_PaddedDiscFolder_ResolvesViaCandidateNames` case driving the H46 regression itself. All 550 tests pass. **Action item for the user:** if you have any 10+ disc box sets with padded folder names, smoke-test playback on discs 1-9 — they should now resolve without needing per-disc `FolderName` overrides.
- **`rework/wpf-di-hygiene`** branch retired H34 + H45 (small WPF/DI cleanup — Top-5 #4 and #5 bundled). (H34) Removed `VirtualizingStackPanel.IsVirtualizing="False"` from `CanonView.xaml`'s composer tree and `PiecePickerWindow.xaml`'s picker tree. Both were workarounds for an `Items.Refresh()` expansion-state collapse, but the actual fix (save/restore the expansion state — see `SaveAllExpansionState` in `CanonView.xaml.cs`; `PickerNode.IsExpanded` view-model binding in the picker) is already in place. With virtualization re-enabled, tens of thousands of `TreeViewItem` containers no longer realise eagerly. In-line XAML comments document why it's safe. (H45) Promoted `SettingsViewModel` and `ImportExportViewModel` from `AddTransient` to `AddSingleton` — the Singleton `MainViewModel` ctor-injected them, freezing them for life anyway, so the Transient registration silently violated the contract. Promoting to Singleton documents actual behaviour with no semantic change. All 526 tests pass (App-only changes; no Core-side tests to add until H39 lands an App.Tests project). **Action item for the user:** smoke-test the Canon view's composer tree and the piece picker — scrolling should feel snappier on a fully-loaded catalogue, and expansion state should still survive sort changes / filter changes.
- **`rework/seeder-safety`** branch retired H42 + H43 (`CanonDbSeeder` IsProvisional preservation + seed-time atomicity — Top-5 #3 and #4 bundled). (H42) The four row builders (ComposerRow, PieceRow via MapPiece recursion, AlbumRow, AlbumTrackRow) all fell through to the row class's C# `IsProvisional = true` default — every reseed silently reset every approval the user had ever applied. Now each builder explicitly copies `src.IsProvisional`, preserving the JSON's value. (H43) `SeedAsync` ran three sequential `SaveChangesAsync` calls with no wrapping transaction — parallel to C5. A `SeedAlbums` failure left composers + pieces committed with no recovery signal. Now wrapped in `BeginTransactionAsync` / `CommitAsync`. First seeder test file (`CanonDbSeederTests`, 4 tests): IsProvisional=false round-trip, default-true on fresh data, mid-seed-failure rolls back composers + pieces, happy-path commit. All 526 tests pass. **Action item for the user:** smoke-test the recovery flow — approve a composer / piece, run `dotnet run --project tools/CDArchive.Tools.SeedDb -- --export`, delete the DB, reseed. The approval should now survive. (Pre-fix every Approve was undone on reseed.)
- **`rework/small-editor-correctness`** branch retired H31 + H32 + H33 (small-editor correctness sweep — three Top-5 entries bundled). (H31) Six editors used to reconstruct their model via `Result = new T { ... }` on OK, silently dropping unknown fields. The model classes are currently field-pure, so the bug was anticipatory — but the prescribed pattern matches `MarkerEditorWindow` and `VariantEditorWindow`'s correct behaviour. Each editor now holds a `_working` reference (input or fresh instance) and mutates only the fields its UI exposes. Public surface (`Result` / `Role` / `Credit` / `Entry`) unchanged so call sites work as-is. (H33) Falls out automatically from H31's fix to `EnsembleEntryEditorWindow` — the unconditional `IsEnsemble = true` write is gone, replaced by a `_members` write only. Opening the editor on a non-ensemble entry now preserves `IsEnsemble = false`. (H32) `ComposerEditorWindow.OnOkClick` gained Name + SortName validation before commit — blank composer names that previously hit the DB's `UNIQUE NOT NULL` index with an opaque `SqliteException` now surface a `MessageBox` at the dialog level. 5 new tests in `SmallEditorContractTests` documenting the model-level invariants the editors must respect; the WPF code-behinds themselves aren't unit-testable without a host (H39 still open). All 522 tests pass. **Action item for the user:** smoke-test by (a) opening any of the 6 editors on an existing model, editing one field, clicking OK — the original reference should be mutated in place; (b) opening `EnsembleEntryEditorWindow` on a non-ensemble entry — should NOT flip `IsEnsemble`; (c) opening `ComposerEditor` with blank Name — should reject with a MessageBox instead of saving an unnamed composer.
- **`rework/archive-scanner-fixes`** branch retired H28 + H29 (`ArchiveScannerService` async-in-name-only + lexicographic disc ordering — Top-5 #3 and #4 bundled). (H28) `ScanArchiveAsync` ran every directory enumeration synchronously on the calling thread and wrapped in `Task.FromResult` — async-in-name-only, froze the UI for tens of seconds on slow drives at the 3,000-CD target. Now wrapped in `Task.Run(...)` matching `ValidateArchiveAsync`'s pattern. (H29) Disc-folder ordering used lexicographic `.OrderBy(d => d)` — "Disc 10" landed between "Disc 1" and "Disc 2", and since the scanner assigns `DiscNumber` 1..N in iteration order, every 10+ disc box set got the wrong on-disk-folder → discNumber mapping, breaking downstream audio-locator resolution. New `Core/Helpers/DiscFolderOrdering` extracts a (primary, secondary) numeric key from `Disc N` / `Disc N-M` / `Disc 0N` names; `OrderByDiscNumber(paths)` is applied in both `ArchiveScannerService.ScanArchiveAsync` AND `CataloguingService.FindMp3Folders` — the same lexicographic bug existed there too, flagged as a deferred Nit during the H25 PR and now fixed. 17 new tests in `DiscFolderOrderingTests` covering numeric / sub-disc / padded / non-matching cases; 6 new tests in `ArchiveScannerServiceTests` driving the scanner against a fake `IFileSystemService` — the headline case is a 12-disc box-set scan that asserts every Disc N folder gets `DiscNumber = N` regardless of on-disk enumeration order. New fake-FS pattern is reusable for future scanner-side tests (no existing scanner tests prior to this PR). All 517 tests pass. **Action item for the user:** smoke-test by (a) clicking the archive-scan command on a real archive — should no longer freeze the UI mid-walk; (b) if you have any ≥10-disc box sets, confirm the scanner / cataloguer assigns disc numbers correctly (Disc 10's tracks should appear under disc 10, not disc 2).
- **`rework/cataloguing-service-safety`** branch retired H25 + H26 + H27 (CataloguingService safety sweep — Top-5 #3, #4, #5 bundled). Three orthogonal fixes to the tag-write pipeline. (H25) `FindMp3Folder` returned only the first matching disc; multi-disc albums silently skipped discs 2..N. Renamed to `FindMp3Folders` (plural), yields every disc folder with its disc number. `ReadAlbumTagsAsync` iterates each disc with per-disc track numbering and re-applies folder-derived DiscNumber / DiscCount after `FormatEntriesAsync`'s unconditional clear. (H26) Composer cache keyed on lastName alone — Johann II Strauss and Richard Strauss collided and wrote the first's birth/death years onto every Strauss track. Cache now keys on `(lastName, firstName)`. `CataloguingService`'s ctor switched to `ICatalogueReference` (was the concrete `CompositeCatalogueReference`) so tests can stub the lookup; DI maps both registrations to the same singleton so `LastSourceUsed` bookkeeping isn't duplicated. (H27) `WriteFileTag` had no undo path. Now snapshots pre-write tag values to a `<file>.tagbackup.json` sidecar before mutating — only if no prior backup exists (preserves truly-original across multiple pipeline runs). New public `RestoreFromBackup(filePath)` reads the sidecar through the same atomic-write primitive. Failures (missing/corrupt sidecar) return a `WriteResult` failure without throwing. `TagSnapshot` JSON shape is the cross-run contract — pinned down by a round-trip test so a careless rename can't silently break every existing user's backups. 11 new tests in `CataloguingServiceSafetyTests`. All 494 tests pass. **Action item for the user:** smoke-test by (a) cataloguing a multi-disc album you have — discs 2..N should now be processed; (b) cataloguing two same-surname composers in one album (e.g. Strauss family) — each should get the right birth/death years; (c) checking that a `.tagbackup.json` sidecar appears alongside the first tag write to a given MP3. No UI for `RestoreFromBackup` yet — that's a separate small task; the API is in place.
- **`rework/track-editor-correctness`** branch retired H22 + H23 (TrackEditor correctness sweep — Top-5 #2 and #4 bundled). (H23) Added a "(no session)" pseudo-item to the SessionBox in both single-edit and multi-edit modes; pre-fix both paths collapsed `SessionIndex == null` to session 0 via `?? 0`, silently writing 0 on Save. Extracted the mapping logic to `CDArchive.Core.Helpers.SessionIndexMapping` — `InitialComboIndex` + `ResolveSelection` are pure, testable, and distinguish "write real session N" / "write null" / "skip the write (Mixed sentinel)". Multi-edit tracks the Mixed-sentinel position explicitly so the "(multiple albums — cannot edit)" disabled-combo case also skips writes correctly (sentinel at index 0 vs the normal sentinel at sessionCount+1). (H22) Both single-edit and multi-edit ctors now JSON-deep-clone the disc's track list and the session list on entry; a `Closing` handler restores them when `DialogResult != true`, undoing Prev/Next per-step commits and OnAddSession appends. Pre-fix Cancel was a polite lie. 23 new tests in `SessionIndexMappingTests` cover every branch of the helper; the Cancel-rollback path is verified by manual smoke test (no WPF test project yet — H39). All 483 tests pass. **Action item for the user:** smoke-test by (a) opening a no-session track in the track editor, clicking OK without changes, then re-opening — SessionIndex should still be null, not 0; (b) selecting multiple tracks that all have SessionIndex=null, opening the track editor, clicking OK without changes, then re-opening — all should still be null; (c) opening a track, clicking Next then Cancel — the navigated-from track's edits should be reverted.
- **`rework/album-editor-player-di`** branch retired H12 (`AlbumEditorWindow` reaching into `App.ServiceProvider` for `PlayerViewModel` — Top-5 #1). Both `AlbumEditorWindow` constructors now take an explicit `PlayerViewModel` parameter; the service-locator call inside `PlaySelectedTrack` is gone. `AlbumsViewModel` / `CanonViewModel` / `TracksViewModel` each gained a public `Player` property fed by DI — the three view code-behinds that open the editor read `vm.Player` and pass it through. 5 construction sites updated (`AlbumsView` × 3, `CanonView` × 1, `TracksView` × 1). Bonus cleanup while in the area: `AlbumsView.xaml.cs`'s "Play album" right-click handler had the same anti-pattern — now reads `vm.Player` too. Build clean, all 460 tests pass (Core test project doesn't cover WPF code-behind; verified via build + Top-5 manual smoke list). **Note:** `CanonView.xaml.cs` still has 3 `App.ServiceProvider.GetRequiredService<AlbumsViewModel>()` calls — same anti-pattern, different singleton, separate finding (worth its own future PR — `CanonView` could read from its CanonViewModel DataContext's `_albumsVm` injection if exposed). **Action item for the user:** smoke-test the right-click "Play track" / "Play from here" / "Play album" context menus on the Albums view, Canon view, Tracks view, and the album-editor dialog. All should behave identically.
- **`rework/canon-load-deduplication`** branch retired H9 (`CanonViewModel` re-loading albums + loose tracks 3+ times per session — Top-5 #1). Three-part refactor. (1) `AlbumsViewModel` and `TracksViewModel` each gained a `HasLoaded` flag flipped true once `LoadDataAsync` completes. (2) `CanonRejectCascade.RejectComposerAsync` / `RejectPieceAsync` got new overloads that accept the caller's `albums` + `looseTracks` lists; when non-null, the cascade mutates them in place (refs stripped, lists saved) so the caller can reuse them for the post-cascade rebuild — no second DB load. Null preserves legacy load-fresh for callers without container state. (3) `CanonViewModel` now takes `AlbumsViewModel` + `TracksViewModel` via DI. The 4 redundant `LoadAlbumsAsync + LoadLooseTracksAsync` sites route through a `GetContainersForRebuildAsync()` helper that prefers the singletons when `HasLoaded`, falling back to a fresh load with a Debug-level log when the user opened Canon before Albums / Tracks. Reject sites pass the singletons' lists through the cascade so the in-memory copies stay in sync with the DB. 3 new tests in `CanonRejectCascadeTests` for the in-place overload contract; existing 6 cascade tests still pass via the legacy load-fresh path. All 460 tests pass. **Action item for the user:** smoke-test by opening the Canon view first, scanning the badge counts, then opening the Albums view (should now be faster on a fresh app start at 3000-CD scale); also smoke-test the Reject Composer / Reject Piece flow (cascade should still strip refs from albums + loose tracks correctly).
- **`rework/archive-audio-locator-cache`** branch retired H8 (`ArchiveAudioLocator` filesystem-probe caching — Top-5 #1). Pre-fix every `Resolve` call issued ~6 syscalls (3× `Directory.Exists` + 2× `Directory.EnumerateFiles` with a glob, plus the override `File.Exists` pair). For a 10-track album auto-advance that's ~60 syscalls; on a network drive or at the 3,000-CD target it shows up as audible lag. Now the locator caches per-path `Directory.Exists` results and per-format-directory file lists (full enumeration, sorted ordinal-case-insensitive at cache-build time); the per-track `"NN*"` lookup is an in-memory prefix scan over the cached list. 10-track album goes from ~30 syscalls down to 3 (album dir exists + FLAC dir exists + FLAC dir enumerate). Auto-invalidates when `IArchiveSettings.ArchiveRootPath` drifts between calls — every cached path is constructed from that root, so a settings change drops the cache wholesale. New `IArchiveAudioLocator.Invalidate()` for explicit calls from album-folder rename / archive rescan flows. Thread-safe via a single lock around dictionary access; the syscalls themselves happen outside the lock so a slow disk can't block concurrent readers. Internal `FilesystemProbeCount` test hook lets the regression tests assert the cache actually short-circuits. 4 new tests in `ArchiveAudioLocatorTests` (existing 11 still pass — caching is transparent to the resolution contract). All 457 tests pass. **Action item for the user:** smoke-test playback through a multi-track album — should sound identical. The speedup is most noticeable if your archive lives on a network drive.
- **`rework/itunes-library-cache`** branch retired H5 + H6 (`ItunesLibraryReference` caching + hardcoded archive-folder filter — Top-5 #1 and #2 bundled). (1) The class used to walk the iTunes Music Library XML twice — once eagerly via `LoadAllTracksAsync` on every iTunes-Import view click, once lazily into composer/works lookup on first call. New design: one walk that populates every projection (`Composers`, `Works`, `AllTracks`) into a shared `LibraryCache`; `LoadAllTracksAsync` reads from the cache too. Public `Refresh()` method invalidates the cache so the next access re-walks. (2) Hardcoded `"CD%20archive"` filter substring replaced by `internal static ComputeArchiveFolderFilter(path)` that URL-encodes the leaf folder of `IArchiveSettings.ArchiveRootPath`. Default `D:\CD archive` produces `CD%20archive` (byte-identical to the pre-fix value, so no behavioural change for default-config users), a custom root produces the matching encoded substring. Class now takes optional `IArchiveSettings` via DI. 9 new tests in `ItunesLibraryReferenceTests`. All 453 tests pass. **Action item for the user:** smoke-test the iTunes Import view — first click reads the XML, subsequent clicks should be instant. If you change settings or want fresh data, restart the app (a "Refresh from iTunes" UI button is a separate small future task — the `Refresh()` API is in place for when it lands).
- **`rework/piece-reference-index-singleton`** branch retired H7 (`PieceReferenceIndex.Current` static singleton + throwaway-resolver races — Top-5 #1). Added `internal PieceReferenceIndex(bool registerAsCurrent)` ctor overload. The 3 throwaway-resolver sites in `Core/Services` (`ItunesImporter`, `SqliteCanonDataService.SaveAlbumsCoreAsync`, `SqliteCanonDataService.SaveLooseTracksCoreAsync`) now construct via the new overload with `registerAsCurrent: false` — they only call `BuildResolver` + `TryResolve`, never touch hit dictionaries, no business being `Current`. Pre-fix every album save / loose-track save / iTunes import swapped `Current` for an empty-hits throwaway and every `HitCountBadgeConverter` read returned 0 until the next real `Rebuild`/`RebuildContainers` — the "badges flicker to zero mid-save" symptom. Default ctor preserved (`public PieceReferenceIndex()` still sets `Current = this`) so the App's DI-registered singleton continues to claim the static on construction. Test fixtures and `tools/ItunesProbe` use the default ctor but never read `Current`, so the leak is harmless — left alone to avoid noise. 3 new tests in `PieceReferenceIndexCurrentTests` lock in the contract, including the H7 regression itself (real index with hits → throwaway+BuildResolver → assert `Current.CountForPiece` still returns the real count). All 444 tests pass. The deeper "kill the static accessor and route through DI / a MarkupExtension" option flagged in the original finding is deferred — narrow fix retires the symptom without disrupting WPF binding. **Action item for the user:** smoke-test by saving an album edit and watching the composer-tree badges in the Canon view — they should stay at their real counts through the save, not flicker to zero.
- **`rework/archive-settings-io`** branch retired H4 (`ArchiveSettings` ctor I/O + non-atomic writes + narrow catch — Top-5 #1). Three-part fix. (1) The ctor no longer reads disk — properties hold defaults until the host calls `Initialize()`. `IArchiveSettings.Load()` renamed to `Initialize()` for clarity (no external callers of `Load()` existed). `App.OnStartup` resolves `IArchiveSettings` and calls `Initialize()` on the UI thread right after `BuildServiceProvider()`, before `MainViewModel` resolves and pulls in every settings consumer transitively. (2) `Save()` now writes atomically via `WriteAllText` to `settings.json.tmp` + `File.Move(tmp, real, overwrite: true)` — a process kill between the two leaves the live file bit-identical. The tmp is best-effort deleted on failure before the exception propagates. (3) `Initialize()` widened the catch from `JsonException` to `Exception` — any I/O failure (permission denied, AV file lock, file missing on first launch) is logged via `ILogger<ArchiveSettings>` and the defaults survive instead of crashing startup. Added an internal `ArchiveSettings(string settingsFilePath, ILogger?)` ctor so tests can drive the contract against a temp file without touching `%AppData%`. 7 new tests in `ArchiveSettingsTests`. Two existing test fakes (`ArchiveAudioLocatorTests.FakeSettings`, `FfmpegConversionServiceTests.FakeSettings`) renamed their `Load()` stubs to `Initialize()`. All 441 tests pass. **Action item for the user:** smoke-test by adjusting any setting in the Settings tab, hitting Save, then restarting the app — the setting should round-trip. If you want to test the corruption-recovery path, delete or scramble `%AppData%\CDArchive\settings.json` between runs — the app should now boot cleanly with defaults instead of refusing to start.
- **`rework/migration-self-healing`** branch retired C6 (`ApplySchemaUpgradesAsync` PRAGMA leakage + orphan `*_new` tables — Top-5 #1). 🎉 **Last open Critical retired**; the Top-5 is now all High. Two-part fix. (1) Extracted `WithForeignKeysOffAsync(conn, body)` helper that wraps the OFF/ON pair in **try/finally** — the original recipe restored FKs only on the happy path, so any exception between `PRAGMA foreign_keys=OFF` and the final `ON` (CREATE TABLE failure, INSERT SELECT misalignment, the `foreign_key_check` assertion inside the txn, etc.) left the connection silently running with FK enforcement off. Microsoft.Data.Sqlite pools connections, so the next caller to grab that recycled handle would silently accept FK-violating writes. Both recreate helpers now route through the helper via expression-bodied delegation. (2) `DropOrphanRecreateTablesAsync` runs at the top of `ApplySchemaUpgradesAsync`, scanning `sqlite_master` for any leftover `*_new` tables and dropping them with `IF EXISTS`. Idempotent on a healthy DB (zero rows from the SELECT). Cheap insurance against the off-nominal class of failures (OS-level crash mid-transaction, third-party tool running half a migration manually). Both helpers exposed as `internal static` and tested directly in `MigrationSelfHealingTests`: 4 tests covering happy-path PRAGMA round-trip, the throw-restores-PRAGMA contract (the C6 regression), orphan-table cleanup, and the no-op behaviour on a healthy DB. All 434 tests pass. **Action item for the user:** no manual smoke test needed; the fix is purely defensive. The Settings UI may now want to gain a "Run integrity check" diagnostic button at some point — `PRAGMA integrity_check` + `PRAGMA foreign_key_check` would surface any silent damage from a pre-fix-era crash, but that's a separate feature.
- **`rework/naudio-sync-context`** branch retired C2 (`NAudioPlayerService` ctor-time `SynchronizationContext.Current` capture — Top-5 #1). Ctor now takes an explicit `SynchronizationContext? sync = null` parameter instead of capturing `SynchronizationContext.Current` itself. `App.OnStartup` captures the WPF dispatcher's sync context (guaranteed available because OnStartup runs on the UI thread by the WPF runtime contract) and registers it in DI as a singleton before calling `AddCoreServices`. `AddCoreServices` registers the audio player via a factory that pulls `SynchronizationContext` from the provider — when the App's registration is in place, the captured UI context flows through; when it isn't (tests, direct-DI fixtures), the player gets null and raises events inline (the pre-fix test-mode behaviour). New regression test `EventsRaisedFromWorkerThread_AreMarshalledThroughCapturedSyncContext` constructs the service with a recording `SynchronizationContext` subclass, calls `Load` from a thread-pool worker (`SynchronizationContext.Current == null`), and asserts the resulting `DurationKnown` event was Posted through the captured context rather than fired inline. Defends against the future regression of someone capturing the worker-thread context. All 430 tests pass. **Action item for the user:** smoke-test playback through the player bar — should be transparent; if anything stops marshalling correctly, you'll see WPF binding errors in the rolling log.
- **`rework/album-save-narrow-load`** branch retired C11 + M15 (`SaveAlbumsAsync` full-graph load + `.Include` chains missing `AsSplitQuery()` — Top-5 #1). Two-step refactor of `SaveAlbumsCoreAsync`: (1) cheap upfront projection (`SELECT Id, Label, CatalogueNumber, Title, Subtitle FROM albums`) builds the identity map, then a narrow `Where(a => matchedIds.Contains(a.Id))` Include-load fetches the full graph for matched rows only. Orphans deleted via stub-attach + `Remove` — SQLite's album→child Cascade FKs (verified all the way through volumes/discs/tracks/piece-refs/performers/sessions) handle the descendants without us loading them. (2) `.AsSplitQuery()` added to both the new save-side narrow load and `LoadAlbumsAsync`'s full load — was producing a Cartesian-multiplied row count from the 5-Include shape (Volumes / Sessions / Performers / Discs→Tracks→PieceRefs / Discs→Tracks→Performers). At the current 99-album scale the perf delta is small; the win compounds at the 3,000-CD target. Locked in by `AlbumSaveInPlaceTests.OrphanAlbumDelete_CascadesToEveryChildTable` — seeds 3 albums each populating every cascade-FK (volume, disc, track, performer, session, piece ref), saves a 1-album list, asserts the FK cascade cleaned up every orphan child row. All 429 tests pass. **Caveat for the user:** the production save flow (`AlbumsViewModel.SaveAsync`) still passes `_allAlbums` to `SaveAlbumsAsync`, so the narrow-load itself doesn't reduce work in that path — every album is "matched". The AsSplitQuery half is what helps the current flow; the narrow-load half is forward-leaning for when a caller passes a smaller delta (e.g. iTunes import only saving newly-added albums). **Action item for the user:** smoke-test the AlbumsView Save button on a real edit — should be transparent; perf may feel slightly snappier on large catalogues.
- **`rework/gitignore-cleanup`** branch retired C7 (`.gitignore` daily-mess). Added six new ignore patterns: `*.binlog`, `*_wpftmp.csproj`, `__pycache__/`, `.claude/`, `data/*.db*`, `*.bak*`. The single `*.bak*` glob handles every backup-filename flavour the seeder + pre-migration scripts have produced: plain `.bak`, dotted-timestamp `.bak.20260422_160109`, dashed-timestamp `.bak-20260424-170547`, and the comma-quirk `.bak,pre-loose-tracks`. JSON snapshots stay tracked (textual source-of-truth, regenerable from the DB). Drops `git status` noise from 32 untracked to 12; the remaining 12 are ad-hoc helper scripts and personal/scratch files (`global.json`, `MusicBrainz evaluation.MD`, `scripts/*.py` extractors) that need a per-file decision from the user. No code touched; 428/428 tests still pass. **Action item for the user:** none — fix is text-config only.
- **`rework/test-data-isolation`** branch retired C15 (two tests mutating the LIVE production data directory). Extracted `CanonDataService.FindRepoDataDirectory(string startFrom)` as `internal static` so the parameterless-ctor data-dir walk-up is testable without `Assembly.Location`. The marker-resolver test no longer renames the user's `Classical Canon composers.json` aside (a crash between Move and the finally-restore would otherwise leave the data dir broken); it builds a synthetic `<tmp>/sub/deeper` + `<tmp>/data/ClassicalCanon.db` tree and calls the helper directly. Two companion tests added for the JSON-marker and direct-child shortcut so the three resolver branches are fully covered. `SaveOperations_DoNotTouchJsonFiles` no longer runs `Save*Async` against the production DB — it builds a temp dir with an empty SQLite schema + placeholder JSON files, lands a minimal fixture via `SaveBatchAsync`, re-stamps the JSONs to a known mtime (defends against FAT32's 2-second granularity), then asserts mtimes stayed put across the load-and-save round-trip. Cleanup goes through `SqliteConnection.ClearAllPools()` before `Directory.Delete` because Microsoft.Data.Sqlite pools connections; same pattern as `AlbumSaveInPlaceTests`. Read-only integration tests against the live DB (`BeethovenOp2_HasAlbumHits_AfterSqliteRoundTrip`, `LeventailDeJeanne_MovementsHaveIndividualComposers`, `CrossComposerSubpieceFinder_*`, the two `LoadX_DeserializesAll` smoke tests) are intentionally left alone — they're integration tests by design, don't mutate, and have real value. All 428 tests pass. **Action item for the user:** no manual smoke test needed; the fix is purely test-infrastructure.
- **`rework/picklists-rename-save`** branch retired C14 (`PickListsViewModel.ApplyRenames` fire-and-forget pieces save). Extended `ICanonDataService.SaveBatchAsync` with an optional `CanonPickLists? pickLists` parameter; extracted `SqliteCanonDataService.SavePickListsCoreAsync` (stages delete-all-and-reinsert without flushing) and wired it into the shared transaction in `SaveBatchAsync` ahead of the composers→pieces→albums→loose-tracks chain. `PickListsViewModel.ApplyRenames` no longer fire-and-forgets — it now returns the rename count, and `SaveAsync` decides whether to bundle `pieces: _canonVm.Pieces.ToList()` into the same `SaveBatchAsync` call. End result: a piece-side failure rolls the pick-list change back too, and the status message only flips to truthful "Pick lists saved. Renamed N piece field(s)." after both have landed. 3 new tests in `SaveBatchAtomicityTests` cover the pick-lists-only path, the pick-lists + pieces atomic happy path, and downstream-failure rollback (uses the existing albums-duplicate-track-number trick since `SavePiecesCoreAsync` silently skips orphan-composer pieces rather than throwing — a separate latent issue worth flagging if it ever changes). All 426 tests pass. **Action item for the user:** smoke-test by renaming a Form / Category / Catalogue / Key in the Pick Lists screen and hitting Save — should see the new status string and any open Composers tree should pick up the new value on next refresh.
- **`rework/cross-save-atomicity`** branch retired C5 (cross-save sequences not atomic). Added `ICanonDataService.SaveBatchAsync(composers?, pieces?, albums?, looseTracks?)` that opens one `CanonDbContext` + one `BeginTransactionAsync`, runs each non-null subsystem inside it via newly-extracted private `Save*CoreAsync(db, …)` helpers, then commits once and applies queued CWT-id-update closures post-commit. Public single-subsystem `Save*Async` methods are now thin wrappers around the Core helpers (composers/pieces preserve their existing partial-commit semantics for the single-subsystem path; albums/loose-tracks keep their own outer tx as before). `ItunesImportViewModel.ImportTracksAsync` replaces its 4-call chain with one `SaveBatchAsync`; `TracksViewModel.SaveAsync` replaces `_albumsVm.SaveAsync() + SaveLooseTracksAsync` with `SaveBatchAsync(albums, looseTracks)` + a local `RebuildContainers` (drops the redundant fresh `LoadAlbumsAsync` round-trip). PickLists' fire-and-forget pieces save is now exclusively C14's territory. 4 new tests in `SaveBatchAtomicityTests` lock the contract: full-4-subsystem persist, rollback-on-mid-batch-constraint-violation (proves true rollback by asserting the baseline `album_tracks.Id` survives byte-identical), null-subset no-op, partial-subset saves only specified. All 423 tests pass. **Action item for the user:** smoke-test an iTunes import — the behaviour should be identical on success, but a constraint failure mid-import now reliably leaves the canon untouched rather than half-written.
- **`rework/dispose-service-provider`** branch retired C1 (`ServiceProvider` never disposed on exit). `App.OnExit` now calls `ServiceProvider?.Dispose()` (wrapped in try/catch, logged on failure) before `Log.CloseAndFlush()`. The DI cascade now disposes every `IDisposable` singleton — most importantly `NAudioPlayerService`, which holds `MediaFoundationReader` + `WaveOutEvent` + a `System.Threading.Timer`; the audio device is no longer leaked on crash exit. `NAudioPlayerService.IsDisposed` exposed as `internal` so the regression test can observe the cascade; a new test (`NAudioPlayerServiceTests.DisposingServiceProvider_DisposesSingletonPlayer`) builds the production DI graph, resolves the player, disposes the provider, and asserts disposal. Test project gains a `Microsoft.Extensions.DependencyInjection` package reference. All 408 tests pass. **Action item for the user:** none — fix is transparent.
- **`rework/musicbrainz-ratelimit`** branch retired C10 + M16 + M17 + M18 (`MusicBrainzReference` hardening). The non-thread-safe `DateTime.UtcNow` gate is replaced with `SemaphoreSlim(1,1)` + `Stopwatch` so concurrent callers serialise through the 1.1-second window — and the monotonic clock means NTP skew can't open up a burst. Service now consumes `IHttpClientFactory` (named client registered in `AddCoreServices`); User-Agent built from the real assembly version + `https://github.com/jamesliddle/CDArchive` contact URL; hand-rolled retry policy (3 attempts, 500ms / 1000ms backoff) covers 429/5xx/`HttpRequestException`, re-entering the gate on each retry. Adds `Microsoft.Extensions.Http` package dependency. 11 new tests in `MusicBrainzReferenceTests` cover gate, retry, non-retry, cancellation, and UA composition via a `ScriptedHandler` + `FakeDelayer`. **Action item for the user:** no manual smoke test needed — the next time the cataloguing flow falls through to MusicBrainz, watch for 503s in the rolling log; if any appear, the gate is still being violated upstream.
- **`rework/ffmpeg-args`** branch retired C8 + H15 + H16 (`FfmpegConversionService` hardening). Single `Arguments` string replaced with `ProcessStartInfo.ArgumentList.Add(...)` per-argument escaping. `ConvertFileAsync` now: short-circuits when the source file is missing, catches `Win32Exception` to surface a friendly "Could not launch ffmpeg" message when the configured `FfmpegPath` is wrong, enforces a 10-minute per-file timeout (kills ffmpeg + tree on expiry), and deletes the partial `.mp3` on every failure path so a re-run doesn't see the stub as "already done". A new `DeriveMp3Path` helper replaces the old global `.Replace("\\FLAC\\", "\\MP3\\")` with a segment-aware variant that only renames the immediate parent dir when it's named `FLAC` — so archive roots like `D:\FLAC\Music\` and filenames containing "FLAC" survive. `InternalsVisibleTo` added so the pure helper is unit-testable. 8 new tests in `FfmpegConversionServiceTests` cover both the path-derivation edge cases and the process-invocation error branches. **Action item for the user:** smoke-test by converting one real album in the app — the existing flow is unchanged; this should be a transparent improvement.
- **`rework/tag-writes`** branch retired C12 + C13 (tag-write safety). `CataloguingService.WriteFileTag` now writes atomically via copy-to-temp + atomic rename in the same directory — a process kill mid-write leaves the source FLAC/MP3 bit-identical. `WriteTagsAsync` returns `IReadOnlyList<WriteResult>` and continues past per-file failures instead of aborting; the single caller (`CatalogueViewModel`) shows `N succeeded; M failed (see log)` when failures occur. `InternalsVisibleTo` added so 7 new unit tests can exercise the atomic-write primitive directly without TagLib fixtures. Merged as PR #14. **Action item for the user:** smoke-test on a real album to confirm tags round-trip via TagLib (the new tests cover atomic-rename semantics but not the TagLib integration itself, which the manual flow has always covered).
- **`rework/barecatch`** branch retired C3 + C9 (bare-catch sites). All 8 VM-level `_refIndex.Rebuild*` catches in `CanonViewModel`/`AlbumsViewModel`/`TracksViewModel` plus `CataloguingService.ReadFileTag` plus `MusicBrainzReference.RateLimitedGetAsync` now log via injected `ILogger<T>`. Stacks on `rework/logging` (PR #11). Next priority per the refreshed Top-5 is C12/C13 (tag-write safety: atomic-rename + per-file error handling in `CataloguingService.WriteFileTag`).
- **`rework/logging`** branch retired C4 (structured logging). Serilog wired with a daily rolling file sink at `%LocalAppData%\CDArchive\logs\`, `SqliteCanonDataService` instrumented with start + elapsed-ms entries for each `SaveXxxAsync`. PR #11 still open at time of writing. **Action item for the user:** smoke-test by running the app, hitting Save on any subsystem, and confirming `cdarchive-YYYYMMDD.log` shows the entry.
- **`feature/tracklist`** branch landed the Tracks list and the loose-tracks subsystem. See `## Tracks list and loose tracks` below for the model. **Action item for the user:** run `dotnet run --project tools/CDArchive.Tools.SeedDb -- --promote-loose-tracks` (dry-run) then `--apply` to convert existing synthetic single-track wrapper albums in the local DB. Real albums with Label / Catalogue / ArchiveFolder / Sessions / track-level Performer overrides are auto-skipped.
- **Stashed work — `stash@{0}: WIP: markers refactor + Various composer`** is preserved on disk. Contains in-progress JSON edits that introduce a unified `markers` array on pieces (replacing per-piece `tempos` / `first_line` shape) and a `(Various)` sentinel composer for collaborative works like `L'éventail de Jeanne`. Independent of the tracklist work; revisit when ready to address multi-composer pieces.
- **Multi-composer pieces (`L'éventail de Jeanne` etc.)** remains the one open canon-data deferral — see the *Multi-composer pieces have no primary composer field* lesson. The stashed work above sketches a `(Various)` sentinel composer; needs a design call before implementation.

---

## Active rework backlog — see `Rework.md`

`Rework.md` at the repo root is the master technical backlog. 244 findings across Critical / High / Medium / Low / Nit, each with a file:line reference, why it matters, and a suggested fix. It also defines the working-through protocol in its own `## Working through this document` section.

**When asked to "address findings", "work the rework backlog", "tackle next priorities" or similar:**

1. **Open `Rework.md` first.** Read its `## Working through this document` and `## Top 5 priorities (start here)` sections. They are the source of truth for the protocol — don't try to reconstruct it from memory.

2. **Pick the next finding(s).** In order:
   - If the Top-5 list still has open items, work from there first.
   - Otherwise: Critical → High → Medium → Low → Nit, numeric within each priority.
   - **Bundle related findings.** Many entries cross-reference each other (e.g. C3 + C9 are both "bare catch blocks"; H35 + L29 + N29 are all "no shared resource dictionary"). Read cross-references and address the natural batch in one session — retire them together.

3. **Address each finding.** Apply the suggested fix as a starting point, not a prescription. Implementation often surfaces constraints the review didn't see; if so, document them in the retirement note or as a fresh finding.

4. **Add a regression test where feasible.** H40 in `Rework.md` flags that no Critical/High has a test today; landing tests alongside fixes prevents future drift. VM-layer fixes likely need H39 done first (create a `CDArchive.App.Tests` project).

5. **Verify.** Build + tests pass (`dotnet build` then `dotnet test`); for behavioural fixes (e.g. C13 atomic write, C8 ffmpeg arg escaping), exercise manually too.

6. **Retire the finding(s).** Move each entry from its severity section to the `✅ Retired` section at the bottom of `Rework.md`. Append a single line: `[YYYY-MM-DD] <short-commit-sha> — <one-line description of fix>`. Update the summary table counts at the top of the file (decrement the severity counter, decrement the total).

7. **Re-evaluate the Top-5 list.** If a Top-5 item retired, promote the next-most-impactful Critical/High item into the list. If none left in those severities, demote the section to "Top 5 priorities" → "Top 3 priorities" etc.

8. **Cross-link Lessons Learned here in `CLAUDE.md`** if the fix surfaced a non-obvious trap. Use the same Problem → Solution → Corollary pattern as the existing entries. The intent is that the *next* contributor doesn't re-discover what was just learned.

**Deferring instead of retiring**: if a finding is intentionally not being addressed in this pass (architectural cost too high, blocks on a prior finding, requires a feature decision the user isn't ready to make), move it to the `🟡 Deferred` section at the bottom of `Rework.md` with a one-line reason. Don't delete; the rationale matters when revisiting.

**Session etiquette**: at the start of each "address findings" session, do a quick read of the most-recent retirements in `Rework.md` to understand what's already been changed (mtime + git log will show this if the file's grown). At the end of each session, leave `Rework.md`'s summary table accurate and the Top-5 list refreshed — that's what the next session reads first.

---

## Technology Stack

| Layer | Technology |
|---|---|
| UI Framework | WPF (Windows Presentation Foundation) |
| Architecture | MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `AsyncRelayCommand`) |
| Data Access | EF Core 8 with SQLite; System.Text.Json for serialization |
| Audio Playback | NAudio 2.2.1 (`MediaFoundationReader` + `WaveOutEvent`); Windows Media Foundation handles MP3 + FLAC decode natively on Win10 1709+ / Win11 |
| Target Framework | .NET 8.0 (Windows) |
| Project Format | Modern SDK-style .csproj |

---

## Solution Structure

```
CDArchive/
  data/
    ClassicalCanon.db                  # SQLite database — single source of truth at runtime
    Classical Canon composers.json     # Optional JSON snapshots — used only for one-shot
    Classical Canon pieces.json        #   import/export via the seeder tool or the
    Classical Canon pick lists.json    #   Import/Export screen. Not auto-synced with the DB.
    Classical Canon albums.json
  src/
    CDArchive.App/                     # WPF application
      ViewModels/                      # MVVM view models
      Views/                           # XAML views and code-behind
      Converters/                      # WPF value converters
      Helpers/                         # Utility classes
      App.xaml / App.xaml.cs           # Startup, DI container, DataTemplates
      MainWindow.xaml                  # Shell: nav bar + content area
    CDArchive.Core/                    # Business logic library
      Models/                          # Domain models (CanonPiece, CanonComposer, CanonAlbum, …)
      Services/                        # Data access, conversion, archive scanning, ref index
      Data/                            # EF Core DbContext, row entities, CanonDbSeeder
      Helpers/
      ServiceCollectionExtensions.cs   # DI registration
  tools/
    CDArchive.Tools.SeedDb/            # One-shot CLI: seeds the DB from JSON, exports DB → JSON
  tests/
    CDArchive.Core.Tests/              # xUnit suite (model, parser, round-trip, invariants)
  scripts/
  docs/
```

---

## Canon Data Model

### Composer (`CanonComposer`)

Represents a classical music composer.

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| Name | string | `name` | Display name, e.g. "Beethoven, Ludwig van" |
| SortName | string | `sort_name` | For alphabetical ordering |
| BirthDate | string? | `birth_date` | ISO format "YYYY-MM-DD" or partial |
| BirthPlace | string? | `birth_local_place` | City/town |
| BirthState | string? | `birth_state` | State/province |
| BirthCountry | string? | `birth_country` | Country |
| BirthNotes | string? | `birth_notes` | Freeform |
| DeathDate | string? | `death_date` | ISO format |
| DeathPlace | string? | `death_local_place` | |
| DeathState | string? | `death_state` | |
| DeathCountry | string? | `death_country` | |
| IsProvisional | bool | `is_provisional` | Defaults to `true`; cleared explicitly via Approve. Stored in the `is_provisional` column of the `composers` table. |

Computed properties: `BirthYear`, `DeathYear`, `LifeSpan`, `BirthLocation`, `DeathLocation`, `BirthYearSort`, `DeathYearSort`, `PieceCount`.

### Piece (`CanonPiece`)

Represents a musical work. This is a recursive, hierarchical model -- a piece can contain subpieces (movements, acts, scenes), and each of those can contain their own subpieces, versions, and metadata.

**Core identity:**

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| Composer | string? | `composer` | Composer name (matches `CanonComposer.Name`) |
| Title | string? | `title` | Original-language title |
| TitleEnglish | string? | `title_english` | English translation |
| Subtitle | string? | `subtitle` | |
| Nickname | string? | `nickname` | Popular name (e.g. "Moonlight") |
| Form | string? | `form` | Musical form (Sonata, Symphony, etc.) |
| Number | int? | `number` | Work number within form |
| MusicNumber | int? | `music_number` | Traditional numbering (e.g. opera scene numbers) |
| IsProvisional | bool | `is_provisional` | Defaults to `true`; cleared explicitly via Approve. Stored in the `is_provisional` column of the `pieces` table. |

**Tonality:**

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| KeyTonality | string? | `key_tonality` | Key name (e.g. "C", "B-flat") |
| KeyMode | string? | `key_mode` | "major" or "minor" |

**Cataloguing:**

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| CatalogInfo | List\<CatalogInfo\>? | `catalog_info` | Catalog entries (Op., WoO, K., BWV, etc.) |
| PublicationYear | int? | `publication_year` | |
| CompositionYears | JsonElement? | `composition_years` | Flexible (string or structured) |
| InstrumentationCategory | string? | `instrumentation_category` | "Chamber", "Piano", "Orchestra", etc. |

**Complex/JSON properties:**

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| Instrumentation | JsonElement? | `instrumentation` | Flexible format (see below) |
| Roles | JsonElement? | `roles` | Vocal work cast roles |
| Tempos | List\<TempoInfo\>? | `tempos` | Tempo markings, optionally nested |
| TextAuthor | JsonElement? | `text_author` | Librettist/lyricist as JSON array |
| FirstLine | string? | `first_line` | Opening text/lyric |

**Hierarchy:**

| Property | Type | JSON Key | Notes |
|---|---|---|---|
| Subpieces | List\<CanonPiece\>? | `subpieces` | Movements, acts, scenes, etc. |
| Versions | List\<CanonPieceVersion\>? | `versions` | Alternative arrangements/editions |
| NumberedSubpieces | bool? | `numbered_subpieces` | Whether subpieces display numbers; null = category default |
| SubpiecesStart | int? | `subpieces_start` | Starting number for subpiece numbering (default 1) |

**Instrumentation format** -- The `instrumentation` JSON field supports several shapes:

```json
// Simple strings
["piano", "violin", "cello"]

// Objects with parts
[{"instrument": "clarinet", "key": "B-flat", "number": 1}]

// Orchestra groupings
[{"orchestra": ["flute", "oboe", "trumpet"]}]

// Sections
[{"section": "violin", "number": 1}]

// Alternates
[{"instrument": "horn", "alternate_instrument": "cornetto"}]
```

### CatalogInfo

| Property | JSON Key | Notes |
|---|---|---|
| Catalog | `catalog` | Prefix (e.g. "Op.", "K.", "BWV") |
| CatalogNumber | `catalog_number` | Number as string |
| CatalogSubnumber | `catalog_subnumber` | Sub-number (e.g. "#1" in "Op. 2 #1") |

Subpieces inherit their parent's `CatalogNumber` automatically at load time via `PropagateCatalogNumbers()` if they only have a `CatalogSubnumber`.

### CanonPieceVersion

Represents an alternative version/arrangement of a piece. Has nearly all the same fields as `CanonPiece`, plus:

| Property | JSON Key | Notes |
|---|---|---|
| Description | `description` | e.g. "Original version", "arr. for string quartet" |
| ContributingComposers | `contributing_composers` | For collaborative arrangements |

### CanonPickLists

Reference data for editor dropdowns:

| Property | JSON Key |
|---|---|
| Forms | `forms` |
| Categories | `categories` |
| CatalogPrefixes | `catalog_prefixes` |
| KeyTonalities | `key_tonalities` |
| VoiceTypes | `voice_types` |
| Instruments | `instruments` |

When a user renames a pick-list value in any editor, the rename propagates to all pieces using that value. Renames are tracked via dictionaries (`FormRenames`, `CategoryRenames`, `CatalogRenames`, `KeyRenames`) and applied after dialog close.

### Album (`CanonAlbum`)

Represents a physical CD release the owner has ripped. Albums own one or more `AlbumDisc`s, each owning `AlbumTrack`s; tracks carry zero or more `TrackPieceRef`s linking them back into the canon piece tree.

| Field | Type | Notes |
|---|---|---|
| `Title` / `Subtitle` | string? | |
| `Label` / `CatalogueNumber` / `Barcode` | string? | Identifying release info |
| `SparsCode` | string? | "DDD", "ADD", etc. |
| `IsStereo` | bool? | |
| `Volumes` | List\<AlbumVolume\>? | For multi-disc box-set hierarchies |
| `Sessions` | List\<RecordingSession\>? | Date / venue / engineers / producers |
| `Performers` | List\<AlbumPerformer\>? | Album-level performer credits |
| `Discs` | List\<AlbumDisc\> | One entry per physical disc |

Each `AlbumTrack` carries `TrackNumber`, `Duration`, `Description`, `SparsCode`, `IsStereo`, `SessionIndex`, `Performers`, and `PieceRefs` (`List<TrackPieceRef>`). A `TrackPieceRef` is the (composer, piece-title, optional subpiece-path, optional version-description) tuple that resolves to a `CanonPiece` / `CanonPieceVersion` via `PieceReferenceIndex`.

### Inheritable album-level fields (`SparsCode`, `IsStereo`, `Performers`)

These three fields exist at both the album and track levels with the same shape. The semantic is **not** runtime inheritance — every track carries its own copy. The album editor is the chokepoint that keeps them in sync:

- When the user changes the album-level value in the album editor, `PropagateAlbumFieldsToTracks` (single-edit) and the equivalent block in `SaveMulti` push the new value to **every track on the album**, overwriting any prior track-level value. The intent: "set at album → propagate to all."
- For unchanged album fields, the same pass **backfills** any track whose value is still `null` with the album's current value. New tracks added in this session and any legacy null tracks therefore end up with explicit values, consistent with the "no Inherit" UI contract.
- Tracks whose value is non-null and whose album-level value was not changed are left alone, preserving prior track-level overrides.
- The Track editor edits each field on a single track only — never touches the album or other tracks.

**SPARS Code constraint:** the dropdown's permitted values are exactly `DDD`, `ADD`, `AAD`, `Unknown`. Both the album and track dropdowns are non-editable (so they share the gray styling of the other dropdowns in the app). `null` / empty values map to `Unknown` on display via `SparsCodeCombo.SelectValue`. Legacy non-standard codes are appended to the dropdown dynamically rather than being silently dropped.

### Provisional Status

Composers and pieces carry an `IsProvisional` flag (default `true`). It distinguishes data that has been auto-created (iTunes import, ad-hoc imports, in-code defaults) from data that has been explicitly reviewed and approved by the user.

- **Display:** Provisional items show a `(provisional)` suffix in the Canon view, beside the composer's lifespan or the piece's title.
- **Filter:** Each list has a three-state **Show** dropdown — *All* / *Provisional* / *Accepted* — bound to `ComposerProvisionalFilter` and `PieceProvisionalFilter` on `CanonViewModel`.
- **Approve / Reject:** Right-click a provisional composer or piece. *Approve* sets `IsProvisional = false` and saves. *Reject* prompts for confirmation, then hard-deletes the row.
- **Persistence:** `IsProvisional` is a real SQLite column (`composers.is_provisional`, `pieces.is_provisional`, both `INTEGER NOT NULL`). It is also exported in the JSON snapshots as `is_provisional` for round-trip via the seeder tool's `--export` / re-seed cycle.
- **Migration default:** When the `is_provisional` column is added to an existing database, the `DEFAULT 1` clause flips every pre-existing row to provisional. The contract is uniform — every row starts provisional and is opted into the canon by an explicit Approve, including data that pre-dated the flag.

### Album reference resolution (`PieceReferenceIndex`)

The runtime cross-reference between albums and the piece tree lives in a single singleton service. It builds three lookup tables on `Rebuild(pieces, albums)`:

- **`_byComposerTitle`** — composer-name → (normalized title → `IndexEntry(piece, setAncestors)`). Indexes every title variant a ref might use: `Title`, `DisplayTitle`, `DisplayTitleShort`, plus stripped-nickname/subtitle versions. **Set members are recursively re-indexed at top level under their own titles** so a ref like `"Piano Sonata #1 in f, Op. 2 #1"` resolves directly to the sonata, with movements addressable via `subpiece_path`.
- **`_hitsForPiece` / `_hitsForVersion` / `_hitsForOriginal` / `_hitsForComposer`** — domain models → list of `PieceAlbumHit`. Drives the album-count badges in the UI.

Set containers (`form: "set"` pieces) get their hit list computed by an `AggregateSetHits` post-pass: a set's badge counts only albums that contain *every* member of the set, computed by intersecting member-album sets. This prevents a partial-set album from incorrectly crediting the container.

Resolution is tolerant: bad refs are silently dropped at runtime (the seeder reports them in its summary instead).

---

## Data Persistence Architecture

### SQLite is the single source of truth

`ClassicalCanon.db` (EF Core 8 + Microsoft.Data.Sqlite) holds all canon data at runtime. The JSON files in `data/` are decoupled from the runtime entirely — they exist only as input to the seeder tool and as output of explicit export operations. Saving through the WPF app writes to SQLite and *only* to SQLite.

```
User edits piece in UI
  -> CanonViewModel.SavePiecesCommand
    -> SqliteCanonDataService.SavePiecesAsync()
      -> EF Core upsert to SQLite (only)
```

This is a deliberate departure from an earlier dual-write design (SQLite + JSON write-through) that allowed the two stores to silently diverge — see *Lessons Learned: JSON write-through divergence*.

### Schema (normalized)

The schema is defined by `CanonDbContext` and the row entities under `src/CDArchive.Core/Data/`. Tables and columns use `snake_case`. The schema is fully relational; large flexible substructures (Instrumentation, Roles, TextAuthor, etc.) are stored as JSON-blob columns on the parent row, but ownership relationships, catalog entries, tempos, composer credits, variants, and album-track piece-refs are all proper tables with foreign keys.

**Composers** — `composers`, `composer_aliases`, `composer_catalog_prefixes`. Indexed on `sort_name`.

**Pieces** — `pieces` (recursive: `parent_piece_id` for movements, `parent_version_id` for version-of-version movements), plus side tables `piece_versions`, `piece_catalog_entries`, `piece_tempos` (recursive), `piece_composer_credits`, `piece_variants`. Sort helpers `catalog_sort_prefix` / `catalog_sort_number` / `catalog_sort_suffix` are computed at save time from `CatalogInfo[0]` for efficient `ORDER BY`.

**Albums** — `albums`, `album_volumes`, `album_discs`, `album_tracks`, `album_track_piece_refs`, `album_performers` (with `track_id` nullable for album-level vs track-level performers), `album_sessions`.

**Pick lists** — `pick_list_values` keyed by `list_name`/`position`.

**Multi-owner CHECK constraints.** Several tables have a row that may be owned by one of several principals — for example `piece_catalog_entries` belongs to either a piece or a version, and `piece_tempos` belongs to a piece, version, or another tempo (sub-tempo). These tables carry CHECK constraints requiring exactly one of the owner FKs to be non-null. The save path explicitly `Remove()`s orphaned rows before clearing navigation collections, because EF nulls the FK on orphan and would otherwise create rows that violate the CHECK.

**Album save uses load-mutate-save**, not delete-and-rebuild. `SaveAlbumsAsync` eager-loads each existing album's full graph, matches input to existing rows by natural key at every level (volume Number, disc DiscNumber, track TrackNumber, performer/session/piece-ref Position), and applies the minimal UPDATE / INSERT / DELETE diff in one transactional `SaveChangesAsync`. Row IDs survive content edits; a constraint violation rolls back without touching unrelated rows. See *Lessons Learned: Album save: load-mutate-save, not delete-and-rebuild* for the full rationale and the trap that prompted it.

### Database lifecycle

1. **Schema creation.** `SqliteCanonDataService.EnsureInitializedAsync()` is called lazily on the first `Load*Async` / `Save*Async`. It creates the schema if absent. **It does not auto-seed.** It then runs `ApplySchemaUpgradesAsync`, which uses `PRAGMA table_info` to detect missing columns and issues idempotent `ALTER TABLE` statements (e.g. the `is_provisional` columns added after the initial schema snapshot). These upgrades are append-only and safe to run on every startup; they let the model evolve without manual EF migrations.
2. **Initial population.** Run the seeder tool once on a clean checkout:
   ```
   dotnet run --project tools/CDArchive.Tools.SeedDb
   ```
   The seeder reads the four JSON files in `data/`, builds the full piece tree + album cross-references via `PieceReferenceIndex`, and populates the SQLite database. It reports counts and any unresolved refs.
3. **Normal operation.** Reads and writes go to SQLite only.
4. **Recovery.** Delete `data/ClassicalCanon.db` and re-run the seeder. The JSON files in `data/` serve as the recovery source; keep them up to date with periodic `--export` runs (see below).

### Backing up to JSON: `--export` mode

```
dotnet run --project tools/CDArchive.Tools.SeedDb -- --export
```

Loads everything from SQLite via `SqliteCanonDataService` and writes the four canonical JSON files via `CanonDataService` directly. The two services are deliberately decoupled:

- `SqliteCanonDataService` reads/writes only SQLite.
- `CanonDataService` reads/writes only JSON.
- `--export` is the explicit composition of the two ("load from DB" → "save to JSON"). It is the *only* path that converts SQLite → JSON; nothing else does, ever.

This composition rule is tested by `SaveOperations_DoNotTouchJsonFiles` (round-trips every subsystem through `SqliteCanonDataService.Save*Async`, asserts JSON-file mtimes are unchanged).

### Surgical albums-only recovery: `--restore-albums` mode

```
dotnet run --project tools/CDArchive.Tools.SeedDb -- --restore-albums
```

Loads albums from `data/Classical Canon albums.json` and pushes them into the existing SQLite database via `SqliteCanonDataService.SaveAlbumsAsync` (which is now the load-mutate-save merge path — see *Album save: load-mutate-save* in Lessons Learned). Composers, pieces, and pick lists are left untouched. Used as a less-destructive alternative to the full-reseed recovery when only the albums data needs restoring (e.g. after an albums-table corruption that didn't touch the canon).

Track-piece refs are re-resolved against the current piece tree on save, so the recovery works even when the DB's piece tree has drifted from the JSON's reference state since the snapshot was taken.

### Inspecting the database

`ClassicalCanon.db` is a standard SQLite 3 file. You can open it with any SQLite tool (DB Browser for SQLite, SQLiteStudio, DataGrip, the `sqlite3` CLI, the VS Code SQLite extension, Chrome SQLite browser extensions). Two important caveats:

- **Close the WPF app first** to release the file lock before editing.
- **Journal mode must stay `delete`.** Browser-based tools (sql.js / WASM) cannot read WAL-mode databases. If something flips the file to WAL mode (look for `.db-wal` / `.db-shm` sidecar files appearing), restore it with:
  ```
  sqlite3 data/ClassicalCanon.db "PRAGMA journal_mode=DELETE;"
  ```
  The setting is persisted in the file header and survives subsequent app runs. We don't enable WAL anywhere in our own code, but a third-party tool might.

Direct edits to the DB persist as expected — the next app save will simply update the modified rows. They do **not** propagate to the JSON files; run `--export` afterward if you need the JSON snapshots refreshed.

### Data directory resolution

Both `CanonDataService` (parameterless ctor) and `tools/CDArchive.Tools.SeedDb`'s `FindRepoRoot()` walk up from the assembly location looking for a `data/` folder containing **either** `Classical Canon composers.json` **or** `ClassicalCanon.db`. Either marker is sufficient — that way the resolver still finds the data directory after the JSON files have been deleted, and after the database has been deleted too (if the JSONs are present).

---

## Navigation Architecture

The application uses a single-window shell with a left navigation bar and a right content area.

**Critical design decision**: `CanonView` (the Composers screen) is a **permanently alive element** in `MainWindow.xaml`. It is never destroyed or recreated -- it is shown/hidden via `Visibility` binding to `MainViewModel.IsCanonViewActive`. All other views are rendered via `ContentControl` + `DataTemplate` and are created fresh on each navigation.

This design was adopted because WPF's `DataTemplate` pattern creates a new `UserControl` instance on every navigation, which caused the composer tree to appear empty when navigating back -- the new view's `Loaded` event raced with background data loading in ways that were impossible to resolve reliably.

```xml
<!-- MainWindow.xaml content area -->
<Grid>
    <views:CanonView DataContext="{Binding CanonViewModel}"
                     Visibility="{Binding DataContext.IsCanonViewActive,
                                  RelativeSource={RelativeSource AncestorType=Window},
                                  Converter={StaticResource BoolToVis}}" />
    <ContentControl Content="{Binding CurrentView}" />
</Grid>
```

**Why the `RelativeSource` binding**: Because `CanonView` has `DataContext="{Binding CanonViewModel}"`, any binding on that element resolves against `CanonViewModel` by default. The `Visibility` binding must reach up to `MainViewModel` (on the Window) to find `IsCanonViewActive`, so it uses `RelativeSource={RelativeSource AncestorType=Window}`.

---

## Import / Export

The Import/Export screen (`ImportExportViewModel`) provides explicit, user-driven JSON ↔ SQLite operations. None of these run automatically — JSON output and JSON-input restore are always deliberate steps.

| Operation | Behavior |
|---|---|
| **Export Composers** | Reads composers from SQLite and writes them to a user-chosen JSON file (default location: the canonical path). |
| **Export Pieces** | Same, for pieces. |
| **Normalise** | Loads each subsystem from SQLite and saves it back. Re-applies derived ordering (catalog sort prefix/number/suffix, composer-preferred catalog ordering). Touches SQLite only — the JSON files are not affected. |
| **Import Composers** | Merge-only: deserialises a user-picked JSON file and adds composers not already in the DB (matched by `Name`, case-insensitive). Existing rows are never overwritten. |
| **Import Pieces** | Merge-only: deserialises a user-picked JSON file and adds pieces not already in the DB (matched by `Composer` + `Title` composite key, case-insensitive). Existing rows are never overwritten. |
| **Restore from JSON** | Lets the user pick replacement JSON files. Loads them into the SQLite store, replacing the current data. The on-disk JSON files are not modified — data flows JSON → SQLite only. |

**Import format**: Both single-object `{}` and array `[]` JSON are accepted. `JsonDocument.Parse` detects the root element kind before deserialization.

**There is no "Sync to Canonical JSON" button anymore.** Use the seeder tool's `--export` mode for that (see *Backing up to JSON* above). The previous design's automatic bidirectional sync is what allowed JSON ↔ SQLite divergence; explicit user-triggered export is the safer alternative.

---

## iTunes Integration

iTunes serves as a reference catalogue source for composer biographical data and work titles. It is the second priority in a three-tier lookup chain:

1. **LocalCatalogueReference** -- User's manually entered catalogue data (highest priority)
2. **ItunesLibraryReference** -- Parsed from the iTunes Music Library XML file
3. **MusicBrainzReference** -- External API fallback (lowest priority)

### How it works

`ItunesLibraryReference` parses the user's `iTunes Music Library.xml` file (auto-discovered at `%MUSIC%\iTunes\`) and indexes tracks whose file path contains `CD%20archive` (i.e., tracks ripped from the user's CD collection).

For each indexed track, it extracts:
- **Composer**: Parsed from the metadata field using the pattern `"LastName, FirstName (YYYY-YYYY)"` -- extracting name parts, birth year, and death year.
- **Work/Movement**: Parsed from the track name using the pattern `"Work Title - Movement #. Movement Name"`.

The library is loaded lazily (first access only) and cached for the session lifetime.

### Importing works from the iTunes library

To import works from your iTunes library into the Canon database:

1. The `ItunesLibraryReference` service automatically reads your iTunes Music Library XML file on first access.
2. The `CompositeCatalogueReference` chains this with other sources, making iTunes data available when local catalogue data doesn't already cover a work.
3. To add works to the Canon that were identified through iTunes, use the **New Piece** button in the Composers view, or prepare a JSON file with the pieces and use **Import Pieces** from the Import/Export screen.

To prepare a JSON import file from iTunes data:
- Export your iTunes library metadata (or use the app's cataloguing features to look up works).
- Format pieces as JSON objects matching the `CanonPiece` schema (see Data Model section above).
- Import via Import/Export > Import Pieces. The import is merge-only -- existing pieces (matched by composer + title) are never overwritten.

**Example import JSON (single piece):**
```json
{
    "composer": "Beethoven, Ludwig van",
    "title": "Sonata",
    "form": "Sonata",
    "number": 14,
    "key_tonality": "C-sharp",
    "key_mode": "minor",
    "nickname": "Moonlight",
    "catalog_info": [{"catalog": "Op.", "catalog_number": "27", "catalog_subnumber": "2"}],
    "instrumentation_category": "Piano",
    "instrumentation": ["Piano"],
    "publication_year": 1801,
    "numbered_subpieces": true,
    "subpieces": [
        {
            "title": "Adagio sostenuto",
            "number": 1,
            "tempos": [{"description": "Adagio sostenuto"}]
        },
        {
            "title": "Allegretto",
            "number": 2,
            "tempos": [{"description": "Allegretto"}]
        },
        {
            "title": "Presto agitato",
            "number": 3,
            "tempos": [{"description": "Presto agitato"}]
        }
    ]
}
```

Both single objects `{}` and arrays `[]` are accepted on import.

---

## Music Player

Persistent transport bar at the bottom of `MainWindow`, iTunes-classic in style: ⏪ / ▶⇄⏸ / ⏩ buttons (10-second seek, not prev/next-track), a progress slider with elapsed/remaining time labels, and a centred "Title — Composer — Album" line. Greyed out (opacity 0.5, controls disabled, slider hidden) when no track is loaded.

### Three-layer architecture

| Layer | Service / class | Responsibility |
|---|---|---|
| File resolution | `IArchiveAudioLocator` / `ArchiveAudioLocator` | Maps `(CanonAlbum, AlbumDisc, AlbumTrack)` → absolute file path via override-or-convention. |
| Audio engine | `IAudioPlayerService` / `NAudioPlayerService` | NAudio 2.2.1 + `MediaFoundationReader` (decodes MP3 and FLAC natively on Win10 1709+ / Win11) + `WaveOutEvent`. Captures `SynchronizationContext.Current` at construction so its events come back on the UI thread. |
| ViewModel + UI | `PlayerViewModel` (singleton) / `Views/PlayerBar.xaml` | Mirrors player state, holds playback context (album + flattened track sequence + index), drives the bar. |

Locator + audio service registered in `ServiceCollectionExtensions.AddCoreServices()`; VM in `App.OnStartup`. All three are singletons so playback state survives navigation between views.

### Locator resolution order

Per track, returning null when nothing matches:

1. **Per-track override** — `track.FlacPath` / `track.Mp3Path` (absolute paths). Preferred format first, then the other.
2. **Convention** — `{archiveRoot}/{albumFolder}[/{discFolder}]/{FLAC|MP3}/{NN}*.{flac|mp3}` where `NN` is the zero-padded track number.
3. Returns null otherwise — UI shows a MessageBox on manual play; auto-advance silently skips.

Where the pieces come from:
- **Archive root**: `IArchiveSettings.ArchiveRootPath` (defaults to `D:\CD archive`).
- **Album folder**: `CanonAlbum.ArchiveFolder` if set, else `CanonAlbum.Title`. An absolute path in `ArchiveFolder` is used as-is and bypasses the root.
- **Disc folder**: `AlbumDisc.FolderName` if set; else `"Disc {DiscNumber}"` when multi-disc; else omitted (single-disc albums put `FLAC/`/`MP3/` directly under the album folder).

The "folder name = album Title" default means most albums need no explicit `ArchiveFolder`. The override field on the album editor only matters when the on-disk folder name diverges from the display title.

### Preferred format

`IArchiveSettings.PreferredAudioFormat` (enum `Flac | Mp3`, default `Flac`). UI: Settings tab → "Player Format". Locator returns the preferred format when present, falls back to the other if it's missing. Change affects newly-loaded tracks only — a currently-playing track keeps its format until it ends.

### Schema additions

Idempotent `ALTER TABLE`s appended to `ApplySchemaUpgradesAsync`:

- `albums.archive_folder TEXT NULL`
- `album_discs.folder_name TEXT NULL`
- `album_tracks.flac_path TEXT NULL`
- `album_tracks.mp3_path TEXT NULL`

Matching nullable C# properties (`ArchiveFolder` / `FolderName` / `FlacPath` / `Mp3Path`) on the JSON models, EF row classes, and Save/Load mappers. Round-trip-safe via the seeder.

### Entry points

- **AlbumsView → right-click album → "Play album"** — starts from track 1, auto-advances across the album.
- **AlbumEditor track list → right-click track → "Play this track"** (single track, no advance) or **"Play from here"** (this track + rest of album).
- **Space-bar = play/pause** at the window level, suppressed when a `TextBoxBase` / `ComboBox` / `PasswordBox` has focus.

### Auto-advance

`PlayerViewModel` holds the current album + flat track sequence (ordered by `VolumeNumber` → `DiscNumber` → `TrackNumber`) + current index. On `IAudioPlayerService.PlaybackEnded`, the VM tries the next index; if the locator can't find a file it silently skips forward. End-of-album just stops at the last successful track's end position. `PlaySingleTrack(...)` nulls the playback context after starting, so single-track playback is a one-shot (no advance).

### Per-track file overrides

For tracks that escape the convention (e.g. standalone MP3s in `C:\Users\james\Music\Yourclassical Daily Download saved\…`), set `track.FlacPath` / `track.Mp3Path` to absolute paths. UI lives in the TrackEditor's "Audio file overrides" GroupBox with Browse buttons; the GroupBox is disabled in multi-edit mode (per-track values don't bulk-edit meaningfully).

---

## Tracks list and loose tracks

Cross-album Tracks view (`TracksView` + `TracksViewModel`) shows every track in the catalogue as flat rows. Loose tracks — singletons that don't belong to any album — coexist with album-bound tracks in the same UI.

### Loose-track data model

A loose track is an `AlbumTrack` instance stored in the same `album_tracks` table as an album-bound track, but with `disc_id = NULL` and `track_number = 0` as sentinels. There is no separate "loose_tracks" table; the existing schema accommodates both shapes via nullable FKs.

| Column | Album-bound | Loose |
|---|---|---|
| `disc_id` | set (FK to `album_discs.id`) | **NULL** |
| `track_number` | position within disc (≥1, unique per disc) | **0** (sentinel, no position) |
| `session_id` | optional FK into the parent album's sessions | always NULL (no album sessions) |
| `flac_path` / `mp3_path` | optional override on the convention | typically required (no archive-folder convention to fall back to) |

Loose-track piece-refs and performers reuse the existing `album_track_piece_refs` and `album_performers` tables. For performers specifically: `album_performers.album_id` was migrated to nullable so a loose-track-only credit can anchor on `track_id` alone (album_id NULL). A new `ck_album_performers_has_owner` CHECK constraint guarantees every performer row points at at least one of album/track — no floating credits.

The full schema migration (album_tracks.disc_id + album_performers.album_id from NOT NULL to nullable) runs idempotently on every startup via `EnsureColumnNullableAsync` → `RecreateXxxAsync` helpers in `SqliteCanonDataService.ApplySchemaUpgradesAsync`. The recreate helpers follow SQLite's recommended dance: `PRAGMA foreign_keys=OFF` → transactional `CREATE … _new` → explicit-column `INSERT SELECT` → `DROP` → `RENAME` → recreate indexes → `PRAGMA foreign_key_check` as a sanity gate → `COMMIT` → `PRAGMA foreign_keys=ON`. Once a column is already nullable the helper no-ops.

### Storage shape

- DB: same `album_tracks` table; `disc_id IS NULL` filters loose tracks.
- JSON: a new top-level file `data/Classical Canon loose tracks.json` (array of `AlbumTrack`). The data-directory resolver still finds the data dir via `composers.json` or `ClassicalCanon.db`; the new file is independent.
- API: `ICanonDataService.LoadLooseTracksAsync` / `SaveLooseTracksAsync` + `LooseTracksFilePath`. Identity tracked via `_looseTrackIds` `ConditionalWeakTable` mirroring the album-side `_albumIds`.

### PieceReferenceIndex

`PieceAlbumHit.Album` and `.Disc` are nullable — null means the hit comes from a loose track. `PieceReferenceIndex.Rebuild` and the new `RebuildContainers` accept an optional `looseTracks` enumerable. The badge count uses `DistinctContainerCount`: an album is one container regardless of how many of its tracks reference the piece; each loose track is its own container. Set aggregation (`form: "set"`) ignores loose-track hits — a single track can't satisfy "every member of a set".

### Tracks list UI

- **Columns**: Album, Disc, Track, Piece (with provisional badge), Time, Composer, Artist. Disc/Track empty for loose tracks; Disc also empty for single-disc albums (the consolidation rule lives in `AlbumTrackRow.DiscDisplay`). The Piece column shows the resolved top-level piece's `DisplayTitle` (with catalogue / nickname) joined to the subpiece path with `" › "`, e.g. `Piano Sonata #17 in d, Op. 31 #2 "Tempest" › 2. Adagio`. Precomputed at row-build time via `TracksViewModel.FormatPiece`.
- **Sort**: every column is sortable; default Album→Disc→Track. After a sort, the first-selected row scrolls to the top of the viewport (anchor behaviour).
- **Filter / Show**: text filter searches Piece / Composer / Album / Artist / Description; "Show: All / Provisional / Accepted" filters by `IsProvisional`.
- **Double-click dispatch**: album-bound row → parent album editor (with the JSON-clone swap); loose row → loose-mode track editor.
- **Toolbar buttons**: "New Track" (creates a fresh loose track via loose-mode `TrackEditorWindow`) and "Edit" (single album-bound → album-track editor; single loose → loose-mode editor; multi → bulk editor with `sessions: null` when the selection mixes albums or includes loose).
- **Context menu**: Approve and Reject. Approve is enabled when at least one selected row is still provisional; Reject is always enabled (with a confirmation dialog that breaks out album-bound vs loose counts). Both run through `TracksViewModel.ApproveRowsAsync` / `RejectRowsAsync`, which call `TrackCascade` for the in-memory mutation and then save only the affected stores.

### TrackEditorWindow loose mode

A third constructor `TrackEditorWindow(AlbumTrack, CanonPickLists, IReadOnlyList<CanonPiece>)` opens loose mode. The window hides `NavigationPanel` (no prev/next within a disc), `TrackNumberLabel` / `TrackNumberBox` (no disc position), and `SessionLabel` / `SessionPanel` (no album sessions). The audio-overrides group stays visible — for a loose track those paths are typically required since there's no album-folder convention. Commit writes back to the supplied instance in place with `TrackNumber = 0` and `SessionIndex = null`.

### iTunes import

`ItunesImporter.Import` partitions input rows via `PartitionByAlbum` and produces loose tracks for albumless rows instead of synthetic single-track albums. The per-track piece-ref / composer resolution moved into a shared `PopulatePieceRefs` helper that both paths call. `ImportResult` now carries `NewLooseTracks` alongside `NewAlbums`; the view-model saves both.

### Migration tool

`tools/CDArchive.Tools.SeedDb -- --promote-loose-tracks` (defaults to dry-run; add `--apply` to commit) scans for synthetic wrapper albums and promotes them. Heuristic in `SqliteCanonDataService.ClassifyForPromotion`: 1 disc, 1 track, no Volumes, no Sessions, no Label / CatalogueNumber / Barcode / ArchiveFolder, no track-level Performers. For each match: re-anchor album-level performers onto the track, inherit album-level SparsCode/IsStereo when the track's are null, set the track's `disc_id=NULL, track_number=0, session_id=NULL`, then delete the disc and album rows. All in one transaction.

### Cascade helpers (Core)

Two pure-Core helpers live next to `SqliteCanonDataService` so the cascades are unit-testable without WPF:

- **`CanonRejectCascade`** — `RejectComposerAsync` / `RejectPieceAsync`. Strips refs from both albums and loose tracks (FK chain: composer ← pieces ← album_track_piece_refs is OnDelete:Restrict at every step), then saves in dependency order. Used by `CanonViewModel.Reject*WithCascadeAsync`.
- **`TrackCascade`** — `Approve(tracks)` clears `IsProvisional`; `Reject(entries, looseTracks)` removes each `(track, disc?)` entry from its owner (disc's Tracks for album-bound, loose list for loose). Used by `TracksViewModel.ApproveRowsAsync` / `RejectRowsAsync`.

---

## Editor Windows

The application has four editor dialog windows, all modal:

| Editor | Model | Launched From |
|---|---|---|
| ComposerEditorWindow | CanonComposer | CanonView (New/Edit Composer) |
| PieceEditorWindow | CanonPiece | CanonView (New/Edit Piece) |
| MovementEditorWindow | CanonPiece (subpiece) | PieceEditorWindow or nested |
| VersionEditorWindow | CanonPieceVersion | PieceEditorWindow or MovementEditorWindow |

### Field availability

PieceEditorWindow, MovementEditorWindow, and VersionEditorWindow all share the full set of piece fields: Title, TitleEnglish, Subtitle, Nickname, Form, Number, MusicNumber, Key/Mode, Category, Catalogue (multi-entry), Instrumentation, Publication Year, Composition Years, Text Author, FirstLine, Roles, Tempos, Numbered Subpieces (with Start number), Subpieces list, and Versions list.

### Pick-list rename propagation

When a user renames a value in a pick list (e.g., renaming a Form from "Concertino" to "Concertino for Orchestra"), the rename is tracked in dictionaries (`FormRenames`, `CategoryRenames`, `CatalogRenames`, `KeyRenames`). After the editor dialog closes, the caller applies these renames to all pieces in the dataset, ensuring consistency.

### Subpiece numbering

- `NumberedSubpieces` (bool?) controls whether subpieces display a number prefix. Default behavior depends on category: Opera-like categories default to `false` (scenes aren't numbered); everything else defaults to `true`.
- `SubpiecesStart` (int?) sets the starting number. Defaults to 1 but can be changed (e.g., a set of preludes numbered 13-24 would have `SubpiecesStart = 13`).
- `RenumberSubpieces()` assigns sequential numbers starting from `SubpiecesStart`.
- Both fields serialize to JSON only when non-default (`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`).

---

## Lessons Learned

### WPF DataTemplate view lifecycle

**Problem**: WPF's `ContentControl` + `DataTemplate` pattern creates a **new UserControl instance** every time the content changes. This means navigating away from and back to the Composers screen destroys the old `CanonView` and creates a fresh one. The new view's `Loaded` event races with background data loading, causing the tree to appear empty.

**Solution**: Make `CanonView` a permanent element in `MainWindow.xaml`, shown/hidden via `Visibility` binding. This eliminates the recreation problem entirely.

**Corollary**: If you explicitly set `DataContext` on an element, all bindings on that element (including `Visibility`) resolve against the new DataContext. Use `RelativeSource={RelativeSource AncestorType=Window}` to reach the Window's DataContext for properties that live on the parent ViewModel.

### AsyncRelayCommand silent no-ops

**Problem**: `CommunityToolkit.Mvvm.Input.AsyncRelayCommand` returns `CanExecute = false` while the command is already executing. If a navigation command is async and the user clicks it while data is loading, the click is silently ignored.

**Solution**: Keep navigation commands synchronous. Fire data loading with fire-and-forget: `_ = viewModel.LoadDataCommand.ExecuteAsync(null)`.

### PropertyChanged subscriptions on detached views

**Problem**: When `DataTemplate` creates a new view, the old view's `PropertyChanged` handler is still subscribed to the ViewModel. The old handler fires on a detached visual tree (invisible controls), while the new view may miss the event entirely.

**Solution**: The permanent-view pattern (above) eliminates this. If you must use DataTemplate-created views, unsubscribe in `Unloaded` and subscribe in `Loaded`, with careful attention to timing.

### PowerShell corrupts UTF-8 files (mojibake)

**Problem**: PowerShell's `Set-Content` uses the system default encoding (often cp1252 on Windows) rather than UTF-8. Running a find-and-replace on a UTF-8 JSON file via PowerShell silently corrupts all non-ASCII characters: `e` becomes `Ã©`, `flat` becomes `â™­`, `a` becomes `Ã `, etc.

**Solution**: Always use `-Encoding UTF8` with PowerShell file operations, or use Python/C# for text manipulation on UTF-8 files. To reverse existing mojibake, use the Python round-trip: `corrupted.encode('cp1252').decode('utf-8')` with a length check to skip already-correct characters. Some sequences (notably `a` stored as `Ã` + JSON-escaped `\u00A0`) require a targeted second pass.

**Prevention rule**: Never use PowerShell `Set-Content` or `Out-File` on the canonical JSON files without explicit `-Encoding UTF8`. Prefer the application's own Export/Sync functions for all data file updates.

### BAML-generated field name collisions

**Problem**: WPF's BAML compiler generates fields for named elements. If a code-behind method has the same name as a XAML `x:Name`, you get `CS0102: duplicate member` at build time.

**Solution**: Use distinct names. In this project, a method named `SortPieces` collided with a XAML element `x:Name="SortPieces"`. The method was renamed to `OrderPieces`.

### DockPanel LastChildFill

**Problem**: `DockPanel` stretches its last child to fill remaining space by default. This caused a small `TextBox` (like the SubpiecesStart field) to stretch across the full width.

**Solution**: Set `LastChildFill="False"` on the `DockPanel`.

### JSON write-through divergence

**Problem**: An earlier design had every `SqliteCanonDataService.SaveXxxAsync` perform a JSON write-through after persisting to SQLite, on the theory that the JSON files would always mirror the DB. In practice, anything that wrote SQLite without going through that exact code path (the seeder, an ad-hoc tool, a test) created an inconsistency window. Worse, a bug in the round-trip path (`BuildTrackPieceRef` losing movement-level subpaths) silently corrupted the JSON files on every export-then-save, gradually replacing rich movement-level refs with sonata-only refs.

**Solution**: SQLite is the sole source of truth. `SqliteCanonDataService` only writes SQLite. JSON I/O happens only through `CanonDataService`, only at explicit user request (Import/Export screen, seeder tool). The architectural invariant "saves don't touch JSON" is locked in by the `SaveOperations_DoNotTouchJsonFiles` test, which round-trips every subsystem through `Save*Async` and asserts JSON-file mtimes are unchanged.

### Set member title collisions in `PieceReferenceIndex`

**Problem**: `PieceReferenceIndex.RegisterPiece` uses `titleMap.TryAdd(...)` to register every title variant a piece might be referenced by. This silently dropped duplicate keys. Beethoven has at least four "Set" pieces whose `DisplayTitleShort` is "Three Piano Sonatas" (Op. 2, Op. 10, Op. 31, WoO 47); only the first to register won the key and the rest became unreachable via that title.

This collided with an earlier shape of `BuildTrackPieceRef` that walked all the way up to the set container, producing refs of shape `(set-DisplayTitleShort, [member-title])`. With the wrapper title ambiguous, every Beethoven set except the first one ended up resolving to the wrong set, and album badges showed zero for them.

**Solution**: `BuildTrackPieceRef` now stops the walk at set boundaries, so the ref addresses the set member directly using its catalog-bearing title (e.g. `"Piano Sonata #1 in f, Op. 2 #1"`) — which is unambiguous. Set members are still registered at top level by `RegisterPiece`'s recursion into set containers; the title key still collides for `DisplayTitleShort`, but the longer `DisplayTitle` (with catalog) doesn't, and that's the one the ref carries.

### Movement-level resolution: `TryResolve` must return the leaf

**Problem**: `PieceReferenceIndex.TryResolve()` used to return `entry.Piece` (the title-lookup top), not the leaf after walking the subpath. The runtime `AddHitForRef` path uses an internal overload that exposes the full `ancestorSubpieces` list, so it credited movements correctly. But the seeder calls the public `TryResolve`, so it stored `piece_id = sonata` for every ref, even those with movement subpaths. When the data round-tripped through SQLite, the movement information was gone — every track on the album pointed at the sonata, and movement-level badges showed zero hits.

**Solution**: The public `TryResolve` now returns the leaf — `ancestorSubpieces[^1]` if the subpath was walked, otherwise `entry.Piece`. The seeder stores movement-level `piece_id`s, and `BuildTrackPieceRef` reconstructs the subpath correctly on the way back out.

### EF Core orphan rows + multi-owner CHECK constraints

**Problem**: `piece_catalog_entries` belongs to either a piece or a version (both FKs nullable, with a CHECK constraint requiring exactly one to be non-null). When a save replaces a piece's catalog entries, the natural pattern is `row.CatalogEntries.Clear(); ... row.CatalogEntries.Add(...)`. EF Core sees the cleared rows as orphans and — because the FK is nullable — sets `piece_id = null` rather than deleting them. Both FKs now null violates the CHECK and the save fails with `SQLITE_CONSTRAINT_CHECK`. The same trap exists for `piece_tempos` (three nullable owners) and the credits / variants tables.

**Solution**: In `SqliteCanonDataService.Replace*` methods, explicitly `db.Remove(existing)` each row before clearing the navigation collection. Tempos additionally need recursive removal (`RemoveTempoTree`) because sub-tempos are themselves `PieceTempoRow`s with their own multi-owner CHECK.

### SQLite WAL mode breaks browser-tool access

**Problem**: SQLite's WAL (write-ahead logging) journal mode creates `-wal` and `-shm` sidecar files for pending writes. Browser-based SQLite tools (which use `sql.js`, a WASM build) can't read WAL-mode databases — they fail with `SQLITE_CANTOPEN`. Even after the sidecars are merged, the file header remembers it's a WAL-mode database and `sql.js` still refuses.

**Solution**: Keep `journal_mode = DELETE` (SQLite's actual default). Our code doesn't explicitly set WAL anywhere. If something flips it (a third-party tool, a user pragma), restore it with `sqlite3 data/ClassicalCanon.db "PRAGMA journal_mode=DELETE;"`. The setting persists in the file header. Symptoms to watch for: `.db-wal` / `.db-shm` files in `data/`.

### Multi-composer pieces have no primary composer field

**Problem**: Some pieces are collaboratively composed (e.g. *L'éventail de Jeanne*, a ballet by 10 French composers). The current data model stores the primary composer in `CanonPiece.Composer` and additional contributors in `Composers` (List\<ComposerCredit\>). Pieces with no single primary composer have an empty `Composer` field. The seeder's `TryGetComposerId` skips such pieces silently, dropping them from the DB.

**Status**: Open. Workarounds: model these as belonging to a sentinel "Various" composer, or extend the seeder to handle composerless pieces using the contributor list. Until then, `L'éventail de Jeanne` and any similar pieces are missing from the DB.

### Album identity loss across the editor's JSON-clone

**Problem**: `AlbumEditorWindow` JSON-serialises the input album into a fresh `CanonAlbum` for editing (so Cancel doesn't mutate the original). On OK it exposes the clone via `Result`, and `AlbumsView.xaml.cs` substitutes the clone for the original in `vm.AllAlbums`. The data service tracks album identity via `ConditionalWeakTable<CanonAlbum, IdHandle>` keyed on the in-memory model instance, so the substituted clone has *no CWT entry* — and the save path falls back to `CanonAlbum.IdentityKey` (a composite of identifying fields) to find the matching DB row. The original `IdentityKey` was just `Label|CatalogueNumber`, returning null when either was missing, so albums without label/catalogue (Böhm Beethoven cycles, Bernstein Mahler, etc. — common in the user's collection) skipped the dedup and inserted a fresh row on every edit, leaving the original untouched as a duplicate.

**Solution**: `IdentityKey` now folds in `Title|Subtitle` as well, so any album with a non-empty title gets a stable lookup key. The dedup rule on the data-service side (`existingByKey` in `SqliteCanonDataService.SaveAlbumsAsync`) builds the same composite. Locked in by `AlbumIdentityTests.SaveTwice_AfterJsonCloneAndEdit_DoesNotDuplicate`.

**Renames (Title change in the editor)**: With the load-mutate-save album-save path (see *Album save: load-mutate-save, not delete-and-rebuild*) the IdentityKey lookup misses on the renamed clone, the old row is orphan-deleted in the same transaction, and the renamed album inserts fresh. End state is one album, not two. Row IDs do churn on a Title rename — if we ever need rename-stable row IDs (e.g. to keep audit timestamps), the fix is a model-side row-pointer that survives the editor's JSON-clone (snapshot/restore for Cancel, or a CWT-rebind API). Not currently warranted.

### Album save: load-mutate-save, not delete-and-rebuild

**Problem**: The original `SqliteCanonDataService.SaveAlbumsAsync` did a per-album "delete the existing row, reinsert from scratch" in two non-atomic `SaveChangesAsync` calls. Any constraint violation in the second call wiped the albums table, since the first call's deletes were already committed. Observed when an iTunes import of 11 standalone tracks lumped them into one synthetic `"(Unknown album)"` and produced 11 tracks all numbered #1 — violating `UNIQUE(disc_id, track_number)` on the re-insert and leaving the table empty.

Two design issues compounded the failure:
1. Two separate transactions made the operation non-atomic.
2. Even after wrapping them in one transaction, every save still threw away and reassigned every album / disc / track / piece-ref / performer row ID. Renaming an album rewrote 2000 rows; reordering one track inside one disc rewrote the entire album.

**Solution**: Load each existing album's full graph via `.Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.PieceRefs)` (and similarly for Volumes, Sessions, Performers), match input albums to existing rows by CWT identity or `IdentityKey`, then **merge in place** by natural key at every child level:

| Collection | Natural key (matches the schema's existing UNIQUE/HasIndex column) |
|---|---|
| `album.Volumes`    | `Number` |
| `album.Sessions`   | `Position` |
| `album.Performers` (album-level, `TrackId IS NULL`) | `Position` |
| `album.Discs`      | `DiscNumber` |
| `disc.Tracks`      | `TrackNumber` |
| `track.PieceRefs`  | `Position` |
| `track.Performers` | `Position`, scoped to `TrackId = track.Id` |

For each child collection: update matched rows in place (scalar fields + FK navigation rewires), insert new rows for unmatched inputs, `db.X.Remove(...)` the orphans. EF Core's change tracker emits the minimal UPDATE / INSERT / DELETE diff in a single `SaveChangesAsync`, wrapped in a transaction.

Two ordering subtleties:
- **Volume orphan deletes are deferred** until after `MergeDiscs` has rewired each disc's `Volume` navigation. Discs reference volumes via `OnDelete(DeleteBehavior.Restrict)`, so deleting a volume while discs still reference it fails the FK check.
- **Session orphan deletes are deferred** for the same reason — `track.Session` is `OnDelete(SetNull)`, so a premature delete would silently null out tracks that should be repointed at a surviving session.

Locked in by `AlbumSaveInPlaceTests` — five tests covering: row IDs preserved on content-edit, row IDs preserved when adding a track, row IDs preserved when removing a track, constraint violation rolls back without wiping, multi-album batch save only touches the edited album. Empirical: a no-op load-from-JSON-save-to-SQLite round-trip against the full 99-album / 2208-track DB leaves every row ID byte-identical.

**When to extend this pattern**: any other persistence method with the "delete the parent's children, reinsert from input" shape should consider the same refactor. Look for `ToList()` + `Clear()` + repopulation patterns in `SqliteCanonDataService`'s `Replace*` methods; those are candidates.

### `ToggleButton` styles can't `BasedOn` a `Button` style

**Problem**: `ToggleButton` is *not* a `Button` — they share `ButtonBase` as a common ancestor but neither inherits from the other. WPF's `Style.BasedOn` requires the derived style's `TargetType` to be assignable to the base style's `TargetType`, so a `Style TargetType="ToggleButton" BasedOn="{StaticResource TransportButtonStyle}"` (where `TransportButtonStyle` targets `Button`) silently produces a runtime mismatch and the toggle visuals fall back to the WPF default Aero look — no shared template, no shared setters.

**Solution**: write a parallel style with the same setters and a `Trigger Property="IsChecked"` for the active-state visuals. Locked in by `TransportToggleButtonStyle` in `PlayerBar.xaml`, which carries the same transparent / hover / disabled visuals as `TransportButtonStyle` plus a warm-yellow `IsChecked=True` background. The duplication is annoying but unavoidable until the styles are refactored onto a shared `ButtonBase` target (which then needs visual-state setup for both Press / Hover and Check).

### WPF mutate-then-save handlers: rebuild the tree *before* the save's await

**Problem**: `OnContextApprove` in `CanonView.xaml.cs` (both the composer and piece branches) originally ran `mutate → suppress → await save → ApplySortedFilter`. After approving a composer, the expander triangles for *every* composer would disappear from the tree until a manual refresh restored them.

The mechanism: during the save command's async `await`, the UI thread is free to process other dispatcher work, and WPF re-evaluates layout against the still-old data. When `ApplySortedFilter` finally rebuilds the tree afterward, the freshly-generated `TreeViewItem` containers end up in a partially-stale state where the expander `Path`'s `RelativeSource AncestorType=TreeViewItem` binding can't resolve cleanly, and the triangles fail to render.

**Solution**: Run the rebuild *before* the save's async await — `mutate → UpdatePieceCounts → ApplySortedFilter → suppress → await save` — matching the pattern already used by `OnNewComposer`. Every other handler that both rebuilds the tree and saves should follow the same order.

### GridView column alignment requires three coordinated XAML settings

**Problem**: `HorizontalAlignment="Right"` on a TextBlock inside a GridView cell does nothing — the data stays left-aligned. `HorizontalContentAlignment="Right"` on `GridViewColumnHeader` is also unreliable (the sort-arrow chrome reserves space on the right edge and the inner ContentPresenter often centres regardless). And styles defined inside `ListView.Resources` and referenced via `HeaderContainerStyle="{StaticResource ...}"` sometimes fail to resolve — `GridViewColumn` lives outside the visual tree.

**Solution** — to align cell content in a GridView, all three of these must be set together:

1. **`ListViewItem` → `HorizontalContentAlignment="Stretch"`**. Default is `Center`, which collapses the `GridViewRowPresenter` to its content width, so individual cells aren't column-width sized and `HorizontalAlignment` on inner TextBlocks has nothing to push against. This single setter is the unlock for the other two.
2. **Column-header styles live at `Window.Resources` / `UserControl.Resources` scope**, not in `ListView.Resources`. The `HeaderContainerStyle` StaticResource lookup is reliable from there.
3. **Right-aligned headers need `HeaderTemplate` + `TextAlignment="Right"`** on a `TextBlock` bound to `{Binding}`, combined with `HorizontalContentAlignment="Stretch"` on the header container style. The `{Binding}` pattern preserves sort-arrow append behaviour (handlers like `OnColumnHeaderClick` mutate `header.Content` directly).

Locked in by AlbumsView.xaml and AlbumEditorWindow.xaml. The whole stack must be present — partial fixes silently leave alignment broken.

### Capturing SynchronizationContext for non-UI-thread callbacks

**Problem**: NAudio's `WaveOutEvent.PlaybackStopped` fires on a pool thread. A view-model that reacts to such an event by updating `[ObservableProperty]` fields will raise `PropertyChanged` on the wrong thread, which WPF either complains about (cross-thread DependencyObject access) or silently mis-renders.

**Solution**: `NAudioPlayerService` captures `SynchronizationContext.Current` in its constructor. Since the DI graph builds singletons on the UI thread at startup, this is the WPF dispatcher's sync context. All public events are raised via `_sync.Post(...)` so consumers see them on the UI thread regardless of which thread NAudio chose. Tests that have no sync context get inline event raising (the check is `_sync is not null && _sync != SynchronizationContext.Current`). Locked in by `NAudioPlayerService.Raise(...)`.

### Slider scrub coordination: three-handler pattern

**Problem**: A `Slider` two-way bound to playback position fights itself when the user drags the thumb — playback updates keep pushing the value via binding while the user is also dragging, producing jitter and stale seek targets. The naïve "bind Value, seek in setter" loop also re-seeks for every micro-update during normal playback.

**Solution**: Three-handler pattern in the View + an `IsScrubbing` flag on the VM. `Thumb.DragStarted` → `PlayerViewModel.BeginScrub()` sets the flag; the VM's `OnPositionChanged` then skips its slider write so the user's drag isn't fought. `Thumb.DragCompleted` → `EndScrub(slider.Value)` clears the flag and seeks. For a *pure click* on the track (where `IsMoveToPointEnabled="True"` jumps the thumb but `DragStarted` never fires), `PreviewMouseLeftButtonUp` on the slider seeks too — guarded by `if (Vm.IsScrubbing) return;` so it doesn't fire alongside `DragCompleted` during real drags. Locked in by `PlayerBar.xaml.cs`.

### Avoid duplicating existing enum types when adding new services

**Problem**: While adding `IArchiveAudioLocator`, I declared a new `AudioFormat` enum (Flac, Mp3) in `CDArchive.Core.Services`, not realising an identically-named enum already lived in `CDArchive.Core.Models` (used by `ArchiveScannerService`). The Core project compiles in isolation (different namespaces), but the scanner's code referencing `Models.AudioFormat` then collides with the assembly's new `Services.AudioFormat` and fails with `CS0266: Cannot implicitly convert Services.AudioFormat to Models.AudioFormat`.

**Solution**: Before declaring an enum in `Services/` (or anywhere), grep for the type name across `Models/` first. In this case `CDArchive.Core.Models.AudioFormat` had exactly the right values and the locator just imports it.

### Window-level keyboard shortcuts that don't fight text inputs

**Problem**: A naked `Space` key shortcut for play/pause needs to fire everywhere in the app — but pressing space in a TextBox must still insert a space character. A `Window.InputBindings` `KeyBinding` for `Key.Space` would intercept the keystroke before any focused text input got it, breaking every textbox in the app.

**Solution**: Handle `PreviewKeyDown` at the Window level in code-behind, check `e.OriginalSource is TextBoxBase or PasswordBox or ComboBox`, and bail out (no `e.Handled = true`) if so. Otherwise execute the play/pause command and mark handled. Window-level routing means the shortcut works regardless of which view is active. Locked in by `MainWindow_PreviewKeyDown`.

### Worktrees + branch checkout exclusivity

**Problem**: A git branch can only be checked out in one worktree at a time. If a worktree's branch is named `feature/foo`, the main repo cannot also `git checkout feature/foo` — it fails with `fatal: 'feature/foo' is already used by worktree at ...`.

**Solution**: When working in a Claude-style worktree (which often lives under `.claude/worktrees/<id>/`), keep the local branch name worktree-scoped (e.g. `claude/<worktree-id>-foo`) and have it track the shared remote branch via `--set-upstream-to=origin/feature/foo`. The main repo can then claim the public branch name (`git checkout -b feature/foo origin/feature/foo`). Push from the worktree with the explicit refspec `git push origin HEAD:feature/foo` so the local and remote names can stay different.

### Find the actual UI click handler before "fixing" a VM RelayCommand

**Problem**: I wrote a cascade-aware `RejectComposerCommand` on `CanonViewModel`, added unit tests, all green — but the user reported the bug was still present. The XAML's context-menu MenuItem bound to `Click="OnContextReject"` in `CanonView.xaml.cs`, an event handler that did its own simpler reject logic. The RelayCommand was never invoked by the UI. Worse, the code-behind overwrote the VM's "failed to save" StatusMessage with a hardcoded success message after the swallowed save exception — the user saw a green-light status while nothing was deleted in the DB.

**Solution**: For any user-triggered bug, search the `Views/*.xaml` files for the user-visible label text first ("Reject", "Approve", "Delete") and trace from there. If the XAML uses `Click="OnFoo"` (code-behind event handler) rather than `Command="{Binding FooCommand}"` (RelayCommand), the VM command is dead from the UI's perspective — fix the code-behind, or have it delegate to a VM method. Don't trust the parallel naming. Status messages should be written by the code path that knows the actual outcome (the save site), not overwritten by a downstream handler that assumes success.

### SQLite column nullability changes: recreate the table, transactionally

**Problem**: `ALTER TABLE … ALTER COLUMN` isn't supported in SQLite. Making `album_tracks.disc_id` nullable (so loose tracks can have a null disc) required the recommended copy-rename dance, which isn't naturally "append-only" the way `EnsureColumnAsync` is. A halfway-failed migration could leave a half-renamed table and orphaned children.

**Solution**: `EnsureColumnNullableAsync(table, column, recreate)` checks `PRAGMA table_info.notnull` and no-ops when the column is already nullable; otherwise calls a per-table `recreate` delegate that runs the standard recipe: `PRAGMA foreign_keys=OFF` → BEGIN TX → `CREATE TABLE foo_new (…nullable…)` with explicit FKs, CHECKs, defaults → `INSERT INTO foo_new (…) SELECT (…) FROM foo` with explicit column lists (no `SELECT *` so a stale column order can't silently misalign) → `DROP TABLE foo` → `ALTER TABLE foo_new RENAME TO foo` → recreate every index with its EF-conventional name → `PRAGMA foreign_key_check` as a sanity gate inside the txn → COMMIT → `PRAGMA foreign_keys=ON`. The integrity check catches orphan FKs that the FK-off period would otherwise hide. Already applied for `album_tracks.disc_id` and `album_performers.album_id`; the same pattern fits any future nullability flip.

### Polymorphic-owner CHECK constraint (`album_id IS NOT NULL OR track_id IS NOT NULL`)

**Problem**: `album_performers` now anchors on either album-level (`track_id IS NULL`), track-level on an album-bound track (both set), or track-level on a loose track (`album_id IS NULL, track_id` set). With both columns nullable, a buggy caller could insert a credit with both NULL — a row that doesn't belong to anything.

**Solution**: The `ck_album_performers_has_owner` CHECK constraint (`album_id IS NOT NULL OR track_id IS NOT NULL`) rejects floating credits at the DB level. Same idea as the existing `ck_piece_composer_credits_exactly_one_owner` / `ck_piece_catalog_entries_exactly_one_owner` patterns — when one of several FKs may be set, the schema enforces "at least one" (or "exactly one") as a CHECK.

### `AddCoreServices` must call `AddLogging()`, not `TryAdd(NullLogger<T>)`

**Problem**: The first cut of `AddCoreServices` wired up `services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)))` so that tests / hosts that forgot to call `AddLogging` would still resolve `ILogger<T>`. The App project then called `services.AddLogging(b => b.AddSerilog(Log.Logger))` after `AddCoreServices()` — and Serilog never received any log events. Resolving `ILogger<SqliteCanonDataService>` still returned `NullLogger<T>`.

Reason: `AddLogging` internally uses `TryAdd` for both `ILoggerFactory` and `ILogger<>`. Once `AddCoreServices` had pre-registered `NullLogger<T>`, the host's later `AddLogging` call no-op'd those descriptors. The Serilog provider was added (as an `ILoggerProvider` via `TryAddEnumerable`, which does append), but it was attached to a `LoggerFactory` that nothing resolved through — `ILogger<T>` resolution short-circuited to the pre-registered `NullLogger<T>`. Caught by `LoggingPlumbingTests.AddCoreServices_HonoursHostLoggingProvider_WhenAddLoggingIsCalledAfter`.

**Solution**: `AddCoreServices` calls `services.AddLogging()` — the canonical pipeline. Hosts that want providers call `AddLogging(b => b.AddSerilog(...))` afterwards; `ILoggerProvider` is registered via `TryAddEnumerable`, which appends, so Serilog joins any other providers on the same factory. Tests that skip the second call still get a working `ILogger<T>` that emits nothing (no providers attached) — equivalent to `NullLogger` in behaviour but resolved through the real pipeline.

**Corollary**: When extending a DI surface that downstream consumers will layer onto via `AddXxx` helpers, prefer calling the canonical `AddXxx` yourself rather than reaching for `TryAdd(SomeNullImpl)`. The `TryAdd` semantics ("only if not registered") interact badly with framework `AddXxx` calls, which themselves use `TryAdd` and won't override your stub.

### Where the logs live

Application logs roll daily to `%LocalAppData%\CDArchive\logs\cdarchive-YYYYMMDD.log` (14 files retained, shared-write so a tail or second process can read concurrently). Serilog is configured in `App.OnStartup` *before* the ServiceProvider is built so startup failures land in the file. Unhandled `DispatcherUnhandledException` and `AppDomain.UnhandledException` both go through `Log.Fatal` + `CloseAndFlush` so a crash flushes its diagnostic before exit. The Debug sink mirrors output into the VS / Rider Output window during dev.

`SqliteCanonDataService` emits Information-level start + elapsed-ms entries for each `SaveXxxAsync`. When investigating a save bug, that log line tells you *which* subsystem was saved, *when*, and how long it took — useful when the user reports "saving felt slow" or "I edited an album but nothing changed".

---

## Data Management Guidelines

### Day-to-day editing

Use the WPF app. Edits go directly to SQLite; no JSON manipulation is needed or wanted.

### Before any operation that could affect the database

1. **Snapshot the DB.** Copy `data/ClassicalCanon.db` to a safe location. It's a single file; recovery is one `cp` away.
2. **Optionally export a JSON snapshot.** Run `dotnet run --project tools/CDArchive.Tools.SeedDb -- --export` to refresh the JSON files from the current DB state. Useful as a human-readable, diffable backup.

### Recovering from a corrupted or empty database

The seeder is the recovery tool. If `ClassicalCanon.db` is missing, corrupted, or contains stale data, run:

```
dotnet run --project tools/CDArchive.Tools.SeedDb
```

This deletes the existing DB, recreates the schema, and seeds from the four JSON files in `data/`. It reports composer / piece / album counts and any unresolved refs in its summary. The seed is deterministic — running it twice from the same JSON yields the same DB.

### Editing JSON files directly

Don't, except for explicit one-off imports or recovery. JSON files are not the source of truth at runtime. If you do edit them (e.g. to merge data from another source or fix a corrupted character), follow these rules:

1. **Always use a UTF-8-aware editor.** Search for telltale mojibake sequences afterward: `Ã©` (should be `é`), `Ã±` (should be `ñ`), `â™­` (should be `♭`), `Ã` followed by a space (should be `à`).
2. **Never use PowerShell `Set-Content` or `Out-File`** without explicit `-Encoding UTF8`. PowerShell's default cp1252 encoding silently corrupts non-ASCII characters.
3. **Reseed afterward**: `dotnet run --project tools/CDArchive.Tools.SeedDb` to push the edited JSON into SQLite.

### Fixing mojibake if it occurs in JSON files

Use the Python cp1252-to-UTF-8 round-trip algorithm. After fixing, reseed so the corrected data lives in SQLite.

```python
import json, re

with open("Classical Canon pieces.json", "r", encoding="utf-8") as f:
    text = f.read()

# Pass 1: Fix standard cp1252 mojibake
fixed = []
i = 0
while i < len(text):
    # Try 2, 3, 4-byte sequences
    for length in (4, 3, 2):
        if i + length <= len(text):
            seq = text[i:i+length]
            try:
                decoded = seq.encode('cp1252').decode('utf-8')
                if len(decoded) < len(seq):
                    fixed.append(decoded)
                    i += length
                    break
            except (UnicodeDecodeError, UnicodeEncodeError):
                pass
    else:
        fixed.append(text[i])
        i += 1

text = ''.join(fixed)

# Pass 2: Fix a-grave (Ã + JSON-escaped NBSP)
text = text.replace('Ã\\u00A0', 'a')  # JSON-escaped form
text = text.replace('Ã\u00A0', 'a')   # Literal NBSP form

with open("Classical Canon pieces.json", "w", encoding="utf-8") as f:
    f.write(text)
```

### Import semantics

- **Merge-only**: Import never overwrites existing data. Composers are matched by `Name` (case-insensitive). Pieces are matched by `Composer` + `Title` composite key (case-insensitive).
- **Format**: Both `{}` (single object) and `[]` (array) are accepted.
- **After import**: Navigate to Composers to see the updated list. The Refresh button reloads from DB.

---

## JSON Serialization Conventions

JSON serialization is used for the seeder import format, the seeder `--export` output, and the Import/Export screen's user-driven file operations. The runtime data store (SQLite) does not use JSON for primary persistence — these conventions apply to the JSON snapshots and to the JSON-blob columns used for flexible substructures inside SQLite rows (`InstrumentationJson`, `RolesJson`, etc.).

| Convention | Details |
|---|---|
| Property naming | `snake_case` in JSON, `PascalCase` in C# (via `[JsonPropertyName]`) |
| Null handling | `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on optional properties — omitted from JSON when null |
| Default-value handling | Fields like `NumberedSubpieces` and `SubpiecesStart` are set to `null` when they match the default, keeping JSON clean |
| Flexible types | `JsonElement?` for fields that can be strings, arrays, or objects (Instrumentation, Roles, TextAuthor, CompositionYears) |
| Read options | `PropertyNameCaseInsensitive = true`, `AllowTrailingCommas = true` |
| Write options | `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`, `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` |

---

## Service Registration Summary

Registered in `ServiceCollectionExtensions.AddCoreServices()`:

| Service | Lifetime | Implementation / Notes |
|---|---|---|
| `IArchiveSettings` | Singleton | `ArchiveSettings` |
| `IFileSystemService` | Transient | `FileSystemService` |
| `IAlbumScaffoldingService` | Transient | `AlbumScaffoldingService` |
| `IDuplicateDetectionService` | Transient | `DuplicateDetectionService` |
| `IArchiveScannerService` | Transient | `ArchiveScannerService` |
| `IConversionService` | Transient | `FfmpegConversionService` |
| `IConversionStatusService` | Transient | `ConversionStatusService` |
| `LocalCatalogueReference` | Singleton | |
| `ItunesLibraryReference` | Singleton | |
| `MusicBrainzReference` | Singleton | |
| `CompositeCatalogueReference` | Singleton | |
| `ICataloguingService` | Transient | `CataloguingService` |
| `CanonDataService` | Singleton | Concrete-typed registration. Used by `SqliteCanonDataService` for the file-path properties and by `ImportExportViewModel` for default file dialog locations. **Not** registered as `ICanonDataService` — it is not the runtime data service. |
| `IDbContextFactory<CanonDbContext>` | Singleton | Connection string derives from `CanonDataService.ComposersFilePath`'s directory. Each Load/Save call opens a fresh short-lived `CanonDbContext` from the factory. |
| `ICanonDataService` | Singleton | `SqliteCanonDataService` — the runtime data service. Reads/writes only SQLite. |
| `PieceReferenceIndex` | Singleton | Cross-references albums to pieces; rebuilt on load and album-edit. |
| `IArchiveAudioLocator` | Singleton | `ArchiveAudioLocator` — resolves an `AlbumTrack` to an audio file via override + convention. |
| `IAudioPlayerService` | Singleton | `NAudioPlayerService` — NAudio + `MediaFoundationReader` + `WaveOutEvent`. Captures `SynchronizationContext` so events come back on the UI thread. |

Registered in `App.xaml.cs`:

| ViewModel | Lifetime |
|---|---|
| `MainViewModel` | Singleton |
| `CanonViewModel` | Singleton |
| `AlbumsViewModel` | Singleton |
| `PlayerViewModel` | Singleton (survives navigation so playback state persists) |
| `PickListsViewModel` | Singleton |
| `ImportExportViewModel`, all other ViewModels | Transient |

---

## File Locations

### Data files

| Path | Purpose |
|---|---|
| `data/ClassicalCanon.db` | **SQLite database — single source of truth at runtime.** Created by the seeder. Safe to delete and rebuild. |
| `data/Classical Canon composers.json` | One-shot import/export snapshot. Read by the seeder; written by `--export`. Not auto-synced. |
| `data/Classical Canon pieces.json` | Same. |
| `data/Classical Canon pick lists.json` | Same. |
| `data/Classical Canon albums.json` | Same. |

### Domain models (`src/CDArchive.Core/Models/`)

| File | Purpose |
|---|---|
| `CanonPiece.cs` | `CanonPiece`, `CanonPieceVersion`, `CatalogInfo`, `TempoInfo`, `RoleEntry`, `InstrumentEntry`, `ComposerCredit`, `VariantInfo` |
| `CanonComposer.cs` | `CanonComposer` |
| `CanonPickLists.cs` | `CanonPickLists` |
| `CanonAlbum.cs` | `CanonAlbum`, `AlbumDisc`, `AlbumTrack`, `AlbumPerformer`, `AlbumVolume`, `RecordingSession`, `TrackPieceRef` |

### Persistence layer (`src/CDArchive.Core/Data/`)

| File | Purpose |
|---|---|
| `CanonDbContext.cs` | EF Core context. Defines schema, indexes, CHECK constraints. |
| `CanonDbSeeder.cs` | One-shot JSON → SQLite migration. Used by the seeder tool and (historically) by auto-init. |
| `*Row.cs` | Row entities mirroring the relational schema (`PieceRow`, `AlbumRow`, `PieceCatalogEntryRow`, etc.) |

### Services (`src/CDArchive.Core/Services/`)

| File | Purpose |
|---|---|
| `ICanonDataService.cs` | Runtime data service contract |
| `SqliteCanonDataService.cs` | Runtime implementation. Reads/writes SQLite only. |
| `CanonDataService.cs` | JSON read/write utility. Used by the seeder tool and Import/Export VM, not the runtime data path. |
| `PieceReferenceIndex.cs` | Album ↔ piece cross-reference index. Accepts loose tracks in `Rebuild` / `RebuildContainers`. |
| `ItunesLibraryReference.cs` | iTunes XML library parser |
| `IArchiveAudioLocator.cs` / `ArchiveAudioLocator.cs` | Music-player file resolution: per-track override → convention → null. Also defines `AudioFileLocation`. |
| `IAudioPlayerService.cs` / `NAudioPlayerService.cs` | Music-player playback engine (NAudio). Defines `PlayerState`. |
| `PreferredAudioFormat.cs` | `Flac | Mp3` enum used by `IArchiveSettings` and the locator. |
| `CanonRejectCascade.cs` | Static helpers for cascading reject of composers and pieces (strips refs from albums and loose tracks; deletes pieces and the composer in FK-dependency order). |
| `TrackCascade.cs` | Static helpers for approve / reject of individual tracks. Loose-aware. |

### Views & ViewModels (`src/CDArchive.App/`)

| File | Purpose |
|---|---|
| `Views/CanonView.xaml[.cs]` | Main composer/piece tree view (permanent element in `MainWindow`) |
| `Views/PieceEditorWindow.xaml[.cs]` | Piece editor dialog |
| `Views/MovementEditorWindow.xaml[.cs]` | Movement/subpiece editor dialog |
| `Views/VersionEditorWindow.xaml[.cs]` | Version editor dialog |
| `Views/ComposerEditorWindow.xaml[.cs]` | Composer editor dialog |
| `Views/ImportExportView.xaml[.cs]` | Import/Export screen |
| `Views/PlayerBar.xaml[.cs]` | Persistent transport bar docked at bottom of `MainWindow`. Hosts scrub coordination for the progress slider. |
| `Views/TracksView.xaml[.cs]` | Cross-album Tracks list. Sortable columns, scroll-to-selection anchor, Approve/Reject context menu, New Track button, double-click dispatch (album-bound → album editor; loose → loose-mode track editor). |
| `Views/TrackEditorWindow.xaml[.cs]` | Track editor dialog. Three constructors: single album-bound, bulk-edit, loose-mode. |
| `ViewModels/MainViewModel.cs` | Shell navigation, `CanonView` visibility, exposes `PlayerViewModel` for binding |
| `ViewModels/CanonViewModel.cs` | Composer/piece loading + saving. `Reject*WithCascadeAsync` wrap `CanonRejectCascade`. |
| `ViewModels/AlbumsViewModel.cs` | Album loading + saving |
| `ViewModels/TracksViewModel.cs` | Cross-album Tracks VM. Owns loose tracks alongside album-bound; flattens both into rows; exposes `ApproveRowsAsync` / `RejectRowsAsync`. |
| `ViewModels/AlbumTrackRow.cs` | Row projection used by the Tracks list. Nullable `Album` / `Disc` for loose tracks; `DiscDisplay` / `TrackDisplay` collapse for loose and single-disc cases. |
| `ViewModels/ImportExportViewModel.cs` | Import/export/restore commands |
| `ViewModels/PlayerViewModel.cs` | Music-player VM. Holds playback context (album + flat sequence + index) and drives auto-advance. |
| `MainWindow.xaml.cs` | Window-level `PreviewKeyDown` for Space = play/pause shortcut. |

### Tooling (`tools/`)

| Path | Purpose |
|---|---|
| `CDArchive.Tools.SeedDb/Program.cs` | CLI entry point. Default mode seeds JSON → SQLite; `--export` writes SQLite → JSON; `--restore-albums` rewrites just the albums table from JSON; `--promote-loose-tracks` [`--apply`] migrates synthetic single-track wrapper albums to loose tracks (heuristic-driven, dry-run by default). |

### Tests (`tests/CDArchive.Core.Tests/`)

| File | Notable contents |
|---|---|
| `SqliteRoundTripTests.cs` | `BeethovenOp2_HasAlbumHits_AfterSqliteRoundTrip` (set + sonata + movement coverage), `SaveOperations_DoNotTouchJsonFiles` (architectural invariant) |
| `CanonDataServiceTests.cs` | JSON loader sanity checks, dual-marker resolver regression test |
| `ArchiveAudioLocatorTests.cs` | 11 filesystem-backed tests covering the locator's override → convention → title-fallback resolution. |
| `NAudioPlayerServiceTests.cs` | 8 smoke tests against synthesised WAV files: initial state, error paths, Duration reporting, Seek clamping, file replacement. Actual audio output not exercised (no device guarantee in CI). |
| `AlbumTracksNullableDiscIdMigrationTests.cs` | 3 tests: fresh-DB schema nullability, legacy-NOT-NULL → nullable migration with row preservation, idempotency. |
| `LooseTrackRoundTripTests.cs` | 4 tests: performer-table nullable-album_id migration with CHECK, loose-track scalar+ref+performer round-trip, orphan-delete, album/loose coexistence. |
| `PieceReferenceIndexLooseTracksTests.cs` | 5 tests covering loose-track indexing and the distinct-container badge count. |
| `CanonRejectCascadeTests.cs` | Composer/piece reject cascade including loose-track ref stripping. |
| `TrackCascadeTests.cs` | Approve / Reject pure logic + integration round-trip through SQLite. |
| `SingletonAlbumPromotionTests.cs` | 13 tests for the wrapper-album → loose-track migration: heuristic per disqualifier, dry-run vs apply, performer re-anchoring, inherited SparsCode/IsStereo, mixed batch. |
| `ItunesImporterTests.cs` | Album path + loose-track path: albumless rows become loose tracks (TrackNumber=0), Artist field → track performers, composite composer parsing (`compl.` / `arr.`). |
