"""
Extract all iTunes tracks on albums containing Beethoven symphonies.

"Contains a Beethoven symphony" = at least one track on the album is both
composed by Beethoven and has a work title starting with "Symphony" (i.e. the
portion of the iTunes Name field before the first " - " separator begins with
"Symphony" — e.g. "Symphony #3 in E-flat, Op. 55 - 1. Allegro con brio").

Whole-album semantics: when an album qualifies, ALL its tracks are emitted
(Beethoven symphonies typically share a disc with an overture, Egmont, the
Leonore, Choral Fantasy, etc.). That preserves the album-as-object shape so
the import step can attach `piece_refs` to every track.

Non-destructive. Reads iTunes XML and writes data/beethoven_symphonies_itunes_raw.json.
"""

import io
import json
import plistlib
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

LIBRARY_PATH = Path.home() / "Music" / "iTunes" / "iTunes Music Library.xml"
OUTPUT_PATH  = Path(__file__).parent.parent / "data" / "beethoven_symphonies_itunes_raw.json"


def is_cd_archive(location: str) -> bool:
    return "CD%20archive" in location or "CD archive" in location


def work_portion(name: str) -> str:
    """Return the work title from an iTunes Name field — the text before the
    first " - " separator. E.g. 'Symphony #3 in E-flat, Op. 55 - 1. Allegro'
    -> 'Symphony #3 in E-flat, Op. 55'."""
    if not name:
        return ""
    return name.split(" - ", 1)[0].strip()


def is_beethoven_symphony_track(t: dict) -> bool:
    composer = (t.get("Composer") or "")
    if "Beethoven" not in composer:
        return False
    work = work_portion(t.get("Name") or "")
    # Match "Symphony" at start, case-insensitive. Don't match "Symphonia",
    # "Symphonic Variations", etc. — require word boundary.
    return bool(re.match(r"^symphony\b", work, re.IGNORECASE))


def album_key(t: dict):
    album  = t.get("Album") or "(Unknown Album)"
    artist = t.get("Album Artist") or t.get("Artist") or ""
    return (album, artist)


def main():
    print(f"Reading {LIBRARY_PATH}")
    with LIBRARY_PATH.open("rb") as f:
        lib = plistlib.load(f)

    all_tracks = lib.get("Tracks", {})
    print(f"Total tracks in library: {len(all_tracks)}")

    # 1. All CD-archive tracks, grouped by album.
    archive_tracks = [
        t for t in all_tracks.values()
        if is_cd_archive(t.get("Location") or "")
    ]
    print(f"CD-archive tracks: {len(archive_tracks)}")

    by_album = defaultdict(list)
    for t in archive_tracks:
        by_album[album_key(t)].append(t)

    # 2. Albums with at least one Beethoven-symphony track.
    qualifying_albums = {}  # album_key -> list of tracks
    for key, tracks in by_album.items():
        if any(is_beethoven_symphony_track(t) for t in tracks):
            qualifying_albums[key] = tracks

    print(f"Albums containing Beethoven symphonies: {len(qualifying_albums)}")

    # 3. Shape output. Group each album's tracks by Disc Number, sort by track#.
    output = []
    for (album_title, album_artist), track_list in sorted(
        qualifying_albums.items(), key=lambda x: (x[0][1], x[0][0])
    ):
        track_list.sort(key=lambda t: (t.get("Disc Number") or 1, t.get("Track Number") or 0))
        discs = defaultdict(list)
        for t in track_list:
            discs[t.get("Disc Number") or 1].append({
                "track_number":   t.get("Track Number"),
                "name":           t.get("Name"),
                "composer":       t.get("Composer"),
                "artist":         t.get("Artist"),
                "album_artist":   t.get("Album Artist"),
                "year":           t.get("Year"),
                "total_time_ms":  t.get("Total Time"),
                "comments":       t.get("Comments"),
                "grouping":       t.get("Grouping"),
                "genre":          t.get("Genre"),
                "is_beethoven_symphony": is_beethoven_symphony_track(t),
                "work_title":     work_portion(t.get("Name") or ""),
            })

        disc_list = [
            {"disc_number": d, "tracks": discs[d]}
            for d in sorted(discs.keys())
        ]

        year = next((t.get("Year") for t in track_list if t.get("Year")), None)
        sym_track_count = sum(
            1 for t in track_list if is_beethoven_symphony_track(t)
        )

        output.append({
            "album_title":              album_title,
            "album_artist":             album_artist,
            "year":                     year,
            "disc_count":               len(disc_list),
            "track_count":              sum(len(d["tracks"]) for d in disc_list),
            "beethoven_symphony_track_count": sym_track_count,
            "discs":                    disc_list,
        })

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT_PATH.open("w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"\nWrote {OUTPUT_PATH}")
    print(f"\n--- Albums (sorted by artist) ---")
    for a in output:
        print(f"  {a['album_artist'] or '(no album artist)'}")
        print(f"    {a['album_title']}  "
              f"[{a['disc_count']} disc(s), {a['track_count']} track(s), "
              f"{a['beethoven_symphony_track_count']} symphony movements]")


if __name__ == "__main__":
    main()
