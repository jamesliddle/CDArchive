"""
Merge data/beethoven_symphonies_albums.json into data/Classical Canon albums.json.

Merge-only semantics: match by lower-cased title; existing albums are never
overwritten. Writes a timestamped backup of the canonical albums file before
applying.

Dry-run by default. Pass --apply to write.
"""
import argparse
import io
import json
import shutil
import sys
from datetime import datetime
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = Path(__file__).parent.parent
DATA = ROOT / "data"

CANON_ALBUMS = DATA / "Classical Canon albums.json"
NEW_ALBUMS   = DATA / "beethoven_symphonies_albums.json"


def album_key(a):
    return (a.get("title") or "").strip().lower()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="Write changes back.")
    args = ap.parse_args()

    canon = json.loads(CANON_ALBUMS.read_text(encoding="utf-8"))
    new   = json.loads(NEW_ALBUMS.read_text(encoding="utf-8"))

    existing_keys = {album_key(a) for a in canon}

    to_add = []
    to_skip = []
    for na in new:
        (to_skip if album_key(na) in existing_keys else to_add).append(na)

    print(f"Existing albums in canon: {len(canon)}")
    print(f"Incoming albums:          {len(new)}")
    print(f"To add:                   {len(to_add)}")
    print(f"To skip (duplicates):     {len(to_skip)}")
    print()

    if to_add:
        print("--- Will add ---")
        for a in to_add:
            disc_count  = len(a.get("discs") or [])
            track_count = sum(len(d.get("tracks") or []) for d in (a.get("discs") or []))
            cataloged   = sum(
                1 for d in (a.get("discs") or [])
                for t in (d.get("tracks") or [])
                if t.get("piece_refs")
            )
            print(f"  + {a['title']}  "
                  f"[{disc_count} disc(s), {track_count} tracks, {cataloged} catalogued]")

    if to_skip:
        print()
        print("--- Will skip (already in canon) ---")
        for a in to_skip:
            print(f"  = {a['title']}")

    if not args.apply:
        print()
        print("(dry run — pass --apply to write)")
        return

    if not to_add:
        print()
        print("No new albums to write.")
        return

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup = CANON_ALBUMS.with_suffix(CANON_ALBUMS.suffix + f".bak.{ts}")
    shutil.copy2(CANON_ALBUMS, backup)
    print()
    print(f"Backup written: {backup}")

    canon.extend(to_add)
    CANON_ALBUMS.write_text(
        json.dumps(canon, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(f"Wrote {CANON_ALBUMS.name}: +{len(to_add)} albums "
          f"(total now {len(canon)}).")


if __name__ == "__main__":
    main()
