"""
Parse iTunes XML and extract all Mozart tracks, grouped by album.
"""
import xml.etree.ElementTree as ET
from collections import defaultdict

FILE = r"C:\Users\james\Music\iTunes\iTunes Music Library.xml"

def parse_dict(elem):
    """Parse a flat <dict> element into a Python dict."""
    result = {}
    children = list(elem)
    i = 0
    while i < len(children) - 1:
        if children[i].tag == "key":
            key = children[i].text
            val_elem = children[i + 1]
            if val_elem.tag == "string":
                result[key] = val_elem.text
            elif val_elem.tag == "integer":
                result[key] = int(val_elem.text)
            elif val_elem.tag in ("true", "false"):
                result[key] = (val_elem.tag == "true")
            i += 2
        else:
            i += 1
    return result

print("Parsing iTunes library...")
tree = ET.parse(FILE)
root = tree.getroot()

# Top-level structure: plist > dict > ... key "Tracks" > dict of dicts
top = root.find("dict")
children = list(top)
tracks_dict = None
for i, child in enumerate(children):
    if child.tag == "key" and child.text == "Tracks":
        tracks_dict = children[i + 1]
        break

mozart_by_album = defaultdict(list)
total = 0

track_children = list(tracks_dict)
i = 0
while i < len(track_children) - 1:
    if track_children[i].tag == "key":
        track_elem = track_children[i + 1]
        if track_elem.tag == "dict":
            t = parse_dict(track_elem)
            composer = t.get("Composer", "") or ""
            if "Mozart" in composer:
                album = t.get("Album", "Unknown Album")
                mozart_by_album[album].append(t)
                total += 1
    i += 2

print(f"Found {total} Mozart tracks across {len(mozart_by_album)} albums.\n")

for album in sorted(mozart_by_album.keys()):
    tracks = sorted(mozart_by_album[album],
                    key=lambda t: (t.get("Disc Number", 0) or 0,
                                   t.get("Track Number", 0) or 0))
    print(f"{'='*80}")
    print(f"ALBUM: {album}  ({len(tracks)} tracks)")
    print(f"{'='*80}")
    for t in tracks:
        disc = t.get("Disc Number", "")
        trk  = t.get("Track Number", "")
        name = t.get("Name", "")
        loc  = f"D{disc}/T{trk}" if disc else f"T{trk}"
        dur_ms = t.get("Total Time", 0) or 0
        dur = f"{dur_ms//60000}:{(dur_ms//1000)%60:02d}"
        composer = t.get("Composer", "")
        print(f"  [{loc}] {name}  ({dur})  [{composer}]")
    print()
