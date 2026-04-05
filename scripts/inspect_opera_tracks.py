"""
Show all opera tracks from iTunes, highlighting those with multiple
sub-movements on a single track (i.e., movement text containing " - NNx. ").
"""
import xml.etree.ElementTree as ET, re
from collections import defaultdict

FILE = r"C:\Users\james\Music\iTunes\iTunes Music Library.xml"

def parse_mozart_tracks(file):
    tree = ET.parse(file)
    td = None
    for i, c in enumerate(list(tree.getroot().find("dict"))):
        if c.tag == "key" and c.text == "Tracks":
            td = list(tree.getroot().find("dict"))[i + 1]; break
    tracks, tc, i = [], list(td), 0
    while i < len(tc) - 1:
        if tc[i].tag == "key":
            elem = tc[i + 1]
            if elem.tag == "dict":
                t, ec, j = {}, list(elem), 0
                while j < len(ec) - 1:
                    if ec[j].tag == "key":
                        k, v = ec[j].text, ec[j + 1]
                        if v.tag == "string":  t[k] = v.text
                        elif v.tag == "integer": t[k] = int(v.text)
                        j += 2
                    else: j += 1
                if "Mozart" in (t.get("Composer") or ""):
                    tracks.append(t)
        i += 2
    return tracks

OPERA_KVS = {
    "492": "Le nozze di Figaro",
    "527": "Don Giovanni",
    "588": "Così fan tutte",
    "620": "Die Zauberflöte",
    "621": "La clemenza di Tito",
    "366": "Idomeneo",
    "384": "Die Entführung",
    "196": "La finta giardiniera",
    "135": "Lucio Silla",
    "111": "Ascanio in Alba",
    "126": "Il sogno di Scipione",
    "208": "Il re pastore",
    "344": "Zaide",
    "486": "Der Schauspieldirektor",
    "87":  "Mitridate",
    "51":  "La finta semplice",
    "50":  "Bastien und Bastienne",
    "38":  "Apollo et Hyacinthus",
    "35":  "Die Schuldigkeit",
    "422": "L'oca del Cairo",
}

# Pattern for an internal sub-movement marker: " - NNx. " or " - NN. "
INTERNAL_SPLIT = re.compile(r'\s+-\s+\d+[a-z]\.\s+')

tracks = parse_mozart_tracks(FILE)

# Filter to opera tracks
opera_tracks = defaultdict(list)
for t in sorted(tracks, key=lambda x: (x.get("Album",""),
                                        x.get("Disc Number",0) or 0,
                                        x.get("Track Number",0) or 0)):
    name = t.get("Name", "")
    for kv, title in OPERA_KVS.items():
        if f", KV {kv}" in name or f", KV {kv.split('/')[0]}" in name:
            # Extract movement part
            idx = name.find(" - ")
            mvt = name[idx+3:] if idx != -1 else ""
            opera_tracks[title].append((name, mvt))
            break

for opera, tracks_list in sorted(opera_tracks.items()):
    multi = [(n, m) for n, m in tracks_list if INTERNAL_SPLIT.search(m)]
    single = [(n, m) for n, m in tracks_list if not INTERNAL_SPLIT.search(m)]
    print(f"\n{'='*80}")
    print(f"{opera}  ({len(tracks_list)} tracks, {len(multi)} multi-subpiece)")
    print(f"{'='*80}")
    print(f"  --- Single-subpiece tracks ({len(single)}) ---")
    for name, mvt in single[:3]:
        print(f"  {mvt[:90]}")
    if len(single) > 3: print(f"  ... ({len(single)-3} more)")
    if multi:
        print(f"  --- Multi-subpiece tracks ({len(multi)}) ---")
        for name, mvt in multi:
            print(f"  {mvt[:120]}")
