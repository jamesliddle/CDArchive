# CDArchive Project Guide

## Overview

CDArchive is a WPF desktop application (.NET 8, C#) for managing a classical music CD library. The owner has 3,000+ physical CDs, rips them to FLAC using Exact Audio Copy, converts to MP3, and imports into iTunes with strict custom metadata formatting. This application centralizes that workflow and maintains a canonical reference database of classical composers and their works.

The application has two major subsystems:

1. **Archive Management** -- CD ripping workflow, folder scaffolding, duplicate detection, FLAC-to-MP3 conversion, archive validation, and iTunes catalogue integration.
2. **Canon** -- A curated reference database of classical music composers, their works, movements, versions, and related metadata. This is the primary subsystem under active development.

---

## Technology Stack

| Layer | Technology |
|---|---|
| UI Framework | WPF (Windows Presentation Foundation) |
| Architecture | MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `AsyncRelayCommand`) |
| Data Access | EF Core 8 with SQLite; System.Text.Json for serialization |
| Target Framework | .NET 8.0 (Windows) |
| Project Format | Modern SDK-style .csproj |

---

## Solution Structure

```
CDArchive/
  data/
    Classical Canon composers.json     # Canonical composer reference (JSON array)
    Classical Canon pieces.json        # Canonical works reference (JSON array)
    Classical Canon pick lists.json    # Dropdown/pick-list values (forms, keys, etc.)
    ClassicalCanon.db                  # SQLite database (auto-created, seeded from JSON)
  src/
    CDArchive.App/                     # WPF application
      ViewModels/                      # MVVM view models
      Views/                           # XAML views and code-behind
      Converters/                      # WPF value converters
      Helpers/                         # Utility classes
      App.xaml / App.xaml.cs           # Startup, DI container, DataTemplates
      MainWindow.xaml                  # Shell: nav bar + content area
    CDArchive.Core/                    # Business logic library
      Models/                          # Data models (CanonPiece, CanonComposer, etc.)
      Services/                        # Data access, conversion, archive scanning
      Data/                            # EF Core DbContext and row entities
      Helpers/
      ServiceCollectionExtensions.cs   # DI registration
  tests/
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

---

## Data Persistence Architecture

### Dual storage: SQLite + JSON write-through

The application uses `SqliteCanonDataService` as its primary data service. It stores data in a SQLite database (`ClassicalCanon.db`) with EF Core, but **every save operation also writes the data back to the canonical JSON files**. This ensures the JSON files always reflect the current state of the database and serve as a human-readable backup.

```
User edits piece in UI
  -> CanonViewModel.SavePiecesCommand
    -> SqliteCanonDataService.SavePiecesAsync()
      -> EF Core upsert to SQLite
      -> CanonDataService.SavePiecesAsync()  (JSON write-through)
```

### Database schema

**Pieces table** -- Scalar columns for indexed/searchable fields, JSON blob columns for complex nested data:
- Indexed: `Composer`, `InstrumentationCategory`, composite `(Composer, CatalogSortPrefix, CatalogSortNumber, CatalogSortSuffix)`
- JSON blobs: `CatalogInfoJson`, `InstrumentationJson`, `SubpiecesJson`, `VersionsJson`, `RolesJson`, `TemposJson`, `TextAuthorJson`, etc.
- Sort helpers: `CatalogSortPrefix`, `CatalogSortNumber` (int), `CatalogSortSuffix` -- derived from `CatalogInfo` at save time for efficient `ORDER BY`

**Composers table** -- Flat columns mirroring `CanonComposer` properties. Indexed on `SortName`.

**Settings table** -- Key/value store. Currently holds `pick_lists` (JSON blob of `CanonPickLists`).

### Database lifecycle

1. **First run**: `EnsureInitialisedAsync()` creates the database and seeds from JSON files if the Pieces table is empty.
2. **Normal operation**: All reads come from SQLite; all writes go to SQLite + JSON.
3. **Recovery**: If the database is corrupted or needs to be rebuilt, the user can delete it via Import/Export and it will be re-seeded from JSON on next launch.

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

The Import/Export screen (`ImportExportViewModel`) provides:

| Operation | Behavior |
|---|---|
| **Export Composers** | Save composers to a user-chosen JSON file (defaults to canonical path/name) |
| **Export Pieces** | Save pieces to a user-chosen JSON file (defaults to canonical path/name) |
| **Sync to Canonical JSON** | Bulk write-through: loads all data from DB, writes to all three canonical JSON files |
| **Import Composers** | Merge-only: adds composers not already in DB (matched by `Name`, case-insensitive). No overwrite. |
| **Import Pieces** | Merge-only: adds pieces not already in DB (matched by `Composer` + `Title` composite key, case-insensitive). No overwrite. |
| **Delete Database** | Deletes `ClassicalCanon.db` with confirmation. App re-seeds from JSON on next launch. |
| **Reseed from JSON** | Lets user pick replacement JSON files, deletes DB, recreates and seeds from the selected files. |

**Import format**: Accepts both single-object `{}` and array `[]` JSON. Uses `JsonDocument.Parse` to detect the root element kind before deserialization.

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

---

## Data Management Guidelines

### Before any mass data update

1. **Sync to JSON first**: Use Import/Export > Sync to Canonical JSON to ensure the JSON files reflect the current DB state.
2. **Back up the JSON files**: Copy `Classical Canon composers.json` and `Classical Canon pieces.json` to a safe location.
3. **Never use PowerShell** for text replacement on JSON files. Use Python or the application's own export functions.
4. **Verify encoding**: After any external edit to JSON files, open them in a UTF-8-aware editor and spot-check characters like `e`, `n`, `flat`, `a`.

### After a mass data update

1. **Spot-check for mojibake**: Search the JSON files for telltale sequences: `Ã©` (should be `e`), `Ã±` (should be `n`), `â™­` (should be `flat`), `Ã` followed by a space (should be `a`).
2. **Delete and reseed the DB** if the update was done directly on JSON files: Import/Export > Reseed from JSON.
3. **Verify counts**: After reseeding, check the status bar for expected composer and piece counts.

### Fixing mojibake if it occurs

Use the Python cp1252-to-UTF-8 round-trip algorithm:

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

| Convention | Details |
|---|---|
| Property naming | `snake_case` in JSON, `PascalCase` in C# (via `[JsonPropertyName]`) |
| Null handling | `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on optional properties -- omitted from JSON when null |
| Default-value handling | Fields like `NumberedSubpieces` and `SubpiecesStart` are set to `null` when they match the default, keeping JSON clean |
| Flexible types | `JsonElement?` for fields that can be strings, arrays, or objects (Instrumentation, Roles, TextAuthor, CompositionYears) |
| Read options | `PropertyNameCaseInsensitive = true`, `AllowTrailingCommas = true` |
| Write options | `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`, `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` |

