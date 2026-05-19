"""
Verify that every track in mahler_albums.json resolves against
mahler_pieces.json using logic equivalent to PieceReferenceIndex.TryResolve.

Reports unresolved track references so we can tweak piece definitions
or album track data before the user imports them.
"""

import json
import re
import unicodedata
from pathlib import Path

ROOT = Path(__file__).parent.parent
PIECES = ROOT / "data" / "mahler_pieces.json"
ALBUMS = ROOT / "data" / "mahler_albums.json"


def normalize_title(s):
    if not s:
        return ""
    # Match PieceReferenceIndex.NormalizeTitle: swap Unicode accidentals for ASCII.
    s = s.replace("\u266D", "-flat").replace("\u266F", "-sharp")
    return s.strip()


def display_title(p):
    """Replicates CanonPiece.BuildDisplayTitle(includeCatalog=true)."""
    title    = p.get("title")
    form     = p.get("form")
    number   = p.get("number")
    key_t    = p.get("key_tonality")
    key_m    = p.get("key_mode")
    subtitle = p.get("subtitle")
    nickname = p.get("nickname")
    catalog_info = p.get("catalog_info") or []
    catalog = ""
    if catalog_info:
        c = catalog_info[0]
        catalog = f"{c.get('catalog','')} {c.get('catalog_number','')}".strip()
        if c.get("catalog_subnumber"):
            catalog += f" #{c['catalog_subnumber']}"

    if key_t:
        key_display = key_t.lower() if (key_m or "").lower() == "minor" else key_t
    else:
        key_display = ""

    if title:
        parts = [title]
        if key_display:
            parts[0] += f" in {key_display}"
        if catalog:
            parts.append(catalog)
        result = ", ".join(parts)
        if subtitle:
            result += f", {subtitle}"
        if nickname:
            result += f' "{nickname}"'
        return result

    main = ""
    if form:
        main = form
        if number is not None:
            main += f" #{number}"
    if key_display:
        main += f" in {key_display}"
    main = main.strip()
    parts = [main] if main else []
    if catalog:
        parts.append(catalog)
    result = ", ".join(parts) if parts else ""
    if subtitle:
        result += f", {subtitle}"
    if nickname:
        result += f' "{nickname}"'
    return result


def subpiece_display(sp):
    """Simplified: what the resolver compares subpieces against.
    Equivalent to SubpieceDisplayTitle for leaves: '{Number}. {tempo or form. tempo}'"""
    number = sp.get("number")
    title  = sp.get("title")
    tempos = sp.get("tempos") or []
    form   = sp.get("form")
    first_line = sp.get("first_line")

    prefix = f"{number}. " if number is not None else ""

    # If there's an explicit title, use it (like BuildDisplayTitle's title path)
    if title:
        return prefix + title

    if tempos:
        tempo_desc = " - ".join(
            t.get("tempo_description") or t.get("description") or "" for t in tempos
        )
        if form:
            return prefix + f"{form}. {tempo_desc}"
        return prefix + tempo_desc

    if first_line:
        if form:
            return prefix + f"{form}. {first_line}"
        return prefix + first_line

    return prefix.strip() or "(unnamed subpiece)"


