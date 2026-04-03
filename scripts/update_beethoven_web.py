#!/usr/bin/env python3
"""
Update remaining abbreviated Beethoven Op. entries using web-sourced data.
"""
import json, sys, os, re
sys.stdout.reconfigure(encoding='utf-8')

CANON_PATH = "data/Classical Canon pieces.json"
CHANGELOG_PATH = "data/canon-pieces-changelog-beethoven-web.txt"

changelog = []
def log(msg):
    changelog.append(msg)
    print(msg)

def T(n, desc):
    """Build a tempo entry."""
    return {"number": n, "tempo_description": desc}

def M(num, tempos, form=None):
    """Build a movement/subpiece."""
    m = {"number": num}
    if form:
        m["form"] = form
    if tempos is None:
        pass
    elif isinstance(tempos, str):
        m["tempos"] = [T(1, tempos)]
    elif isinstance(tempos, list):
        m["tempos"] = [T(i+1, t) for i, t in enumerate(tempos)]
    return m

def CI(cat, num, sub=None):
    """Build catalog_info."""
    c = {"catalog": cat, "catalog_number": num}
    if sub is not None:
        del c["catalog_number"]
        c["catalog_subnumber"] = sub
    return [c]

# =========================================================================
# Define all 36 entries from web research
# =========================================================================
ENTRIES = {}

