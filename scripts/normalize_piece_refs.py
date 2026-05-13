"""
Normalise TrackPieceRef.piece_title to the resolved piece's qualified
DisplayTitle form (key + catalog), matching PiecePickerWindow's new
behaviour so "Chopin – Scherzo #1" becomes "Chopin – Scherzo #1 in b, Op. 20"
on existing albums.

Dry-run by default. Pass --apply to write back to the canonical files.
Creates a timestamped backup of Classical Canon albums.json before writing.
"""
import argparse
import io
import json
import shutil
import sys
from datetime import datetime
from pathlib import Path

# Force UTF-8 console output — Windows cp1252 can't print Unicode accidentals.
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT   = Path(__file__).parent.parent
DATA   = ROOT / "data"
PIECES = DATA / "Classical Canon pieces.json"
ALBUMS = DATA / "Classical Canon albums.json"


# ── CanonPiece.BuildDisplayTitle replica (includeCatalog=True) ───────────────

def title_case(s):
    # CanonPiece.TitleCase preserves existing capitalisation after the first
    # letter, so "Piano Sonata" stays "Piano Sonata". We only uppercase the
    # first char if it's lowercase — matches the spirit of the C# helper for
    # our purposes here.
    if not s:
        return s
    return s[0].upper() + s[1:] if s[0].islower() else s


def catalog_of(piece):
    ci = piece.get("catalog_info") or []
    if not ci:
        return ""
    c = ci[0]
    cat  = (c.get("catalog") or "").strip()
    num  = (c.get("catalog_number") or "").strip()
    sub  = (c.get("catalog_subnumber") or "").strip()
    if num and sub:
        return f"{cat} {num} #{sub}".strip()
    if num:
        return f"{cat} {num}".strip()
    if sub:
        return f"{cat} #{sub}".strip()
    return cat


def key_display(piece):
    kt = (piece.get("key_tonality") or "").strip()
    km = (piece.get("key_mode") or "").strip().lower()
    if not kt:
        return ""
    return kt.lower() if km == "minor" else kt


def display_title(piece, include_nick_sub=True):
    """Replica of CanonPiece.BuildDisplayTitle(includeCatalog=True) for top-level pieces.
    include_nick_sub=False mirrors PieceReferenceIndex.StripNicknameAndSubtitle — drops
    the trailing subtitle/nickname suffixes that album refs historically omit."""
    title    = piece.get("title")
    form     = piece.get("form")
    number   = piece.get("number")
    subtitle = piece.get("subtitle")
    nickname = piece.get("nickname")
    key      = key_display(piece)
    catalog  = catalog_of(piece)

    # Explicit title path.
    if title:
        parts = [title]
        if key:
            parts[0] += f" in {key}"
        if catalog:
            parts.append(catalog)
        result = ", ".join(parts)
        if include_nick_sub:
            if subtitle:
                result += f", {subtitle}"
            if nickname:
                result += f' "{nickname}"'
        return result

    # Form+number+key+catalog path.
    main = ""
    if form:
        main = title_case(form)
        if number is not None:
            main += f" #{number}"
    if key:
        main += f" in {key}"
    main = main.strip()

    parts = [main] if main else []
    if catalog:
        parts.append(catalog)
    result = ", ".join(parts)
    if include_nick_sub:
        if subtitle:
            result += f", {subtitle}"
        if nickname:
            result += f' "{nickname}"'
    return result


def canonical_ref_title(piece):
    """The form that album refs should store — DisplayTitle minus nickname/subtitle."""
    return display_title(piece, include_nick_sub=False)


def normalize(s):
    if not s:
        return ""
    return (s.replace("\u266D", "-flat").replace("\u266F", "-sharp")).strip().lower()


# ── Main ─────────────────────────────────────────────────────────────────────

