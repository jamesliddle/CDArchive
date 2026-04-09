"""
sync_and_normalize.py

Step 1 — Sync: reads all pieces from ClassicalCanon.db and writes the canonical
         pieces JSON, mirroring what the app's "Sync to Canonical JSON" function
         does for top-level piece data.

Step 2 — Normalize: applies the numbered_subpieces rule:
         - Parent of leaf subpieces, not in an opera → numbered_subpieces: true
           (or null when the default already gives true)
         - Parent of non-leaf subpieces, or inside an opera → numbered_subpieces: false
           (or null when the default already gives false)

Run from the repo root:
    python scripts/sync_and_normalize.py
"""

import json
import sqlite3
import sys
from pathlib import Path

DB_PATH    = Path("data/ClassicalCanon.db")
PIECES_JSON = Path("data/Classical Canon pieces.json")

OPERA_CATEGORIES = {"opera", "musical theater"}

# Maps (DB column name, JSON property name) for plain scalar columns.
# Values that are None/null are omitted from the JSON output.
SCALAR_COLUMNS = [
    ("Composer",               "composer"),
    ("Form",                   "form"),
    ("Title",                  "title"),
    ("TitleEnglish",           "title_english"),
    ("Nickname",               "nickname"),
    ("Subtitle",               "subtitle"),
    ("Number",                 "number"),
    ("MusicNumber",            "music_number"),
    ("KeyTonality",            "key_tonality"),
    ("KeyMode",                "key_mode"),
    ("InstrumentationCategory","instrumentation_category"),
    ("PublicationYear",        "publication_year"),
    ("NumberedSubpieces",      "numbered_subpieces"),
    ("FirstLine",              "first_line"),
]

# Maps (DB column name, JSON property name) for JSON blob columns.
# Empty/null blobs are omitted from the JSON output.
BLOB_COLUMNS = [
    ("CatalogInfoJson",      "catalog_info"),
    ("InstrumentationJson",  "instrumentation"),
    ("CompositionYearsJson", "composition_years"),
    ("TextAuthorJson",       "text_author"),
    ("ArrangementsJson",     "arrangements"),
    ("RolesJson",            "roles"),
    ("CadenzaJson",          "cadenza"),
    ("TitleNumberJson",      "title_number"),
    ("TemposJson",           "tempos"),
    ("SubpiecesJson",        "subpieces"),
    ("VersionsJson",         "versions"),
]


# ── Step 1: sync ─────────────────────────────────────────────────────────────

def sync_from_db() -> list[dict]:
    if not DB_PATH.exists():
        print(f"ERROR: {DB_PATH} not found. Run from the repo root.", file=sys.stderr)
        sys.exit(1)

    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    try:
        rows = conn.execute(
            "SELECT * FROM Pieces "
            "ORDER BY Composer, CatalogSortPrefix, CatalogSortNumber, CatalogSortSuffix, Title"
        ).fetchall()
    finally:
        conn.close()

    pieces = []
    for row in rows:
        piece: dict = {}

        for col, json_key in SCALAR_COLUMNS:
            val = row[col]
            if val is not None:
                # SQLite stores booleans as 0/1; convert to Python bool.
                if col == "NumberedSubpieces":
                    val = bool(val)
                piece[json_key] = val

        for col, json_key in BLOB_COLUMNS:
            raw = row[col]
            if raw:
                piece[json_key] = json.loads(raw)

        pieces.append(piece)

    return pieces


# ── Step 2: normalize ─────────────────────────────────────────────────────────

def default_numbered(piece: dict) -> bool:
    """Replicates EffectiveSubpiecesNumbered default from CanonPiece.cs."""
    cat = (piece.get("instrumentation_category") or "").lower().strip()
    return bool(cat) and cat not in OPERA_CATEGORIES


def is_leaf(piece: dict) -> bool:
    return not piece.get("subpieces")


def apply_numbering(parent: dict, opera_context: bool) -> None:
    subpieces = parent.get("subpieces")
    if not subpieces:
        return

    all_leaves = all(is_leaf(sp) for sp in subpieces)
    desired = all_leaves and not opera_context

    if desired == default_numbered(parent):
        parent.pop("numbered_subpieces", None)
    else:
        parent["numbered_subpieces"] = desired

    for sp in subpieces:
        apply_numbering(sp, opera_context)


def normalize(pieces: list[dict]) -> None:
    for piece in pieces:
        cat = (piece.get("instrumentation_category") or "").lower().strip()
        opera_context = cat in OPERA_CATEGORIES

        apply_numbering(piece, opera_context)

        for version in piece.get("versions") or []:
            apply_numbering(version, opera_context)


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> None:
    print("Syncing from database…")
    pieces = sync_from_db()
    print(f"  Read {len(pieces)} pieces from {DB_PATH}")

    print("Normalizing numbered_subpieces…")
    normalize(pieces)

    with PIECES_JSON.open("w", encoding="utf-8") as f:
        json.dump(pieces, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"  Written to {PIECES_JSON}")
    print("Done.")


if __name__ == "__main__":
    main()
