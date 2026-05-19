"""
Merge scripts/data/mahler_pieces.json and data/mahler_albums.json into the
canonical Classical Canon JSON files. Does NOT overwrite existing entries —
pieces are matched by (composer, title/display-title) and albums by title.
"""
import json
from pathlib import Path

ROOT = Path(__file__).parent.parent
DATA = ROOT / "data"

CANON_PIECES = DATA / "Classical Canon pieces.json"
CANON_ALBUMS = DATA / "Classical Canon albums.json"
NEW_PIECES   = DATA / "mahler_pieces.json"
NEW_ALBUMS   = DATA / "mahler_albums.json"


def load(p):
    with p.open(encoding="utf-8") as f:
        return json.load(f)


def save(p, data):
    with p.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def piece_key(p):
    composer = (p.get("composer") or "").strip().lower()
    title = (p.get("title") or "").strip().lower()
    if title:
        return (composer, title)
    form = (p.get("form") or "").strip().lower()
    number = p.get("number")
    key_t  = (p.get("key_tonality") or "").strip().lower()
    return (composer, form, number, key_t)


def album_key(a):
    return (a.get("title") or "").strip().lower()


def strip_internal_fields(piece):
    """Remove internal-only fields like `_itunes_titles` before writing."""
    clean = {k: v for k, v in piece.items() if not k.startswith("_")}
    if "subpieces" in clean:
        clean["subpieces"] = [strip_internal_fields(sp) for sp in clean["subpieces"]]
    return clean


def main():
    pieces = load(CANON_PIECES)
    albums = load(CANON_ALBUMS)
    new_pieces = load(NEW_PIECES)
    new_albums = load(NEW_ALBUMS)

    existing_piece_keys = {piece_key(p) for p in pieces}
    existing_album_keys = {album_key(a) for a in albums}

    added_pieces = 0
    skipped_pieces = 0
    for np in new_pieces:
        clean = strip_internal_fields(np)
        k = piece_key(clean)
        if k in existing_piece_keys:
            skipped_pieces += 1
            continue
        pieces.append(clean)
        existing_piece_keys.add(k)
        added_pieces += 1

    added_albums = 0
    skipped_albums = 0
    for na in new_albums:
        k = album_key(na)
        if k in existing_album_keys:
            skipped_albums += 1
            continue
        albums.append(na)
        existing_album_keys.add(k)
        added_albums += 1

    save(CANON_PIECES, pieces)
    save(CANON_ALBUMS, albums)
    print(f"pieces: +{added_pieces} added, {skipped_pieces} skipped (duplicates)")
    print(f"albums: +{added_albums} added, {skipped_albums} skipped (duplicates)")
    print(f"total pieces in canon: {len(pieces)}")
    print(f"total albums in canon: {len(albums)}")


if __name__ == "__main__":
    main()
