"""
Extract all Mahler tracks from the iTunes library, grouped by album.

Reads iTunes Music Library.xml (Apple plist format) and dumps every track
whose Composer field matches Mahler (filtered to tracks in the CD archive),
grouped by album. The output is a JSON file meant as raw material for
further processing, not a finished CanonAlbum/CanonPiece file.
"""

import plistlib
import json
import re
import sys
import urllib.parse
from pathlib import Path
from collections import defaultdict

LIBRARY_PATH = Path.home() / "Music" / "iTunes" / "iTunes Music Library.xml"
OUTPUT_PATH = Path(__file__).parent.parent / "data" / "mahler_itunes_raw.json"

def main():
    print(f"Reading {LIBRARY_PATH}")
    with LIBRARY_PATH.open("rb") as f:
        lib = plistlib.load(f)

    tracks = lib.get("Tracks", {})
    print(f"Total tracks in library: {len(tracks)}")

    # Filter to Mahler tracks in the CD archive
    mahler_tracks = []
    for tid, t in tracks.items():
        composer = t.get("Composer", "") or ""
        location = t.get("Location", "") or ""
        if "Mahler" not in composer:
            continue
        if "CD%20archive" not in location and "CD archive" not in location:
            continue
        mahler_tracks.append(t)

    print(f"Mahler tracks in CD archive: {len(mahler_tracks)}")

    # Group by (Album, Album Artist, Disc Number)
    # The album identity isn't perfect in iTunes XML; we use Album + Album Artist
    # (or Artist if no Album Artist) as a reasonable key.
    albums = defaultdict(list)
    for t in mahler_tracks:
        album = t.get("Album", "(Unknown Album)") or "(Unknown Album)"
        album_artist = t.get("Album Artist") or t.get("Artist") or ""
        key = (album, album_artist)
        albums[key].append(t)

    print(f"Distinct albums: {len(albums)}")

    # Shape for downstream processing
    output = []
    for (album_title, album_artist), track_list in sorted(albums.items(),
                                                          key=lambda x: (x[0][1], x[0][0])):
        # Sort tracks by disc then track number
        track_list.sort(key=lambda t: (t.get("Disc Number", 1), t.get("Track Number", 0)))
        # Group into discs
        discs = defaultdict(list)
        for t in track_list:
            discs[t.get("Disc Number", 1)].append({
                "track_number": t.get("Track Number"),
                "name": t.get("Name"),
                "composer": t.get("Composer"),
                "artist": t.get("Artist"),
                "album_artist": t.get("Album Artist"),
                "year": t.get("Year"),
                "total_time_ms": t.get("Total Time"),
                "comments": t.get("Comments"),
                "grouping": t.get("Grouping"),
                "genre": t.get("Genre"),
            })
        disc_list = []
        for disc_num in sorted(discs.keys()):
            disc_list.append({
                "disc_number": disc_num,
                "tracks": discs[disc_num],
            })

        # Extract a sample year from any track that has one
        year = None
        for t in track_list:
            if t.get("Year"):
                year = t["Year"]
                break

        output.append({
            "album_title": album_title,
            "album_artist": album_artist,
            "disc_count": len(disc_list),
            "track_count": sum(len(d["tracks"]) for d in disc_list),
            "year": year,
            "discs": disc_list,
        })

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT_PATH.open("w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"Wrote {OUTPUT_PATH}")
    print(f"Summary: {len(output)} albums, {sum(a['track_count'] for a in output)} tracks")

    # Also print a short summary to stdout
    print("\n--- Albums ---")
    for a in output:
        print(f"  {a['album_artist']}: {a['album_title']} [{a['disc_count']} disc(s), {a['track_count']} track(s)]")

if __name__ == "__main__":
    main()
