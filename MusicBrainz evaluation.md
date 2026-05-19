# MusicBrainz evaluation

Working notes on how MusicBrainz is used in this app today and how it could be used to supplement album information that iTunes doesn't store. Captured for revisiting / iterating.

---

## Part 1: How MusicBrainz is used today

Short version: **lightly, indirectly, and incompletely**.

### The chain

`MusicBrainzReference` is the bottom of a three-source priority chain orchestrated by `CompositeCatalogueReference`:

```
CompositeCatalogueReference (the chain orchestrator)
  ├─ LocalCatalogueReference   (priority 1 — always returns null, permanent stub per L38)
  ├─ ItunesLibraryReference    (priority 2 — parses iTunes XML)
  └─ MusicBrainzReference      (priority 3 — HTTPS calls to musicbrainz.org)
```

`CompositeCatalogueReference.LookupComposerAsync` walks the chain and returns the first non-null result. Since Local is a permanent stub, in practice it's iTunes-then-MusicBrainz.

### What MusicBrainzReference does

Two methods, both required by `ICatalogueReference`:

| Method | What it hits | What's returned |
|---|---|---|
| `LookupComposerAsync(lastName, firstName?)` | `https://musicbrainz.org/ws/2/artist?query=...&fmt=json` | `ComposerInfo` (last, first, birth year, death year) |
| `LookupWorkAsync(composerLastName, workSearchTerm)` | `https://musicbrainz.org/ws/2/work?...` + `/ws/2/work/{id}?inc=work-rels` | `WorkInfo` (title + movement list via "parts" relations) |

Both are rate-limited via a `_lastRequest` field (1100ms between requests). The composer query URL-encodes the search string; the work query does a two-step lookup (search → details with `work-rels` for movements).

### What actually calls it

There's exactly **one** caller in the entire codebase. From `CataloguingService.FormatEntriesAsync`:

```csharp
composerCache[composerLast] = await _reference.LookupComposerAsync(
    composerLast,
    string.IsNullOrEmpty(composerFirst) ? null : composerFirst);
```

So MusicBrainz is consulted exactly when:

