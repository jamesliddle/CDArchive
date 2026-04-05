"""
Auto-generate and insert Mozart entries into Classical Canon pieces.json.

Handles all observed iTunes track-title formats:
  "Form #N in Key, KV NNN - N. Tempo"
  "Form #N in Key, KV NNN "Nickname" - N. Tempo"
  "Title, KV NNN (compl.) - N. Tempo"        — fragment completion
  "Title, KV (412+514)/386b - N. Tempo"      — complex KV number
  "Title, KV App. 91/516c - N. Tempo"        — appendix work
  "Title, KV NNN"                             — single movement
"""
import xml.etree.ElementTree as ET
import re, json
from collections import OrderedDict

ITUNES = r"C:\Users\james\Music\iTunes\iTunes Music Library.xml"
DATA   = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

# ── Key helpers ─────────────────────────────────────────────────────────────
SHARP_FLAT_MAP = {
    "B-flat": "B♭", "E-flat": "E♭", "A-flat": "A♭",
    "D-flat": "D♭", "G-flat": "G♭", "C-flat": "C♭",
    "F-sharp": "F♯", "C-sharp": "C♯",
}

def normalise_key(raw): return SHARP_FLAT_MAP.get(raw, raw)
def key_is_minor(s):    return len(s) <= 2 and s == s.lower() and s.isalpha()

# ── iTunes parser ────────────────────────────────────────────────────────────
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
                        k, v = ec[j].text, ec[j+1]
                        if v.tag == "string":  t[k] = v.text
                        elif v.tag == "integer": t[k] = int(v.text)
                        j += 2
                    else: j += 1
                if "Mozart" in (t.get("Composer") or ""):
                    tracks.append(t)
        i += 2
    return tracks

# ── Robust title parser ──────────────────────────────────────────────────────
PAREN_SUFFIX = re.compile(r'\s*\([^)]+\)\s*$')  # strip trailing (compl.), (arr.), etc.
MVT_NUM_RE   = re.compile(r'^(\d+)[a-z]?\.\s+(.+)$')

def split_track_name(name):
    """
    Split 'Work, KV NNN "Nick" - N. Mvt' into
    (work_title, kv_raw, nickname, mvt_text).
    Returns None if no ', KV ' found.
    """
    kv_sep = ', KV '
    idx = name.find(kv_sep)
    if idx == -1:
        return None
    work_title = name[:idx].strip()
    remainder  = name[idx + len(kv_sep):]  # everything after ", KV "

    # Pull off optional movement part after the first bare " - "
    # (not inside parentheses or quotes)
    mvt_text = ""
    kv_plus = remainder
    m = re.search(r'\s+-\s+', remainder)
    if m:
        kv_plus   = remainder[:m.start()]
        mvt_text  = remainder[m.end():]

    # Pull off optional quoted nickname from kv_plus
    nickname = None
    nick_m = re.search(r'\s+"([^"]+)"\s*$', kv_plus)
    if nick_m:
        nickname = nick_m.group(1)
        kv_plus  = kv_plus[:nick_m.start()]

    # Strip trailing parenthetical suffixes from KV number
    kv_raw = PAREN_SUFFIX.sub("", kv_plus).strip()

    return work_title, kv_raw, nickname, mvt_text

# ── Work-title parsing ───────────────────────────────────────────────────────
FORM_NUM_KEY_RE = re.compile(r'^(?P<form>.+?)\s+#(?P<num>\d+)\s+in\s+(?P<key>\S+(?:-\S+)?)$')
FORM_KEY_RE     = re.compile(r'^(?P<form>.+?)\s+in\s+(?P<key>\S+(?:-\S+)?)$')

def parse_work_title(work_title, inline_nickname=None):
    nickname = inline_nickname
    nick_m = re.search(r'"([^"]+)"', work_title)
    if nick_m:
        if not nickname: nickname = nick_m.group(1)
        work_title = work_title[:nick_m.start()].strip()

    m = FORM_NUM_KEY_RE.match(work_title)
    if m:
        raw = m.group("key")
        return m.group("form").strip(), int(m.group("num")), \
               normalise_key(raw), ("minor" if key_is_minor(raw) else "major"), None, nickname

    m = FORM_KEY_RE.match(work_title)
    if m:
        raw = m.group("key")
        return m.group("form").strip(), None, \
               normalise_key(raw), ("minor" if key_is_minor(raw) else "major"), None, nickname

    return None, None, None, None, work_title.strip(), nickname

