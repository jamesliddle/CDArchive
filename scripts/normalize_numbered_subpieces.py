"""
normalize_numbered_subpieces.py

Mass-updates `numbered_subpieces` on every piece/subpiece according to the rule:
  - Leaf subpieces (no children) are numbered, EXCEPT inside operas.
  - Non-leaf subpieces (have children of their own) are NOT numbered.

`numbered_subpieces` is serialised only when it differs from the computed default:
  - Default TRUE  when InstrumentationCategory is set and is not "Opera".
  - Default FALSE when InstrumentationCategory is empty or is "Opera".

So the field is written to JSON only as an override; otherwise it is removed (null).

Run from the repo root:
    python scripts/normalize_numbered_subpieces.py
"""

import json
import sys
from pathlib import Path

PIECES_JSON = Path("data/Classical Canon pieces.json")
OPERA_CATEGORIES = {"opera", "musical theater"}


def default_numbered(piece: dict) -> bool:
    """Replicate EffectiveSubpiecesNumbered default logic from CanonPiece.cs."""
    cat = (piece.get("instrumentation_category") or "").lower().strip()
    return bool(cat) and cat not in OPERA_CATEGORIES


def is_leaf(piece: dict) -> bool:
    return not piece.get("subpieces")


def apply_numbering(parent: dict, opera_context: bool) -> None:
    """
    Set (or remove) `numbered_subpieces` on *parent* based on whether its
    direct children are leaves and whether we are inside an opera.
    Then recurse into those children.
    """
    subpieces = parent.get("subpieces")
    if not subpieces:
        return

    all_leaves = all(is_leaf(sp) for sp in subpieces)
    desired = all_leaves and not opera_context

    if desired == default_numbered(parent):
        # Matches the default — remove any explicit override to keep JSON clean.
        parent.pop("numbered_subpieces", None)
    else:
        parent["numbered_subpieces"] = desired

    for sp in subpieces:
        apply_numbering(sp, opera_context)


def process_piece(piece: dict) -> None:
    cat = (piece.get("instrumentation_category") or "").lower().strip()
    opera_context = cat in OPERA_CATEGORIES

    apply_numbering(piece, opera_context)

    # Versions can also have subpieces.
    for version in piece.get("versions") or []:
        apply_numbering(version, opera_context)


def main() -> None:
    if not PIECES_JSON.exists():
        print(f"ERROR: {PIECES_JSON} not found. Run from the repo root.", file=sys.stderr)
        sys.exit(1)

    with PIECES_JSON.open(encoding="utf-8") as f:
        pieces = json.load(f)

    for piece in pieces:
        process_piece(piece)

    with PIECES_JSON.open("w", encoding="utf-8") as f:
        json.dump(pieces, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Done. Updated {PIECES_JSON}.")


if __name__ == "__main__":
    main()
