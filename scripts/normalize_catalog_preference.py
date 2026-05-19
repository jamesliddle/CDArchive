"""
One-off migration to apply each composer's catalog_prefixes preference to the
on-disk canon pieces, and cascade the resulting display-title changes into
album refs.

Problem: when a composer has a preferred catalog order (e.g. Chopin ["Op.", "B."]),
the app applies it in memory at load time and on save, but pieces.json itself may
still store catalog_info in the old order (e.g. [{B., 65}, {Op., 20}]). Album
refs captured before the preference feature existed will also have the stale
catalog — "Scherzo #1 in b, B. 65" instead of "Scherzo #1 in b, Op. 20".

This script:
  1. Loads composers.json and builds composer -> catalog_prefixes.
  2. Loads pieces.json, reorders catalog_info (recursively into subpieces,
     versions, and version subpieces) per each piece's composer preference.
     Stable partition: preferred prefixes move to front in preference order;
     unmatched entries keep their original relative order.
  3. For each top-level piece whose *display title* changed as a result,
     records an old -> new rename keyed by composer.
  4. Loads albums.json and rewrites any piece_ref whose (composer, piece_title)
     matches a rename.

Dry-run by default. Pass --apply to write both pieces.json and albums.json back
to disk (with timestamped backups).
"""
import argparse
import io
import json
import shutil
import sys
from datetime import datetime
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT      = Path(__file__).parent.parent
DATA      = ROOT / "data"
COMPOSERS = DATA / "Classical Canon composers.json"
PIECES    = DATA / "Classical Canon pieces.json"
ALBUMS    = DATA / "Classical Canon albums.json"


# ── DisplayTitle replica (matches CanonPiece.BuildDisplayTitle) ──────────────

def title_case(s):
    if not s:
        return s
    return s[0].upper() + s[1:] if s[0].islower() else s


def catalog_of(piece):
    ci = piece.get("catalog_info") or []
    if not ci:
        return ""
    c = ci[0]
    cat = (c.get("catalog") or "").strip()
    num = (c.get("catalog_number") or "").strip()
    sub = (c.get("catalog_subnumber") or "").strip()
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
    title    = piece.get("title")
    form     = piece.get("form")
    number   = piece.get("number")
    subtitle = piece.get("subtitle")
    nickname = piece.get("nickname")
    key      = key_display(piece)
    catalog  = catalog_of(piece)

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


# ── Stable partition: move preferred prefixes to front ───────────────────────

def sort_catalog_info(catalog_info, preferred_prefixes):
    """Reorder catalog_info in place per the preference list (case-insensitive).
    Stable: unmatched entries keep their original relative order, after all
    matched entries. Returns True if order actually changed."""
    if not catalog_info or len(catalog_info) < 2 or not preferred_prefixes:
        return False

    prefs_lower = [p.lower() for p in preferred_prefixes]

    def rank(entry):
        cat = (entry.get("catalog") or "").strip().lower()
        try:
            return prefs_lower.index(cat)
        except ValueError:
            return len(prefs_lower)  # unmatched — goes last

    indexed = list(enumerate(catalog_info))
    indexed.sort(key=lambda t: (rank(t[1]), t[0]))
    new_order = [entry for _, entry in indexed]

    changed = new_order != catalog_info
    catalog_info[:] = new_order
    return changed


def sort_piece_recursive(piece, preferred_prefixes):
    """Apply preference to piece.catalog_info, subpieces, and version chains."""
    changed = sort_catalog_info(piece.get("catalog_info"), preferred_prefixes)
    for sub in piece.get("subpieces") or []:
        if sort_piece_recursive(sub, preferred_prefixes):
            changed = True
    for ver in piece.get("versions") or []:
        if sort_catalog_info(ver.get("catalog_info"), preferred_prefixes):
            changed = True
        for sub in ver.get("subpieces") or []:
            if sort_piece_recursive(sub, preferred_prefixes):
                changed = True
    return changed


# ── Album-ref stripping (mirrors PieceReferenceIndex.StripNicknameAndSubtitle) ─

