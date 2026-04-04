"""
List all Mozart albums with track counts, and show a sample of works per album.
"""
import xml.etree.ElementTree as ET
from collections import defaultdict
import re

FILE = r"C:\Users\james\Music\iTunes\iTunes Music Library.xml"

def parse_tracks(file):
    tree = ET.parse(file)
    root = tree.getroot()
    top = root.find("dict")
    children = list(top)
    tracks_dict = None
    for i, child in enumerate(children):
        if child.tag == "key" and child.text == "Tracks":
            tracks_dict = children[i + 1]
            break
    tracks = []
    tc = list(tracks_dict)
    i = 0
    while i < len(tc) - 1:
        if tc[i].tag == "key":
            elem = tc[i + 1]
            if elem.tag == "dict":
                t = {}
                ec = list(elem)
                j = 0
                while j < len(ec) - 1:
                    if ec[j].tag == "key":
                        key = ec[j].text
                        val = ec[j+1]
                        if val.tag == "string": t[key] = val.text
                        elif val.tag == "integer": t[key] = int(val.text)
                        j += 2
                    else:
                        j += 1
                if "Mozart" in (t.get("Composer") or ""):
                    tracks.append(t)
        i += 2
    return tracks

def strip_movement(name):
    m = re.match(r'^(.+?) - \d+\.? .+$', name)
    if m:
        return m.group(1).strip()
    m = re.match(r'^(.+?) - (?:Act|Scene|No\.|Aria|Recit|Duet|Trio|Quartet|Chorus|Overture|Finale|Entr)', name)
    if m:
        return m.group(1).strip()
    return name

print("Parsing iTunes library...")
tracks = parse_tracks(FILE)

# Group by album
albums = defaultdict(list)
for t in tracks:
    albums[t.get("Album", "Unknown")].append(t)

print(f"\n{len(albums)} Mozart albums, {len(tracks)} tracks total\n")
print(f"{'Album':<60} {'Tracks':>6}  Sample works")
print("-" * 100)
for album in sorted(albums.keys()):
    ts = albums[album]
    # Get unique works in this album
    works_seen = []
    for t in sorted(ts, key=lambda x: (x.get("Disc Number",0) or 0, x.get("Track Number",0) or 0)):
        w = strip_movement(t.get("Name",""))
        if w not in works_seen:
            works_seen.append(w)
    sample = "; ".join(works_seen[:3])
    if len(works_seen) > 3:
        sample += f" ... (+{len(works_seen)-3} more)"
    print(f"{album:<60} {len(ts):>6}  {sample}")