def register_piece(p, composer, idx):
    """Register every short (unqualified) variant of a piece's title, mapping
    each to the qualified canonical_ref_title. We intentionally omit the
    already-qualified forms (DisplayTitle, canonical_ref_title) — this is
    an *upgrade* index, not a full lookup. Refs that already store a
    qualified title aren't changed, whether they include the nickname or not.

    Also registers the piece's variants for set-member recursion below."""
    canonical = canonical_ref_title(p)
    cat = catalog_of(p)
    key = key_display(p)

    short_forms = set()

    # 1. Explicit raw Title (e.g. "Scherzo #1", "Choral Fantasy") — the
    #    classic unqualified case where the picker previously stored
    #    `piece.Title` and dropped the key/catalog.
    if p.get("title"):
        short_forms.add(p["title"])

    # 2. Form+number (no key) — e.g. "Piano Sonata #14" without the "in c♯".
    form   = p.get("form")
    number = p.get("number")
    if form and not p.get("title"):
        main = title_case(form)
        if number is not None:
            main += f" #{number}"
        short_forms.add(main.strip())

    # 3. Form+number+key without catalog — DisplayTitleShort.
    if cat:
        ds_short = canonical[: -(len(cat) + 2)] if canonical.endswith(", " + cat) else None
        if ds_short:
            short_forms.add(ds_short)

    for v in short_forms:
        if v and v != canonical:
            idx.setdefault(composer, {}).setdefault(normalize(v), canonical)

    # Recurse into "set"-type containers: their subpieces are independent
    # works (e.g. Beethoven Three Piano Sonatas Op. 31 → sonatas #16/#17/#18),
    # each referenced by its own title from album tracks.
    if (p.get("form") or "").strip().lower() == "set":
        for sub in p.get("subpieces") or []:
            sub_composer = (sub.get("composer") or composer).strip() or composer
            register_piece(sub, sub_composer, idx)


def build_index(pieces):
    """(composer, normalised-title) → canonical display_title."""
    idx = {}
    for p in pieces:
        composer = (p.get("composer") or "").strip()
        if composer:
            register_piece(p, composer, idx)
    return idx


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true",
                    help="Write changes back to Classical Canon albums.json.")
    args = ap.parse_args()

    pieces = json.loads(PIECES.read_text(encoding="utf-8"))
    albums = json.loads(ALBUMS.read_text(encoding="utf-8"))

    idx = build_index(pieces)

    updates = []

    for a in albums:
        for d in a.get("discs") or []:
            for t in d.get("tracks") or []:
                for r in t.get("piece_refs") or []:
                    composer = (r.get("composer") or "").strip()
                    current  = (r.get("piece_title") or "").strip()
                    # Index only contains short forms → if lookup hits, the
                    # ref is missing qualification and we upgrade. Qualified
                    # refs (with catalog, with or without nickname) aren't in
                    # the index and are left alone.
                    qual = idx.get(composer, {}).get(normalize(current))
                    if qual is not None and qual != current:
                        updates.append({
                            "album":    a["title"],
                            "disc":     d.get("disc_number"),
                            "track":    t.get("track_number"),
                            "composer": composer,
                            "from":     current,
                            "to":       qual,
                            "ref":      r,
                        })

    print(f"Scanned albums: {len(albums)}")
    print(f"Refs to upgrade (missing key or catalog): {len(updates)}")

    if updates:
        print()
        print("--- Proposed updates ---")
        for u in updates:
            print(f"  {u['album']} D{u['disc']} T{u['track']}: {u['composer']}")
            print(f"    -  {u['from']}")
            print(f"    +  {u['to']}")

    if args.apply and updates:
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        backup = ALBUMS.with_suffix(ALBUMS.suffix + f".bak.{ts}")
        shutil.copy2(ALBUMS, backup)
        print()
        print(f"Backup written: {backup}")

        # Apply in-place (the ref dicts are already the live objects).
        for u in updates:
            u["ref"]["piece_title"] = u["to"]

        ALBUMS.write_text(
            json.dumps(albums, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"Wrote updated {ALBUMS.name}: {len(updates)} refs normalised.")
    elif args.apply:
        print()
        print("No changes to write.")
    else:
        print()
        print("(dry run — pass --apply to write changes)")


if __name__ == "__main__":
    main()