# ── Catalog builder ──────────────────────────────────────────────────────────
def make_catalog(kv_raw):
    """Build catalog_info from potentially complex KV string.

    Preserves the full dual-number form (e.g. "392/340a", "191/186e") so the
    display shows "KV 392/340a" as Köchel intended.  Parentheses are stripped
    from unusual compound numbers like "(412+514)/386b" → "412+514/386b".
    kv_sort() still extracts just the leading digits for ordering.
    """
    kv = kv_raw.strip()

    # KV App. or KV Anh.
    if re.match(r'(?:App|Anh)\.?', kv, re.I):
        m = re.match(r'(?:App|Anh)\.?\s*([\w./]+)', kv, re.I)
        num = m.group(1) if m else kv
        return [{"catalog": "KV Anh.", "catalog_number": num.strip(".")}]

    # Normalise parentheses: "(412+514)/386b" → "412+514/386b"
    kv = re.sub(r'[()]', '', kv).strip()

    return [{"catalog": "KV", "catalog_number": kv}]

def kv_sort(kv_raw):
    m = re.search(r'(\d+)', kv_raw or "")
    return int(m.group(1)) if m else 9999

# ── Instrumentation category ─────────────────────────────────────────────────
OPERA_TITLES = {
    "Le nozze di Figaro", "Don Giovanni", "Così fan tutte", "Die Zauberflöte",
    "La clemenza di Tito", "Idomeneo, re di Creta", "Idomeneo",
    "Die Entführung aus dem Serail",
    "La finta giardiniera", "Die Gärtnerin aus Liebe", "La finta semplice",
    "Bastien und Bastienne", "Apollo et Hyacinthus",
    "Mitridate, re di Ponto", "Ascanio in Alba", "Il sogno di Scipione",
    "Lucio Silla", "Il re pastore", "Zaide", "Der Schauspieldirektor",
    "L'oca del Cairo", "Lo sposo deluso",
    "Die Schuldigkeit des ersten Gebots",
    "Thamos, König in Ägypten",
}
CHORAL_KW = {"Mass", "Missa", "Requiem", "Kyrie", "Litaniae", "Vesperae",
             "Ave verum", "Exsultate", "Grabmusik", "Davidde"}

def is_opera(t):
    return any(op.lower() in t.lower() for op in OPERA_TITLES)
def is_choral(t, f=""):
    return any(k.lower() in t.lower() or k.lower() in (f or "").lower() for k in CHORAL_KW)

def infer_category(form, work_title):
    f, w = (form or "").lower(), work_title.lower()
    if is_opera(work_title):                                 return "Opera"
    if is_choral(work_title, form):                          return "Choral"
    if "concerto" in f:                                      return "Concerto"
    if "sinfonia" in f and "concertante" in f:               return "Concerto"
    if "symphony" in f:                                      return "Symphonic"
    if "serenade" in f or "cassation" in f:                  return "Orchestral"
    if any(x in f for x in ("string quartet","string quintet","string trio",
                             "piano trio","violin sonata","quintet","quartet",
                             "trio","duo","divertiment","clarinet quintet",
                             "horn quintet","oboe quartet")):
        return "Chamber"
    if any(x in f for x in ("piano sonata","sonata for piano","fantasia",
                             "fantasy","variation","rondo")):
        return "Keyboard"
    if any(x in f for x in ("minuet","dance","contredanse","march","german dance",
                             "anglaise","contradanse","menuet")):
        return "Orchestral"
    if any(x in f for x in ("lied","song","notturno","villanelle","canzonetta")):
        return "Song"
    if "church sonata" in f or "organ sonata" in f:          return "Organ"
    if "canon" in f:                                         return "Choral"
    return "Orchestral"