def strip_nick_sub(piece):
    """Album refs historically stored the display title minus nickname/subtitle.
    Produce that canonical stripped form for rename matching."""
    return display_title(piece, include_nick_sub=False)


# ── Main ────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true",
                    help="Write changes back to pieces.json and albums.json.")
    args = ap.parse_args()

    composers = json.loads(COMPOSERS.read_text(encoding="utf-8"))
    pieces    = json.loads(PIECES.read_text(encoding="utf-8"))
    albums    = json.loads(ALBUMS.read_text(encoding="utf-8"))

    prefs_by_composer = {
        c["name"]: c["catalog_prefixes"]
        for c in composers
        if (c.get("catalog_prefixes") or [])
    }
    print(f"Composers with catalog preference: {len(prefs_by_composer)}")
    for name, prefs in prefs_by_composer.items():
        print(f"  {name}: {prefs}")
    print()

    # Walk pieces, reorder, collect per-composer renames for top-level pieces.
    # renames[composer] = { old_title: new_title }  — both the full (with nick/sub)
    # and the stripped form are emitted, to cover both historical ref formats.
    renames = {}
    piece_changes = 0

    for p in pieces:
        composer = (p.get("composer") or "").strip()
        prefs = prefs_by_composer.get(composer)
        if not prefs:
            continue

        old_full     = display_title(p, include_nick_sub=True)
        old_stripped = display_title(p, include_nick_sub=False)

        if sort_piece_recursive(p, prefs):
            piece_changes += 1
            new_full     = display_title(p, include_nick_sub=True)
            new_stripped = display_title(p, include_nick_sub=False)
            m = renames.setdefault(composer, {})
            if old_full != new_full:
                m[old_full] = new_full
            if old_stripped != new_stripped:
                m[old_stripped] = new_stripped

    print(f"Top-level pieces with catalog_info reordered: {piece_changes}")
    print()
    if renames:
        print("--- Rename map (display titles) ---")
        for composer, m in renames.items():
            for old, new in m.items():
                print(f"  {composer}")
                print(f"    -  {old}")
                print(f"    +  {new}")
    print()

    # Walk album refs, apply renames.
    ref_updates = []
    for a in albums:
        for d in a.get("discs") or []:
            for t in d.get("tracks") or []:
                for r in t.get("piece_refs") or []:
                    composer = (r.get("composer") or "").strip()
                    current  = (r.get("piece_title") or "").strip()
                    new = renames.get(composer, {}).get(current)
                    if new and new != current:
                        ref_updates.append({
                            "album":    a.get("title"),
                            "disc":     d.get("disc_number"),
                            "track":    t.get("track_number"),
                            "composer": composer,
                            "from":     current,
                            "to":       new,
                            "ref":      r,
                        })

    print(f"Album refs to update: {len(ref_updates)}")
    if ref_updates:
        print()
        print("--- Proposed album-ref updates ---")
        for u in ref_updates:
            print(f"  {u['album']} D{u['disc']} T{u['track']}: {u['composer']}")
            print(f"    -  {u['from']}")
            print(f"    +  {u['to']}")

    if not args.apply:
        print()
        print("(dry run — pass --apply to write changes)")
        return

    if piece_changes == 0 and not ref_updates:
        print()
        print("No changes to write.")
        return

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")

    if piece_changes > 0:
        backup = PIECES.with_suffix(PIECES.suffix + f".bak.{ts}")
        shutil.copy2(PIECES, backup)
        print()
        print(f"Backup written: {backup}")
        PIECES.write_text(
            json.dumps(pieces, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"Wrote updated {PIECES.name}: {piece_changes} pieces reordered.")

    if ref_updates:
        backup = ALBUMS.with_suffix(ALBUMS.suffix + f".bak.{ts}")
        shutil.copy2(ALBUMS, backup)
        print(f"Backup written: {backup}")
        for u in ref_updates:
            u["ref"]["piece_title"] = u["to"]
        ALBUMS.write_text(
            json.dumps(albums, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"Wrote updated {ALBUMS.name}: {len(ref_updates)} refs renamed.")


if __name__ == "__main__":
    main()