def resolve_ref(ref, pieces_by_composer_title):
    """Returns (ok, piece, subpiece_path_resolved_count) or (False, None, segments_matched)."""
    composer = (ref.get("composer") or "").strip()
    title = (ref.get("piece_title") or "").strip()
    path = ref.get("subpiece_path") or []

    titles = pieces_by_composer_title.get(composer, {})
    piece = titles.get(normalize_title(title))
    if not piece:
        return False, None, f"piece not found: '{title}'"

    current = piece.get("subpieces") or []
    for i, seg in enumerate(path):
        norm_seg = normalize_title(seg)
        if not current:
            return False, None, f"no subpieces at depth {i}: '{seg}'"

        match = None
        # strict: exact match against computed display title of each subpiece
        for sp in current:
            cand_title = sp.get("title") or ""
            if normalize_title(cand_title).lower() == norm_seg.lower():
                match = sp; break
            if normalize_title(subpiece_display(sp)).lower() == norm_seg.lower():
                match = sp; break

        # loose: SubpieceDisplayTitle prefix + ". " or " - "
        if match is None:
            for sp in current:
                st = normalize_title(subpiece_display(sp)).lower()
                if st and (norm_seg.lower().startswith(st + " - ")
                           or norm_seg.lower().startswith(st + ". ")):
                    match = sp; break

        # loose: "N. " or "N<letter>. " numeric prefix, plus zero-padded variants
        if match is None:
            for sp in current:
                n = sp.get("number")
                if n is None: continue
                digits = str(n)
                if norm_seg.startswith(f"{digits}. "):
                    match = sp; break
                if (len(norm_seg) >= len(digits) + 3
                        and norm_seg.startswith(digits)
                        and norm_seg[len(digits)].isalpha()
                        and norm_seg[len(digits)+1] == "."
                        and norm_seg[len(digits)+2] == " "):
                    match = sp; break
                # zero-padded numeric prefix: "01. ", "02. ", ...
                padded = digits.rjust(2, "0") + ". "
                if padded != f"{digits}. " and norm_seg.startswith(padded):
                    match = sp; break
                # reverse: subpiece display has "N. " prefix, segment does not
                sub_display = normalize_title(subpiece_display(sp))
                num_prefix = f"{digits}. "
                if (sub_display.startswith(num_prefix)
                        and sub_display[len(num_prefix):].lower() == norm_seg.lower()):
                    match = sp; break

        if match is None:
            return False, None, f"subpiece not found at depth {i}: '{seg}'"
        current = match.get("subpieces") or []

    return True, piece, len(path)


def build_title_index(pieces):
    # composer -> {normalized_title: piece}
    idx = {}
    for p in pieces:
        composer = p.get("composer") or ""
        titles = set()
        if p.get("title"):
            titles.add(p["title"])
        titles.add(display_title(p))
        # also add DisplayTitleShort-ish variant (no catalog)
        # and nickname-stripped
        dt = display_title(p)
        titles.add(dt)
        # strip nickname suffix
        nick = p.get("nickname")
        if nick:
            suf = f' "{nick}"'
            if dt.endswith(suf):
                titles.add(dt[:-len(suf)])
        # strip subtitle suffix
        sub = p.get("subtitle")
        if sub:
            suf = f", {sub}"
            # try after removing nickname first
            stripped = dt
            if nick and stripped.endswith(f' "{nick}"'):
                stripped = stripped[:-len(f' "{nick}"')]
            if stripped.endswith(suf):
                titles.add(stripped[:-len(suf)])
        for t in titles:
            idx.setdefault(composer, {})[normalize_title(t)] = p
    return idx


def main():
    with PIECES.open(encoding="utf-8") as f:
        pieces = json.load(f)
    with ALBUMS.open(encoding="utf-8") as f:
        albums = json.load(f)

    idx = build_title_index(pieces)

    total_refs = 0
    unresolved = []
    per_piece_albums = {}

    for a in albums:
        for d in a["discs"]:
            for t in d["tracks"]:
                refs = t.get("piece_refs") or []
                for r in refs:
                    total_refs += 1
                    ok, piece, info = resolve_ref(r, idx)
                    if not ok:
                        unresolved.append({
                            "album": a["title"],
                            "disc": d["disc_number"],
                            "track": t.get("track_number"),
                            "ref": r,
                            "reason": info,
                        })
                    else:
                        per_piece_albums.setdefault(piece.get("title") or display_title(piece), set()).add(a["title"])

    print(f"Total refs: {total_refs}")
    print(f"Unresolved: {len(unresolved)}")
    if unresolved:
        print()
        print("--- Unresolved ---")
        for u in unresolved[:40]:
            print(f"  {u['album']} D{u['disc']} T{u['track']}: {u['reason']}")
            print(f"    ref: {u['ref']}")

    print()
    print("--- Albums per piece (distinct) ---")
    for title, albums_set in sorted(per_piece_albums.items()):
        print(f"  {title}: {len(albums_set)}")


if __name__ == "__main__":
    main()