---

## Service Registration Summary

Registered in `ServiceCollectionExtensions.AddCoreServices()`:

| Service | Lifetime | Implementation |
|---|---|---|
| ICanonDataService | Singleton | SqliteCanonDataService |
| IArchiveSettings | Singleton | ArchiveSettings |
| LocalCatalogueReference | Singleton | -- |
| ItunesLibraryReference | Singleton | -- |
| MusicBrainzReference | Singleton | -- |
| CompositeCatalogueReference | Singleton | -- |
| IFileSystemService | Transient | FileSystemService |
| IAlbumScaffoldingService | Transient | AlbumScaffoldingService |
| IDuplicateDetectionService | Transient | DuplicateDetectionService |
| IArchiveScannerService | Transient | ArchiveScannerService |
| IConversionService | Transient | FfmpegConversionService |
| IConversionStatusService | Transient | ConversionStatusService |
| ICataloguingService | Transient | CataloguingService |

Registered in `App.xaml.cs`:

| ViewModel | Lifetime |
|---|---|
| MainViewModel | Singleton |
| CanonViewModel | Transient |
| ImportExportViewModel | Transient |
| All other ViewModels | Transient |

---

## File Locations

| File | Purpose |
|---|---|
| `data/Classical Canon composers.json` | Canonical composer data (JSON, always kept in sync with DB) |
| `data/Classical Canon pieces.json` | Canonical works data (JSON, always kept in sync with DB) |
| `data/Classical Canon pick lists.json` | Pick-list values for editor dropdowns |
| `data/ClassicalCanon.db` | SQLite database (auto-created, can be safely deleted and rebuilt) |
| `src/CDArchive.Core/Models/CanonPiece.cs` | Piece, Version, CatalogInfo, TempoInfo, RoleEntry, InstrumentEntry models |
| `src/CDArchive.Core/Models/CanonComposer.cs` | Composer model |
| `src/CDArchive.Core/Models/CanonPickLists.cs` | Pick-list model |
| `src/CDArchive.Core/Services/SqliteCanonDataService.cs` | Primary data service (SQLite + JSON write-through) |
| `src/CDArchive.Core/Services/CanonDataService.cs` | JSON-only data service (used for seeding and write-through) |
| `src/CDArchive.Core/Services/ItunesLibraryReference.cs` | iTunes XML library parser |
| `src/CDArchive.App/Views/CanonView.xaml[.cs]` | Main composer/piece tree view |
| `src/CDArchive.App/Views/PieceEditorWindow.xaml[.cs]` | Piece editor dialog |
| `src/CDArchive.App/Views/MovementEditorWindow.xaml[.cs]` | Movement/subpiece editor dialog |
| `src/CDArchive.App/Views/VersionEditorWindow.xaml[.cs]` | Version editor dialog |
| `src/CDArchive.App/Views/ComposerEditorWindow.xaml[.cs]` | Composer editor dialog |
| `src/CDArchive.App/Views/ImportExportView.xaml[.cs]` | Import/Export screen |
| `src/CDArchive.App/ViewModels/MainViewModel.cs` | Shell navigation, CanonView visibility |
| `src/CDArchive.App/ViewModels/CanonViewModel.cs` | Data loading/saving commands |
| `src/CDArchive.App/ViewModels/ImportExportViewModel.cs` | Import/export/reseed commands |
