"""
Extract unique Mozart works from iTunes, grouped by work title (stripping movement suffix).
Prints a clean list suitable for building the insert script.
"""
import xml.etree.ElementTree as ET
import re
from collections import defaultdict, OrderedDict

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
    """Strip movement suffix: everything after ' - N.' or ' - [movement text]'."""
    # Pattern: " - digit(s). " or " - [text]." at end indicating a movement
    m = re.match(r'^(.+?) - \d+\.? .+$', name)
    if m:
        return m.group(1).strip()
    # Also handle e.g. "Work - Act I. Scene..."
    m = re.match(r'^(.+?) - (?:Act|Scene|No\.|Aria|Recit|Duet|Trio|Quartet|Chorus|Overture|Finale|Entr\'acte|Ballet)', name)
    if m:
        return m.group(1).strip()
    return name

print("Parsing iTunes library...")
tracks = parse_tracks(FILE)
print(f"Found {len(tracks)} Mozart tracks.\n")

# Group tracks by work title (stripped of movement)
works = OrderedDict()  # work_title -> list of track names (movements)
for t in sorted(tracks, key=lambda x: (
        x.get("Album", ""),
        x.get("Disc Number", 0) or 0,
        x.get("Track Number", 0) or 0)):
    name = t.get("Name", "")
    work = strip_movement(name)
    if work not in works:
        works[work] = []
    # Only add the movement part (after " - ")
    if " - " in name and name != work:
        mvt_part = name[name.index(" - ") + 3:]
        if mvt_part not in works[work]:
            works[work].append(mvt_part)

print(f"Unique works: {len(works)}\n")
for work, mvts in sorted(works.items()):
    print(f"{work}")
    for m in mvts:
        print(f"   {m}")
    print()
