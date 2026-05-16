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

- **`feature/follow-ups`** branch pending PR — bundles four small deferred items: stop-after-current toggle, volume control with persisted settings, build-warning cleanup, and the `PropagateAlbumFieldsToTracks` extraction into `CDArchive.Core.Helpers.AlbumFieldPropagator` (now unit-tested). The currently-playing-track highlight was tried and rolled back (the user didn't find it helpful) — see the revert commit on the same branch.
- **`feature/tracklist`** branch exists with no commits — created at the start of the bugfix/import session, work not yet started.
- **Multi-composer pieces (`L'éventail de Jeanne` etc.)** remains the one open canon-data deferral — see the *Multi-composer pieces have no primary composer field* lesson. Needs a design call on whether "Various" is a sentinel composer or a real first-class entity before implementation.

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
| `PieceReferenceIndex.cs` | Album ↔ piece cross-reference index. |
| `ItunesLibraryReference.cs` | iTunes XML library parser |
| `IArchiveAudioLocator.cs` / `ArchiveAudioLocator.cs` | Music-player file resolution: per-track override → convention → null. Also defines `AudioFileLocation`. |
| `IAudioPlayerService.cs` / `NAudioPlayerService.cs` | Music-player playback engine (NAudio). Defines `PlayerState`. |
| `PreferredAudioFormat.cs` | `Flac | Mp3` enum used by `IArchiveSettings` and the locator. |

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
| `ViewModels/MainViewModel.cs` | Shell navigation, `CanonView` visibility, exposes `PlayerViewModel` for binding |
| `ViewModels/CanonViewModel.cs` | Composer/piece loading + saving |
| `ViewModels/AlbumsViewModel.cs` | Album loading + saving |
| `ViewModels/ImportExportViewModel.cs` | Import/export/restore commands |
| `ViewModels/PlayerViewModel.cs` | Music-player VM. Holds playback context (album + flat sequence + index) and drives auto-advance. |
| `MainWindow.xaml.cs` | Window-level `PreviewKeyDown` for Space = play/pause shortcut. |

### Tooling (`tools/`)

| Path | Purpose |
|---|---|
| `CDArchive.Tools.SeedDb/Program.cs` | CLI entry point. Default mode seeds JSON → SQLite; `--export` writes SQLite → JSON; `--restore-albums` rewrites just the albums table from JSON. |

### Tests (`tests/CDArchive.Core.Tests/`)

| File | Notable contents |
|---|---|
| `SqliteRoundTripTests.cs` | `BeethovenOp2_HasAlbumHits_AfterSqliteRoundTrip` (set + sonata + movement coverage), `SaveOperations_DoNotTouchJsonFiles` (architectural invariant) |
| `CanonDataServiceTests.cs` | JSON loader sanity checks, dual-marker resolver regression test |
| `ArchiveAudioLocatorTests.cs` | 11 filesystem-backed tests covering the locator's override → convention → title-fallback resolution. |
| `NAudioPlayerServiceTests.cs` | 8 smoke tests against synthesised WAV files: initial state, error paths, Duration reporting, Seek clamping, file replacement. Actual audio output not exercised (no device guarantee in CI). |