1. The user opens the **Catalogue** screen (`CatalogueView`).
2. They pick an album from the scanned archive.
3. They click **Read Tags**.
4. `CataloguingService.ReadAlbumTagsAsync` reads each MP3 filename, parses out the composer (via `TagParser.ParseFilename`), then calls `LookupComposerAsync(composerLast, composerFirst)`.
5. The composite chain tries Local (null), then iTunes (if the composer is already in the user's iTunes library), then MusicBrainz (only if iTunes had no match).
6. If MusicBrainz wins, the user sees `Reference: MusicBrainz` in the status bar (`CatalogueViewModel.ReferenceSource`).

The returned `ComposerInfo.Formatted` (something like `"Brahms, Johannes (1833–1897)"`) is then written into `entry.Composer` for every track in the album.

If the user clicks **Write Tags**, `CataloguingService.WriteTagsAsync` writes that formatted string into the MP3/FLAC file's composer ID3 tag via TagLib.

### What's NOT used

`LookupWorkAsync` — defined on all 4 implementations of `ICatalogueReference`, including the full two-step MusicBrainz HTTP dance with movement enumeration — **is never called from anywhere in the codebase**. Grep confirms zero callers. Half of `MusicBrainzReference` is dead code at the consumer level.

This means MusicBrainz today contributes only **composer-level identity** (name spelling + birth/death years) to the cataloguing workflow. Work titles, movement lists, structured catalogue numbers — all the things MusicBrainz actually knows about — are unused.

### Where MusicBrainz sits in the user's workflow

```
Rip CD with EAC → FLAC files
       ↓
ffmpeg → MP3 files (CDArchive.ConversionService)
       ↓
User opens Catalogue screen, picks album
       ↓
Click "Read Tags"
       ↓
TagParser parses MP3 filenames → (composer, work, movement)
       ↓
For each unique composer last-name:
   LookupComposerAsync (the only MusicBrainz touchpoint)
       ↓
CataloguingService writes back via WriteTagsAsync → MP3/FLAC tags
       ↓
User then imports the MP3s into iTunes (manual step outside the app)
```

The cataloguing screen exists specifically to standardize the composer name to the user's preferred format ("Brahms, Johannes (1833–1897)") before the MP3s land in iTunes. iTunes is the primary source for previously-imported composers (so re-importing the same composer is fast); MusicBrainz is the fallback for composers the user hasn't yet seen.

### Known issues with the current MusicBrainz integration

From `Rework.md`:

- **C9** — `RateLimitedGetAsync` has a bare `catch { return null; }`. Network errors, 5xx, JSON parse failures, and legitimate 404s are indistinguishable. User sees "no composer found" with no way to tell why.
- **C10** — `_lastRequest` is mutable shared state on a Singleton with no locking. Two concurrent callers race and can both fire under 1s, violating MusicBrainz's rate-limit policy → 503 ban risk.
- **M16** — `HttpClient` constructed per-instance, not via `IHttpClientFactory`. Modern .NET pattern absent.
- **M17** — User-Agent hardcoded as `"CDArchive/1.0 (https://github.com/cdarchive)"`. MusicBrainz documents this should carry a real version + real contact URL; the URL in the user-agent doesn't exist.
- **M18** — No retry on 503 (rate-limit response). One transient failure drops a result.
- **M28** — Only the first result is returned. `count` field ignored; a generic name like "Strauss" arbitrarily picks one of several real candidates.
- **M81** — `ICatalogueReference` has no `CancellationToken`. Closing the dialog mid-lookup doesn't abort.
- **M79** — `CompositeCatalogueReference._lastSourceUsed` is racy under concurrent calls.

### Summary of current usage

MusicBrainz in this app is a **fallback composer-name normalizer** for the cataloguing screen, gated behind iTunes. It's used **once per unique composer per album** during the Read Tags pass. Half of the integration (`LookupWorkAsync` for movement enumeration) is built but unused.

It's *infrastructure waiting for a feature* in roughly the same way `LocalCatalogueReference` is: the chain was designed to support richer reference lookups (work titles, movements, catalogue numbers) than the app actually consumes today.

---

## Part 2: How MusicBrainz could supplement album info iTunes doesn't store

iTunes is a *consumer* metadata source (what you bought, what's on your device); MusicBrainz is a *bibliographic* source (the actual release, its identifiers, its place in the discography). The gap is large, and the existing schema is mostly already wired to absorb it.

### What MusicBrainz knows that iTunes doesn't

For a "release" (the MB term for a specific pressing of an album):

| Field | iTunes | MusicBrainz |
|---|---|---|
| Title / Subtitle | ✓ | ✓ |
| Artist / Composer | ✓ (free text) | ✓ (as linked `artist` entity with MBID) |
| Label | ✗ | ✓ (linked `label` entity with MBID) |
| Catalogue number | ✗ | ✓ (e.g. `DG 419 052-2`) |
| Barcode (EAN/UPC) | ✗ | ✓ |
| Release date | partial (Year) | full (yyyy-mm-dd, original vs reissue) |
| Country of release | ✗ | ✓ |
| SPARS code | ✗ | ✓ (where data exists) |
| Recording dates / venue / engineers / producers | ✗ | partial (release annotations, recording entity relations) |
| Cover art | ✓ (if you bought from iTunes) | ✓ (Cover Art Archive, separate API) |
| Stable identifier | ❌ (only PersistentID per-track) | ✓ **MBID — a UUID per release / recording / work / artist / label** |
| Linked work entity (catalogue number, composition year, movement list) | ✗ | ✓ |
| Multiple recordings of the same work | implicit | ✓ (separate `recording` MBIDs per performance, same `work` MBID) |

The schema already has columns for almost everything in the left half: `albums.Label`, `albums.CatalogueNumber`, `albums.Barcode`, `albums.SparsCode`, `RecordingSession.Dates/Venue/City/Country/Engineers/Producers`, etc. They're just predominantly empty for albums the user hasn't curated manually.

### The four most useful supplements

#### 1. Per-album "Fetch from MusicBrainz" by barcode or catalogue number

Concrete UX: in `AlbumEditorWindow`, next to the Barcode field, add a `[Fetch ↻]` button. User types `028941905222`, clicks fetch:

```
MusicBrainz /ws/2/release?query=barcode:028941905222&fmt=json
→ "Karajan – Beethoven: Symphony No. 9, Deutsche Grammophon, 419 052-2, 1983"
```

The dialog pre-fills Label, CatalogueNumber, Title, Subtitle. The user reviews and saves. The MBID is stored in a new `albums.musicbrainz_release_id` column (nullable, additive migration — zero risk).

This is the smallest possible MusicBrainz integration that adds real value: one API endpoint, one button, one new column. Implementation maps cleanly onto the existing `ICatalogueReference` chain — just needs a new `LookupReleaseAsync(string barcode)` method or its catalogue-number sibling.

#### 2. Disc identification via TOC (the "I just ripped this CD, what is it?" case)

EAC can compute a **DiscID** — a stable hash of the disc's table of contents. MusicBrainz indexes releases by DiscID:

```
GET https://musicbrainz.org/ws/2/discid/{fingerprint}?inc=labels+release-groups+artist-credits+recordings
→ returns the release(s) matching that exact TOC
```

Workflow: when EAC rips a CD, configure it to write a `.discid` sidecar file alongside the FLAC tracks. `ArchiveScannerService` reads the sidecar during a scan; if the album has no Label set, automatically resolve the DiscID. The user gets a "Found 3 albums on MusicBrainz that match the disc you ripped, pick one" prompt in `ValidationView` or a new "Identify" screen.

This is the most powerful workflow for the user's specific habit (rip → import → enrich) because it leverages an identifier that's already a property of the physical CD, not something the user has to type.

#### 3. Work + movement structure for new pieces

`MusicBrainzReference.LookupWorkAsync` already exists in the code — and is currently dead. The MB endpoint returns the canonical work entity:

```
GET https://musicbrainz.org/ws/2/work/{mbid}?inc=work-rels+artist-rels
→ {
    "title": "Symphony No. 9 in D minor, Op. 125 'Choral'",
    "iswcs": [...],
    "relations": [
      { "type": "parts", "work": { "title": "I. Allegro ma non troppo, un poco maestoso" } },
      { "type": "parts", "work": { "title": "II. Molto vivace" } },
      ...
    ]
  }
```

UX: in `PieceEditorWindow`, when creating a piece, after the user picks the composer and types a title, a `[Look up]` button hits `/work?query=composer:Beethoven AND work:"Symphony No. 9"`. The returned work pre-fills `CatalogInfo` (Op. 125), `PublicationYear`, and **the entire `Subpieces` list** (the four movements with their tempo markings).

This is the integration that would eliminate the most manual data entry. The user currently types every movement by hand. MusicBrainz knows them. The iTunes-inference bugs (M66 mangled compound surnames, M67 `St. Peter` over-splits, M68 joint composers) all fall away once a canonical work entity is the source of truth instead of a parsed track filename.

#### 4. Stable identifiers (MBIDs) as a replacement for fuzzy matching

The architectural win that would compound across many `Rework.md` findings. The codebase currently fights with fuzzy identity in many places:

- **H26** — composer cache by surname-only (Bach/Strauss collision)
- **H41** — `PieceReferenceIndex.RegisterPiece` silently drops duplicate-title pieces
- **M39 / M40** — Levenshtein dedup with diacritic-mangling
- **`CanonAlbum.IdentityKey`** — composite over (Label, CatalogueNumber, Title, Subtitle), fragile when Title is renamed (per CLAUDE.md's existing lesson)
- **H24** — iTunes dedup ignores Label/CatalogueNumber → Karajan B9 vs Bernstein B9 collision
- **M77** — null-composer pieces re-imported on every run

Every one of these would become exact-equality lookups if the relevant entities had MBIDs populated:

```sql
-- Pure additive schema changes, all nullable
ALTER TABLE albums       ADD COLUMN musicbrainz_release_id       TEXT NULL;
ALTER TABLE albums       ADD COLUMN musicbrainz_release_group_id TEXT NULL;
ALTER TABLE pieces       ADD COLUMN musicbrainz_work_id          TEXT NULL;
ALTER TABLE composers    ADD COLUMN musicbrainz_artist_id        TEXT NULL;
ALTER TABLE album_tracks ADD COLUMN musicbrainz_recording_id     TEXT NULL;
```

Then `CanonAlbum.IdentityKey` becomes: prefer MBID; fall back to today's `Label|CatalogueNumber|Title|Subtitle` composite for unidentified albums. Same for composers (prefer artist-MBID), pieces (prefer work-MBID), and tracks (prefer recording-MBID).

This is the **dedup-correctness fix in disguise**: the H41/M39/M40 family of findings doesn't go away (you still need fuzzy matching for unidentified entries) but it becomes the fallback path, not the primary. The Karajan/Bernstein B9 confusion (H24/H26) ceases to be a possibility for any album the user has identified.

The **secondary win**: re-resolution after a rename or merge. If MusicBrainz merges two duplicate "Karajan, Herbert von" entries, the MBID stays valid (or is redirected). The user's local data references the MBID, so a re-fetch transparently follows the redirect. Today, if the user renames a composer locally, all the fuzzy refs in albums lose their anchor.

### Multiple recordings of the same work

Closely related, worth its own callout because it's the user's most common dedup case. The owner has many recordings of Beethoven 9:

- iTunes can't distinguish them beyond track-name string equality.
- MusicBrainz models this explicitly:
  - **work-MBID** = the composition itself (`b76f7e4e-a0e1-4c6d-9b8c-...` for Beethoven Sym 9)
  - **recording-MBID** = a specific performance (different for Karajan 1962, Karajan 1977, Bernstein NYP 1963, Bernstein Wiener 1989...)

If `album_track_piece_refs` carried a `musicbrainz_recording_id`, the question "do I already have this exact Karajan recording?" becomes a SQL equality check. Today it's a heuristic that involves matching album-title + performer-list across albums with the same work-piece-id.

### What would need to change in the code

**Minimal viable integration (Workflow 1 only)**:

1. Add `LookupReleaseAsync(string barcode)` to `ICatalogueReference`.
2. Implement it in `MusicBrainzReference` (one endpoint, one JSON shape — mirrors the existing `LookupComposerAsync`).
3. Stub it in `LocalCatalogueReference` and `ItunesLibraryReference` (return null — iTunes XML doesn't index by barcode).
4. Add the dispatch in `CompositeCatalogueReference`.
5. Add `albums.musicbrainz_release_id` column via `ApplySchemaUpgradesAsync` (the `EnsureColumnAsync` helper is already in place).
6. Add a `[Fetch ↻]` button in `AlbumEditorWindow` next to the Barcode field, wire it to the new method, pre-fill the fields.

That's maybe 200 lines of new code. The pre-requisites from `Rework.md` that should land first: **C9** (real exception handling), **C10** (thread-safe rate-limit), **M16** (`IHttpClientFactory`), **M17** (real User-Agent), **M18** (retry on 503). Together those make MusicBrainz reliable enough to depend on for an opt-in workflow.

**Stretch (Workflow 2)** — DiscID identification — adds:
- A `DiscIdProvider` that reads sidecar files from the EAC rip.
- A new `/discid` endpoint method on `MusicBrainzReference`.
- A disambiguation UI for the "multiple releases match this TOC" case.
- The existing `ArchiveScannerService` already enumerates the disc folders; adding sidecar discovery is local.

**Big payoff (Workflow 3 + 4)** — work enrichment + MBID-everywhere — would touch more of the schema and more code paths, but it's mostly additive: new nullable columns, new fetch buttons in `PieceEditorWindow` / `ComposerEditorWindow`, and refactoring the identity layer (`CanonAlbum.IdentityKey`, `PieceReferenceIndex.RegisterPiece`, the composer cache key in `CataloguingService`) to prefer MBID when present.

### What MusicBrainz won't help with

To set expectations:

- **Subjective formatting choices.** The user's preferred name format ("Brahms, Johannes (1833–1897)") and convention for catalogue numbers ("Op. 27 #2" vs "Op. 27 No. 2") — MusicBrainz returns canonical data but not in the user's preferred format. The cataloguing rules in `CataloguingRules.cs` (en-dash dates, hyphenated keys, padded movement numbers) would still apply after the fetch.
- **Recording sessions data quality.** MB has partial coverage of session metadata (venues, engineers, producers). For mainstream classical labels the data is good; for niche labels or older recordings, often sparse. Treat it as a starting point, not the final answer.
- **Conflicting opinions.** The user's local data may disagree with MusicBrainz on edge cases (e.g. what counts as a "movement" vs a "section"). The architecture should make MusicBrainz suggestions easy to accept, modify, or reject — never auto-overwrite curated state. This is the same principle behind the H42 "preserve IsProvisional" fix.

### Honest assessment

MusicBrainz integration of any depth requires the existing Rework findings in the MusicBrainz family to land first (C9, C10, M16-M18, M28, M81). Without those, the integration is unreliable enough that the user will lose trust in it after a few "MusicBrainz lookup failed silently" sessions.

Once those are fixed, **Workflow 1 (per-album barcode lookup)** is the highest-leverage smallest-effort addition. It populates Label / CatalogueNumber / Barcode for the albums where the user cares enough to enter the barcode, with very low friction.

**Workflow 4 (MBID-as-stable-identifier)** is the deepest architectural payoff — it makes a half-dozen `Rework.md` findings simpler or moot — but only matters once the user has a non-trivial number of identified albums to dedup against. For a brand-new install with no MBIDs populated, you still need the fuzzy paths.

**Workflow 3 (work + movement enrichment)** is the highest user-time-savings addition because every new piece in the canon currently requires the user to type all the movement names by hand. MusicBrainz already has them. The dead `LookupWorkAsync` code is half the wiring; the other half is a UI affordance and a "movements from MB" → `Subpieces` mapper.

**Workflow 2 (DiscID scan-time identification)** is the most ambitious because it requires changing the rip workflow upstream (telling EAC to write the sidecar file), but it's also the most automated — once configured, the user gets MusicBrainz suggestions for every newly-ripped album without touching anything.

If forced to pick one to land first, it'd be **Workflow 1 with an MBID column**. It establishes the pattern, proves the C9-C10-M16-M18 hardening was sufficient, and starts populating the MBID identifier graph that the other three workflows can then build on.

---

## Iterations to explore on revisit

Placeholder for future-self thinking. Possible threads:

- **Cover Art Archive integration** as a Workflow 5 — once MBIDs are populated, fetching the front cover from `coverartarchive.org/release/{mbid}` is a one-line addition. Store as a sidecar JPG in the album folder; surface in `AlbumsView` as a thumbnail column.
- **Discogs as an alternative source** — Discogs has stronger coverage of niche labels and bootlegs than MusicBrainz. Could be added as a 4th source in `CompositeCatalogueReference` (`LookupDiscogsAsync`) with its own API quirks. Slight risk: Discogs's data is more variable in quality.
- **Bulk enrichment pass** — once Workflow 1 works per-album, a "Enrich all unidentified albums" batch command in `ImportExportViewModel` could walk the catalogue, fetching MusicBrainz data for any album with a barcode but no MBID. Subject to rate limits (1 req/s × 3000 albums = ~50 min).
- **The "MusicBrainz suggests, user confirms" UI pattern** — what does the disambiguation screen look like when MB returns multiple candidates? Probably a side-by-side: "Your album: X — Y" vs each candidate. Cover thumbnails would help disambiguation.
- **Going further on work-MBID** — could pre-load common composer works (Beethoven's 32 sonatas, Bach's WTC, Mahler's 10 symphonies) into the canon DB with MBIDs as a "starter pack". Saves the per-piece lookup. Risks: opinionated canonical-form choices, version drift if MB updates the work entity.
- **The MBID-as-stable-identifier refactor (Workflow 4)** — sequence matters. The `IdentityKey` callers (load-mutate-save album path, dedup logic in iTunes import, composer cache in CataloguingService) all need to handle "MBID-present" + "MBID-absent" paths during the transition. Probably best to land the schema columns first, then progressively prefer MBID at each call site over multiple PRs.
- **Cache strategy** — a local cache of MusicBrainz responses (keyed by MBID + entity type) keyed in a new SQLite table would avoid re-fetching the same release detail across sessions. Useful for the bulk enrichment case.
- **Testing strategy** — MusicBrainz integration tests are tricky (network-dependent, rate-limited). A `RecordedMusicBrainzReference` that replays canned HTTP responses from JSON fixtures would let the test suite exercise the full chain without hitting the real API.

---

*Last updated: end of review session that produced Rework.md.*