# ── Movement builder ─────────────────────────────────────────────────────────
FORM_TEMPO_RE = re.compile(
    r'^(Menuetto|Menuet|Scherzo|Trio|Finale|Rondeau|Rondo|Thema|'
    r'Adagio|Andante|Allegro|Presto|Largo|Vivace|Minuet|March|Marcia|'
    r'Romanze|Romance|Fuga|Fugue|Aria|Cavatina|Sinfonia|Overture|'
    r'Introduction|Intrada|Contredanse|Theme|Air|Polonaise|Minuets)\.'
    r'\s+(.+)', re.IGNORECASE
)

def build_movement(num, mvt_text, is_opera_work):
    entry = {"number": num}
    if is_opera_work:
        entry["title"] = mvt_text
        return entry
    m = FORM_TEMPO_RE.match(mvt_text)
    if m:
        entry["form"] = m.group(1)
        tempo_text = m.group(2).strip()
    else:
        tempo_text = mvt_text
    if tempo_text:
        parts = [p.strip() for p in re.split(r'\s+-\s+', tempo_text) if p.strip()]
        entry["tempos"] = [{"number": i+1, "tempo_description": p} for i, p in enumerate(parts)]
    return entry

# ── Build all pieces ─────────────────────────────────────────────────────────
def build_pieces(tracks):
    works = OrderedDict()

    for t in sorted(tracks, key=lambda x: (
            x.get("Album",""), x.get("Disc Number",0) or 0, x.get("Track Number",0) or 0)):
        name = (t.get("Name") or "").strip()
        parsed = split_track_name(name)
        if not parsed:
            continue
        work_title, kv_raw, nickname, mvt_text = parsed
        if not kv_raw:
            continue

        key = (work_title, kv_raw)
        if key not in works:
            works[key] = {"work_title": work_title, "kv_raw": kv_raw,
                          "nickname": nickname, "movements": OrderedDict()}
        elif nickname and not works[key]["nickname"]:
            works[key]["nickname"] = nickname

        if mvt_text:
            mm = MVT_NUM_RE.match(mvt_text)
            if mm:
                num, text = int(mm.group(1)), mm.group(2).strip()
                if num not in works[key]["movements"]:
                    works[key]["movements"][num] = text

    pieces = []
    for (work_title, kv_raw), info in sorted(works.items(),
                                              key=lambda x: kv_sort(x[0][1])):
        form, number, key_tonality, key_mode, title, nickname = \
            parse_work_title(work_title, info.get("nickname"))

        piece = {"composer": "Mozart, Wolfgang Amadeus"}
        if form:          piece["form"] = form
        if number:        piece["number"] = number
        if title:         piece["title"] = title
        if nickname:      piece["nickname"] = nickname
        if key_tonality:  piece["key_tonality"] = key_tonality
        if key_mode:      piece["key_mode"] = key_mode
        piece["catalog_info"] = make_catalog(kv_raw)
        piece["instrumentation_category"] = infer_category(form, work_title)

        opera_work = is_opera(work_title)
        movs = info["movements"]
        if movs:
            piece["subpieces"] = [
                build_movement(num, text, opera_work)
                for num, text in sorted(movs.items())
            ]
        pieces.append(piece)

    return pieces

# ── Run ──────────────────────────────────────────────────────────────────────
print("Parsing iTunes library...")
tracks = parse_mozart_tracks(ITUNES)
print(f"Found {len(tracks)} Mozart tracks.")

print("Building piece entries...")
pieces_to_insert = build_pieces(tracks)
print(f"Generated {len(pieces_to_insert)} Mozart pieces.")

with open(DATA, encoding="utf-8") as f:
    all_pieces = json.load(f)

before = len(all_pieces)
all_pieces = [p for p in all_pieces if p.get("composer") != "Mozart, Wolfgang Amadeus"]
if before - len(all_pieces):
    print(f"Removed {before - len(all_pieces)} existing Mozart entries.")

all_pieces.extend(pieces_to_insert)
print(f"Total pieces now: {len(all_pieces)}. Saving...")
with open(DATA, "w", encoding="utf-8") as f:
    json.dump(all_pieces, f, ensure_ascii=False, indent=2)
    f.write("\n")
print("Done.")