# --- Op. 31: Three Piano Sonatas (set) ---
ENTRIES["31"] = {
    "catalog_info": CI("Op.", "31"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "publication_year": 1802,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "piano sonata",
            "key_mode": "major", "key_tonality": "G", "number": 16,
            "subpieces": [
                M(1, "Allegro vivace"),
                M(2, "Adagio grazioso"),
                M(3, "Allegretto", form="Rondo"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "piano sonata", "nickname": "Tempest",
            "key_mode": "minor", "key_tonality": "D", "number": 17,
            "subpieces": [
                M(1, ["Largo", "Allegro"]),
                M(2, "Adagio"),
                M(3, "Allegretto"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "3"}],
            "form": "piano sonata", "nickname": "The Hunt",
            "key_mode": "major", "key_tonality": "E-flat", "number": 18,
            "subpieces": [
                M(1, "Allegro"),
                M(2, "Allegretto vivace", form="Scherzo"),
                M(3, "Moderato e grazioso", form="Menuetto"),
                M(4, "Presto con fuoco"),
            ]
        }
    ]
}

# --- Op. 38: Piano Trio (arrangement of Septet Op. 20) ---
ENTRIES["38"] = {
    "catalog_info": CI("Op.", "38"),
    "composer": "Beethoven, Ludwig van",
    "form": "piano trio",
    "title": "Piano Trio (arrangement of Septet, Op. 20)",
    "instrumentation": ["clarinet", "cello", "piano"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1803,
    "subpieces": [
        M(1, ["Adagio", "Allegro con brio"]),
        M(2, "Adagio cantabile"),
        M(3, "Tempo di menuetto"),
        M(4, "Andante con variazioni"),
        M(5, "Allegro molto e vivace", form="Scherzo"),
        M(6, ["Andante con moto", "Presto"]),
    ]
}

# --- Op. 39: Two Preludes ---
ENTRIES["39"] = {
    "catalog_info": CI("Op.", "39"),
    "composer": "Beethoven, Ludwig van",
    "form": "prelude",
    "title": "Two Preludes through All Twelve Major Keys",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "publication_year": 1803,
}

# --- Op. 42: Notturno ---
ENTRIES["42"] = {
    "catalog_info": CI("Op.", "42"),
    "composer": "Beethoven, Ludwig van",
    "form": "notturno",
    "title": "Notturno (arrangement of Serenade, Op. 8)",
    "instrumentation": ["viola", "piano"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "D",
    "publication_year": 1803,
}

# --- Op. 45: Three Marches ---
ENTRIES["45"] = {
    "catalog_info": CI("Op.", "45"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "title": "Three Marches for Piano, Four Hands",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "publication_year": 1803,
    "subpieces": [
        {"catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
         "form": "march", "key_mode": "major", "key_tonality": "C", "number": 1},
        {"catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
         "form": "march", "key_mode": "major", "key_tonality": "E-flat", "number": 2},
        {"catalog_info": [{"catalog": "Op.", "catalog_subnumber": "3"}],
         "form": "march", "key_mode": "major", "key_tonality": "D", "number": 3},
    ]
}

# --- Op. 49: Two Piano Sonatas (set) ---
ENTRIES["49"] = {
    "catalog_info": CI("Op.", "49"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "publication_year": 1805,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "piano sonata",
            "key_mode": "minor", "key_tonality": "G", "number": 19,
            "subpieces": [
                M(1, "Andante"),
                M(2, "Allegro", form="Rondo"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "piano sonata",
            "key_mode": "major", "key_tonality": "G", "number": 20,
            "subpieces": [
                M(1, "Allegro, ma non troppo"),
                M(2, "Tempo di Menuetto"),
            ]
        }
    ]
}

# --- Op. 51: Two Rondos (set) ---
ENTRIES["51"] = {
    "catalog_info": CI("Op.", "51"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "publication_year": 1797,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "rondo",
            "key_mode": "major", "key_tonality": "C", "number": 1,
            "tempos": [T(1, "Moderato e grazioso")]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "rondo",
            "key_mode": "major", "key_tonality": "G", "number": 2,
            "tempos": [T(1, "Andante cantabile e grazioso")]
        }
    ]
}

# --- Op. 59: Three String Quartets "Rasumovsky" (set) ---
ENTRIES["59"] = {
    "catalog_info": CI("Op.", "59"),
    "composer": "Beethoven, Ludwig van",
    "form": "set", "nickname": "Rasumovsky",
    "instrumentation": ["violin", "violin", "viola", "cello"],
    "instrumentation_category": "chamber",
    "publication_year": 1808,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "string quartet",
            "key_mode": "major", "key_tonality": "F", "number": 7,
            "subpieces": [
                M(1, "Allegro"),
                M(2, "Allegretto vivace e sempre scherzando"),
                M(3, "Adagio molto e mesto"),
                M(4, "Allegro"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "string quartet",
            "key_mode": "minor", "key_tonality": "E", "number": 8,
            "subpieces": [
                M(1, "Allegro"),
                M(2, "Molto adagio"),
                M(3, "Allegretto"),
                M(4, "Presto", form="Finale"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "3"}],
            "form": "string quartet",
            "key_mode": "major", "key_tonality": "C", "number": 9,
            "subpieces": [
                M(1, ["Andante con moto", "Allegro vivace"]),
                M(2, "Andante con moto quasi allegretto"),
                M(3, "Grazioso", form="Menuetto"),
                M(4, "Allegro molto"),
            ]
        }
    ]
}

# --- Op. 61a: Piano Concerto (transcription of Violin Concerto) ---
ENTRIES["61a"] = {
    "catalog_info": CI("Op.", "61a"),
    "composer": "Beethoven, Ludwig van",
    "form": "piano concerto",
    "title": "Piano Concerto (transcription of Violin Concerto, Op. 61)",
    "instrumentation": ["piano", "orchestra"],
    "instrumentation_category": "concerto",
    "key_mode": "major", "key_tonality": "D",
    "subpieces": [
        M(1, "Allegro, ma non troppo"),
        M(2, "Larghetto"),
        M(3, "Allegro", form="Rondo"),
    ]
}

# --- Op. 63: Arrangement for Piano Trio (doubtful) ---
ENTRIES["63"] = {
    "catalog_info": CI("Op.", "63"),
    "composer": "Beethoven, Ludwig van",
    "form": "piano trio",
    "title": "Piano Trio (arrangement of String Quintet, Op. 4)",
    "instrumentation": ["piano", "violin", "cello"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1806,
}

# --- Op. 64: Arrangement for Piano and Cello (spurious) ---
ENTRIES["64"] = {
    "catalog_info": CI("Op.", "64"),
    "composer": "Beethoven, Ludwig van",
    "form": "sonata",
    "title": "Sonata for Piano and Cello (arrangement of String Trio, Op. 3)",
    "instrumentation": ["cello", "piano"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1807,
}

# --- Op. 65: Ah! perfido ---
ENTRIES["65"] = {
    "catalog_info": CI("Op.", "65"),
    "composer": "Beethoven, Ludwig van",
    "form": "aria",
    "title": "Ah! perfido",
    "instrumentation": ["soprano", "orchestra"],
    "instrumentation_category": "vocal",
    "publication_year": 1805,
}

# --- Op. 70: Two Piano Trios (set) ---
ENTRIES["70"] = {
    "catalog_info": CI("Op.", "70"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "instrumentation": ["piano", "violin", "cello"],
    "instrumentation_category": "chamber",
    "publication_year": 1809,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "piano trio", "nickname": "Ghost",
            "key_mode": "major", "key_tonality": "D", "number": 5,
            "subpieces": [
                M(1, "Allegro vivace e con brio"),
                M(2, "Largo assai ed espressivo"),
                M(3, "Presto"),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "piano trio",
            "key_mode": "major", "key_tonality": "E-flat", "number": 6,
            "subpieces": [
                M(1, ["Poco sostenuto", "Allegro ma non troppo"]),
                M(2, "Allegretto"),
                M(3, "Allegretto ma non troppo"),
                M(4, "Allegro", form="Finale"),
            ]
        }
    ]
}

# --- Op. 71: Wind Sextet ---
ENTRIES["71"] = {
    "catalog_info": CI("Op.", "71"),
    "composer": "Beethoven, Ludwig van",
    "form": "sextet",
    "instrumentation": ["clarinet", "clarinet", "horn", "horn", "bassoon", "bassoon"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1810,
    "subpieces": [
        M(1, ["Adagio", "Allegro"]),
        M(2, "Adagio"),
        M(3, "Quasi allegretto", form="Menuetto"),
        M(4, None, form="Rondo"),
    ]
}

# --- Op. 72a: Leonore (1805 version) ---
ENTRIES["72a"] = {
    "catalog_info": CI("Op.", "72a"),
    "composer": "Beethoven, Ludwig van",
    "form": "opera",
    "title": "Leonore (with Leonore Overture No. 2)",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "vocal",
    "publication_year": 1805,
}

# --- Op. 72b: Leonore (1806 version) ---
ENTRIES["72b"] = {
    "catalog_info": CI("Op.", "72b"),
    "composer": "Beethoven, Ludwig van",
    "form": "opera",
    "title": "Leonore (with Leonore Overture No. 3)",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "vocal",
    "publication_year": 1806,
}

# --- Op. 76: Six Variations ---
ENTRIES["76"] = {
    "catalog_info": CI("Op.", "76"),
    "composer": "Beethoven, Ludwig van",
    "form": "variations",
    "title": "Six Variations on an Original Theme",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "key_mode": "major", "key_tonality": "D",
    "publication_year": 1810,
}

# --- Op. 77: Fantasia ---
ENTRIES["77"] = {
    "catalog_info": CI("Op.", "77"),
    "composer": "Beethoven, Ludwig van",
    "form": "fantasia",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "key_mode": "minor", "key_tonality": "G",
    "publication_year": 1810,
}

# --- Op. 81a: Piano Sonata No. 26 "Les Adieux" ---
ENTRIES["81a"] = {
    "catalog_info": CI("Op.", "81a"),
    "composer": "Beethoven, Ludwig van",
    "form": "piano sonata",
    "nickname": "Les Adieux",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "key_mode": "major", "key_tonality": "E-flat",
    "number": 26,
    "publication_year": 1811,
    "subpieces": [
        M(1, ["Adagio", "Allegro"]),
        M(2, "Andante espressivo"),
        M(3, "Vivacissimamente"),
    ]
}

# --- Op. 81b: Sextet for 2 Horns and Strings ---
ENTRIES["81b"] = {
    "catalog_info": CI("Op.", "81b"),
    "composer": "Beethoven, Ludwig van",
    "form": "sextet",
    "instrumentation": ["horn", "horn", "violin", "violin", "viola", "cello"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1810,
}

# --- Op. 85: Christus am Olberge ---
ENTRIES["85"] = {
    "catalog_info": CI("Op.", "85"),
    "composer": "Beethoven, Ludwig van",
    "form": "oratorio",
    "title": "Christus am \u00d6lberge",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "choral",
    "publication_year": 1811,
}

# --- Op. 86: Mass in C major ---
ENTRIES["86"] = {
    "catalog_info": CI("Op.", "86"),
    "composer": "Beethoven, Ludwig van",
    "form": "mass",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "choral",
    "key_mode": "major", "key_tonality": "C",
    "publication_year": 1812,
    "subpieces": [
        {"number": 1, "title": "Kyrie"},
        {"number": 2, "title": "Gloria"},
        {"number": 3, "title": "Credo"},
        {"number": 4, "title": "Sanctus"},
        {"number": 5, "title": "Agnus Dei"},
    ]
}

# --- Op. 87: Trio for 2 Oboes and English Horn ---
ENTRIES["87"] = {
    "catalog_info": CI("Op.", "87"),
    "composer": "Beethoven, Ludwig van",
    "form": "trio",
    "instrumentation": ["oboe", "oboe", "english horn"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "C",
    "publication_year": 1806,
    "subpieces": [
        M(1, "Allegro"),
        M(2, "Adagio cantabile"),
        M(3, "Allegro molto", form="Scherzo"),
        M(4, "Presto"),
    ]
}

# --- Op. 89: Polonaise ---
ENTRIES["89"] = {
    "catalog_info": CI("Op.", "89"),
    "composer": "Beethoven, Ludwig van",
    "form": "polonaise",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "key_mode": "major", "key_tonality": "C",
    "publication_year": 1815,
}

# --- Op. 91: Wellington's Victory ---
ENTRIES["91"] = {
    "catalog_info": CI("Op.", "91"),
    "composer": "Beethoven, Ludwig van",
    "form": "symphony",
    "title": "Wellington's Victory",
    "instrumentation": ["orchestra"],
    "instrumentation_category": "orchestra",
    "publication_year": 1816,
    "subpieces": [
        {"number": 1, "title": "Schlacht"},
        {"number": 2, "title": "Sieges Sinfonie"},
    ]
}

# --- Op. 102: Two Cello Sonatas (set) ---
ENTRIES["102"] = {
    "catalog_info": CI("Op.", "102"),
    "composer": "Beethoven, Ludwig van",
    "form": "set",
    "instrumentation": ["cello", "piano"],
    "instrumentation_category": "chamber",
    "publication_year": 1817,
    "subpieces": [
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "1"}],
            "form": "cello sonata",
            "key_mode": "major", "key_tonality": "C", "number": 4,
            "subpieces": [
                M(1, ["Andante", "Allegro vivace"]),
                M(2, ["Adagio", "Allegro vivace"]),
            ]
        },
        {
            "catalog_info": [{"catalog": "Op.", "catalog_subnumber": "2"}],
            "form": "cello sonata",
            "key_mode": "major", "key_tonality": "D", "number": 5,
            "subpieces": [
                M(1, "Allegro con brio"),
                M(2, "Adagio con molto sentimento d'affetto"),
                M(3, ["Allegro", "Allegro fugato"]),
            ]
        }
    ]
}

# --- Op. 103: Wind Octet ---
ENTRIES["103"] = {
    "catalog_info": CI("Op.", "103"),
    "composer": "Beethoven, Ludwig van",
    "form": "octet",
    "instrumentation": ["oboe", "oboe", "clarinet", "clarinet", "horn", "horn", "bassoon", "bassoon"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "E-flat",
    "publication_year": 1834,
    "subpieces": [
        M(1, "Allegro"),
        M(2, "Andante"),
        M(3, None, form="Menuetto"),
        M(4, "Presto"),
    ]
}

# --- Op. 104: String Quintet ---
ENTRIES["104"] = {
    "catalog_info": CI("Op.", "104"),
    "composer": "Beethoven, Ludwig van",
    "form": "string quintet",
    "title": "String Quintet (arrangement of Piano Trio No. 3, Op. 1 #3)",
    "instrumentation": ["violin", "violin", "viola", "viola", "cello"],
    "instrumentation_category": "chamber",
    "key_mode": "minor", "key_tonality": "C",
    "publication_year": 1819,
    "subpieces": [
        M(1, "Allegro con brio"),
        M(2, "Andante cantabile con variazioni"),
        M(3, "Quasi allegro", form="Minuetto"),
        M(4, "Prestissimo", form="Finale"),
    ]
}

# --- Op. 108: Twenty-Five Scottish Songs ---
ENTRIES["108"] = {
    "catalog_info": CI("Op.", "108"),
    "composer": "Beethoven, Ludwig van",
    "form": "song",
    "title": "Twenty-Five Scottish Songs",
    "instrumentation": ["voice", "piano", "violin", "cello"],
    "instrumentation_category": "vocal",
    "publication_year": 1818,
}

# --- Op. 114: March and Chorus ---
ENTRIES["114"] = {
    "catalog_info": CI("Op.", "114"),
    "composer": "Beethoven, Ludwig van",
    "form": "march",
    "title": "March and Chorus from Die Ruinen von Athen",
    "instrumentation": ["chorus", "orchestra"],
    "instrumentation_category": "choral",
    "publication_year": 1822,
}

# --- Op. 116: Tremate, empi, tremate ---
ENTRIES["116"] = {
    "catalog_info": CI("Op.", "116"),
    "composer": "Beethoven, Ludwig van",
    "form": "aria",
    "title": "Tremate, empi, tremate",
    "instrumentation": ["soprano", "tenor", "bass", "orchestra"],
    "instrumentation_category": "vocal",
    "publication_year": 1826,
}

# --- Op. 121a: Kakadu Variations ---
ENTRIES["121a"] = {
    "catalog_info": CI("Op.", "121a"),
    "composer": "Beethoven, Ludwig van",
    "form": "variations",
    "title": "Kakadu Variations",
    "instrumentation": ["piano", "violin", "cello"],
    "instrumentation_category": "chamber",
    "key_mode": "minor", "key_tonality": "G",
    "publication_year": 1824,
}

# --- Op. 121b: Opferlied ---
ENTRIES["121b"] = {
    "catalog_info": CI("Op.", "121b"),
    "composer": "Beethoven, Ludwig van",
    "form": "song",
    "title": "Opferlied",
    "instrumentation": ["soprano", "chorus", "orchestra"],
    "instrumentation_category": "choral",
    "publication_year": 1825,
}

# --- Op. 123: Missa solemnis ---
ENTRIES["123"] = {
    "catalog_info": CI("Op.", "123"),
    "composer": "Beethoven, Ludwig van",
    "form": "mass",
    "title": "Missa solemnis",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "choral",
    "key_mode": "major", "key_tonality": "D",
    "publication_year": 1827,
    "subpieces": [
        {"number": 1, "title": "Kyrie"},
        {"number": 2, "title": "Gloria"},
        {"number": 3, "title": "Credo"},
        {"number": 4, "title": "Sanctus"},
        {"number": 5, "title": "Agnus Dei"},
    ]
}

# --- Op. 134: Grosse Fuge (piano 4 hands) ---
ENTRIES["134"] = {
    "catalog_info": CI("Op.", "134"),
    "composer": "Beethoven, Ludwig van",
    "form": "fugue",
    "title": "Grosse Fuge (piano four-hand arrangement of Op. 133)",
    "instrumentation": ["piano"],
    "instrumentation_category": "piano",
    "key_mode": "major", "key_tonality": "B-flat",
    "publication_year": 1827,
}

# --- Op. 136: Der glorreiche Augenblick ---
ENTRIES["136"] = {
    "catalog_info": CI("Op.", "136"),
    "composer": "Beethoven, Ludwig van",
    "form": "cantata",
    "title": "Der glorreiche Augenblick",
    "instrumentation": ["soloists", "chorus", "orchestra"],
    "instrumentation_category": "choral",
    "publication_year": 1837,
}

# --- Op. 137: Fugue for String Quintet ---
ENTRIES["137"] = {
    "catalog_info": CI("Op.", "137"),
    "composer": "Beethoven, Ludwig van",
    "form": "fugue",
    "instrumentation": ["violin", "violin", "viola", "viola", "cello"],
    "instrumentation_category": "chamber",
    "key_mode": "major", "key_tonality": "D",
    "publication_year": 1827,
    "tempos": [T(1, "Allegretto")]
}


# =========================================================================
# Main: load, match, update, save
# =========================================================================
def main():
    os.chdir(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

    with open(CANON_PATH, 'r', encoding='utf-8') as f:
        canon = json.load(f)

    # Index Canon entries by catalog_number (only abbreviated Beethoven)
    canon_idx = {}
    for i, p in enumerate(canon):
        if p.get('composer') != 'Beethoven, Ludwig van':
            continue
        ci = p.get('catalog_info', [])
        if not ci or ci[0].get('catalog') != 'Op.':
            continue
        num = ci[0].get('catalog_number', '')
        # Check if still abbreviated (no form, no structured subpieces)
        has_form = bool(p.get('form'))
        has_cat = bool(p.get('instrumentation_category'))
        if not has_form and not has_cat:
            canon_idx[num] = i

    updated = 0
    mvts_added = 0

    for op_num, new_entry in sorted(ENTRIES.items(), key=lambda x: (len(x[0]), x[0])):
        if op_num not in canon_idx:
            log(f"SKIP Op. {op_num}: not found as abbreviated entry in Canon")
            continue

        idx = canon_idx[op_num]
        old = canon[idx]
        old_title = old.get('title', '(no title)')

        log("")
        log("=" * 60)
        display = new_entry.get('title') or new_entry.get('form', '?')
        if new_entry.get('number'):
            display += f" #{new_entry['number']}"
        log(f"Op. {op_num}: {display}")
        log(f"  OLD: \"{old_title}\"")

        # Log what's being added
        changes = []
        if new_entry.get('form'):
            changes.append(f"form={new_entry['form']}")
        if new_entry.get('title'):
            changes.append(f"title=\"{new_entry['title']}\"")
        if new_entry.get('number'):
            changes.append(f"number={new_entry['number']}")
        if new_entry.get('key_tonality'):
            changes.append(f"key={new_entry['key_tonality']} {new_entry.get('key_mode','')}")
        if new_entry.get('nickname'):
            changes.append(f"nickname=\"{new_entry['nickname']}\"")
        if new_entry.get('instrumentation_category'):
            changes.append(f"category={new_entry['instrumentation_category']}")
        if new_entry.get('publication_year'):
            changes.append(f"pub_year={new_entry['publication_year']}")

        log(f"  ADDED: {', '.join(changes)}")

        # Count and log movements
        mc = count_movements(new_entry)
        if mc > 0:
            log(f"  ADDED: {mc} movements/sections from web sources")
        if new_entry.get('subpieces'):
            for sp in new_entry['subpieces']:
                if sp.get('form') and sp.get('subpieces'):
                    sp_desc = sp.get('form', '?')
                    if sp.get('number'):
                        sp_desc += f" #{sp['number']}"
                    if sp.get('key_tonality'):
                        sp_desc += f" in {sp['key_tonality']} {sp.get('key_mode','')}"
                    sub_num = sp.get('catalog_info', [{}])[0].get('catalog_subnumber', '?')
                    smc = count_movements(sp)
                    log(f"    #{sub_num}: {sp_desc} ({smc} movements)")

        log(f"  SOURCE: Web (Wikipedia, IMSLP, AllMusic)")

        canon[idx] = new_entry
        updated += 1
        mvts_added += mc

    # Summary
    log("")
    log("=" * 60)
    log("SUMMARY")
    log("=" * 60)
    log(f"  Source: Web (Wikipedia, IMSLP, AllMusic, Hyperion, earsense.org)")
    log(f"  Total pieces updated: {updated}")
    log(f"  Total movements/sections added: {mvts_added}")

    with open(CANON_PATH, 'w', encoding='utf-8') as f:
        json.dump(canon, f, indent=2, ensure_ascii=False)
        f.write('\n')

    with open(CHANGELOG_PATH, 'w', encoding='utf-8') as f:
        f.write('\n'.join(changelog))
        f.write('\n')

    print(f"\nUpdated {updated} pieces ({mvts_added} movements)")
    print(f"Written: {CANON_PATH}")
    print(f"Written: {CHANGELOG_PATH}")


def count_movements(piece):
    count = 0
    subs = piece.get('subpieces', [])
    for s in subs:
        if s.get('tempos'):
            count += 1
        elif s.get('title') and not s.get('subpieces'):
            count += 1  # Named section (like mass Kyrie)
        else:
            count += count_movements(s)
    return count


if __name__ == '__main__':
    main()
