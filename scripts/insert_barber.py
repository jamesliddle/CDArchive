"""
Insert Samuel Barber entries into Classical Canon pieces.json
"""
import json
import sys

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

def op(num, subnumber=None):
    entry = {"catalog": "Op.", "catalog_number": str(num)}
    if subnumber is not None:
        entry["catalog_subnumber"] = str(subnumber)
    return entry

def mvt(number, tempos=None, form=None, key_tonality=None, key_mode=None,
        title=None, first_line=None, text_author=None):
    m = {"number": number}
    if form:         m["form"] = form
    if title:        m["title"] = title
    if first_line:   m["first_line"] = first_line
    if key_tonality: m["key_tonality"] = key_tonality
    if key_mode:     m["key_mode"] = key_mode
    if tempos:
        m["tempos"] = [{"number": i+1, "description": t} for i, t in enumerate(tempos)]
    if text_author:  m["text_author"] = text_author
    return m

BARBER_PIECES = [
    # Op. 1 — Serenade for Strings
    {
        "composer": "Barber, Samuel",
        "form": "Serenade",
        "title": "Serenade for Strings",
        "catalog_info": [op(1)],
        "instrumentation": ["String Orchestra"],
        "instrumentation_category": "Chamber",
        "publication_year": 1928,
        "subpieces": [
            mvt(1, tempos=["Moderato"]),
            mvt(2, tempos=["Vivace"]),
            mvt(3, tempos=["Molto adagio — Tempo primo"]),
        ]
    },
    # Op. 2 — Three Songs
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Three Songs",
        "catalog_info": [op(2)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1937,
        "subpieces": [
            {**mvt(1, title="The Daisies"), "text_author": "Stephens, James"},
            {**mvt(2, title="With Rue My Heart Is Laden"), "text_author": "Housman, A. E."},
            {**mvt(3, title="Bessie Bobtail"), "text_author": "Stephens, James"},
        ]
    },
    # Op. 3 — Dover Beach
    {
        "composer": "Barber, Samuel",
        "form": "Song",
        "title": "Dover Beach",
        "catalog_info": [op(3)],
        "instrumentation": ["Voice", "String Quartet"],
        "instrumentation_category": "Chamber",
        "publication_year": 1931,
        "text_author": "Arnold, Matthew"
    },
    # Op. 5 — Overture to The School for Scandal
    {
        "composer": "Barber, Samuel",
        "form": "Overture",
        "title": "Overture to The School for Scandal",
        "catalog_info": [op(5)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1933
    },
    # Op. 7 — Music for a Scene from Shelley
    {
        "composer": "Barber, Samuel",
        "form": "Tone Poem",
        "title": "Music for a Scene from Shelley",
        "catalog_info": [op(7)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1935
    },
    # Op. 9 — Symphony No. 1
    {
        "composer": "Barber, Samuel",
        "form": "Symphony",
        "number": 1,
        "catalog_info": [op(9)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Symphonic",
        "publication_year": 1936,
        "subpieces": [
            mvt(1, tempos=["Allegro ma non troppo"]),
            mvt(2, tempos=["Allegro molto"]),
            mvt(3, tempos=["Andante tranquillo"]),
            mvt(4, tempos=["Con moto"]),
        ]
    },
    # Op. 10 — Three Songs (Joyce)
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Three Songs",
        "catalog_info": [op(10)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1937,
        "text_author": "Joyce, James",
        "subpieces": [
            mvt(1, title="Rain Has Fallen"),
            mvt(2, title="Sleep Now"),
            mvt(3, title="I Hear an Army"),
        ]
    },
    # Op. 11 — String Quartet No. 1
    {
        "composer": "Barber, Samuel",
        "form": "String Quartet",
        "number": 1,
        "catalog_info": [op(11)],
        "instrumentation": ["String Quartet"],
        "instrumentation_category": "Chamber",
        "publication_year": 1936,
        "subpieces": [
            mvt(1, tempos=["Molto allegro e appassionato"]),
            mvt(2, tempos=["Molto adagio"]),
            mvt(3, tempos=["Molto allegro come prima"]),
        ]
    },
    # Op. 11a — Adagio for Strings
    {
        "composer": "Barber, Samuel",
        "form": "Adagio",
        "title": "Adagio for Strings",
        "catalog_info": [{"catalog": "Op.", "catalog_number": "11a"}],
        "instrumentation": ["String Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1938
    },
    # Op. 12 — Essay for Orchestra No. 1
    {
        "composer": "Barber, Samuel",
        "form": "Essay",
        "number": 1,
        "title": "Essay for Orchestra",
        "catalog_info": [op(12)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1938
    },
    # Op. 13 — Four Songs
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Four Songs",
        "catalog_info": [op(13)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1938,
        "subpieces": [
            {**mvt(1, title="A Nun Takes the Veil"), "text_author": "Hopkins, Gerard Manley"},
            {**mvt(2, title="The Secrets of the Old"), "text_author": "Yeats, William Butler"},
            {**mvt(3, title="Sure on This Shining Night"), "text_author": "Agee, James"},
            {**mvt(4, title="Nocturne"), "text_author": "Prokosch, Frederic"},
        ]
    },
    # Op. 14 — Violin Concerto
    {
        "composer": "Barber, Samuel",
        "form": "Concerto",
        "title": "Violin Concerto",
        "catalog_info": [op(14)],
        "instrumentation": ["Violin", "Orchestra"],
        "instrumentation_category": "Concerto",
        "publication_year": 1939,
        "subpieces": [
            mvt(1, tempos=["Allegro"]),
            mvt(2, tempos=["Andante sostenuto"]),
            mvt(3, tempos=["Presto in moto perpetuo"]),
        ]
    },
    # Op. 17 — Second Essay for Orchestra
    {
        "composer": "Barber, Samuel",
        "form": "Essay",
        "number": 2,
        "title": "Second Essay for Orchestra",
        "catalog_info": [op(17)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1942
    },
    # Op. 18 — Two Songs
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Two Songs",
        "catalog_info": [op(18)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1943,
        "subpieces": [
            {**mvt(1, title="The Queen's Face on the Summery Coin"), "text_author": "Horan, Robert"},
            {**mvt(2, title="Monks and Raisins"), "text_author": "Villard, León"},
        ]
    },
    # Op. 19 — Symphony No. 2
    {
        "composer": "Barber, Samuel",
        "form": "Symphony",
        "number": 2,
        "catalog_info": [op(19)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Symphonic",
        "publication_year": 1944,
        "subpieces": [
            mvt(1, tempos=["Allegro ma non troppo"]),
            mvt(2, tempos=["Andante un poco mosso"]),
            mvt(3, tempos=["Presto"]),
        ]
    },
    # Op. 20 — Excursions
    {
        "composer": "Barber, Samuel",
        "form": "Suite",
        "title": "Excursions",
        "catalog_info": [op(20)],
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard",
        "publication_year": 1945,
        "subpieces": [
            mvt(1, tempos=["Un poco allegro"]),
            mvt(2, tempos=["In slow blues tempo"]),
            mvt(3, tempos=["Allegretto"]),
            mvt(4, tempos=["Allegro molto"]),
        ]
    },
    # Op. 22 — Cello Concerto
    {
        "composer": "Barber, Samuel",
        "form": "Concerto",
        "title": "Cello Concerto",
        "catalog_info": [op(22)],
        "instrumentation": ["Cello", "Orchestra"],
        "instrumentation_category": "Concerto",
        "publication_year": 1945,
        "subpieces": [
            mvt(1, tempos=["Allegro moderato"]),
            mvt(2, tempos=["Andante sostenuto"]),
            mvt(3, tempos=["Molto allegro e appassionato"]),
        ]
    },
    # Op. 23 — Cave of the Heart (Medea)
    {
        "composer": "Barber, Samuel",
        "form": "Ballet",
        "title": "Cave of the Heart",
        "catalog_info": [op(23)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Ballet",
        "publication_year": 1946
    },
    # Op. 23a — Medea's Dance of Vengeance
    {
        "composer": "Barber, Samuel",
        "form": "Tone Poem",
        "title": "Medea's Dance of Vengeance",
        "catalog_info": [{"catalog": "Op.", "catalog_number": "23a"}],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1956
    },
    # Op. 24 — Knoxville: Summer of 1915
    {
        "composer": "Barber, Samuel",
        "form": "Tone Poem",
        "title": "Knoxville: Summer of 1915",
        "catalog_info": [op(24)],
        "instrumentation": ["Soprano", "Orchestra"],
        "instrumentation_category": "Vocal",
        "publication_year": 1948,
        "text_author": "Agee, James"
    },
    # Op. 25 — Nuvoletta
    {
        "composer": "Barber, Samuel",
        "form": "Song",
        "title": "Nuvoletta",
        "catalog_info": [op(25)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1947,
        "text_author": "Joyce, James"
    },
    # Op. 26 — Piano Sonata
    {
        "composer": "Barber, Samuel",
        "form": "Sonata",
        "title": "Piano Sonata",
        "catalog_info": [op(26)],
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard",
        "publication_year": 1949,
        "subpieces": [
            mvt(1, tempos=["Allegro energico"]),
            mvt(2, tempos=["Allegro vivace e leggero"]),
            mvt(3, tempos=["Adagio mesto"]),
            mvt(4, form="Fugue", tempos=["Fuga: Allegro con spirito"]),
        ]
    },
    # Op. 27 — Mélodies passagères
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Mélodies passagères",
        "catalog_info": [op(27)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1952,
        "text_author": "Rilke, Rainer Maria",
        "subpieces": [
            mvt(1, title="Puisque tout passe"),
            mvt(2, title="Un cygne"),
            mvt(3, title="Tombeau dans un parc"),
            mvt(4, title="Le clocher chante"),
            mvt(5, title="Départ"),
        ]
    },
    # Op. 28 — Souvenirs
    {
        "composer": "Barber, Samuel",
        "form": "Suite",
        "title": "Souvenirs",
        "catalog_info": [op(28)],
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard",
        "publication_year": 1952,
        "subpieces": [
            mvt(1, form="Waltz"),
            mvt(2, form="Schottische"),
            mvt(3, form="Pas de deux"),
            mvt(4, form="Two-Step"),
            mvt(5, title="Hesitation-Tango"),
            mvt(6, form="Galop"),
        ]
    },
    # Op. 29 — Hermit Songs
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Hermit Songs",
        "catalog_info": [op(29)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1953,
        "subpieces": [
            mvt(1, title="At Saint Patrick's Purgatory"),
            mvt(2, title="Church Bell at Night"),
            mvt(3, title="St. Ita's Vision"),
            mvt(4, title="The Heavenly Banquet"),
            mvt(5, title="The Crucifixion"),
            mvt(6, title="Sea-Snatch"),
            mvt(7, title="Promiscuity"),
            mvt(8, title="The Monk and His Cat"),
            mvt(9, title="The Praises of God"),
            mvt(10, title="The Desire for Hermitage"),
        ]
    },
    # Op. 30 — Prayers of Kierkegaard
    {
        "composer": "Barber, Samuel",
        "form": "Cantata",
        "title": "Prayers of Kierkegaard",
        "catalog_info": [op(30)],
        "instrumentation": ["Soprano", "Chorus", "Orchestra"],
        "instrumentation_category": "Choral",
        "publication_year": 1954,
        "text_author": "Kierkegaard, Søren"
    },
    # Op. 31 — Summer Music
    {
        "composer": "Barber, Samuel",
        "form": "Suite",
        "title": "Summer Music",
        "catalog_info": [op(31)],
        "instrumentation": ["Wind Quintet"],
        "instrumentation_category": "Chamber",
        "publication_year": 1956
    },
    # Op. 32 — Vanessa
    {
        "composer": "Barber, Samuel",
        "form": "Opera",
        "title": "Vanessa",
        "catalog_info": [op(32)],
        "instrumentation": ["Voices", "Orchestra"],
        "instrumentation_category": "Opera",
        "publication_year": 1958,
        "librettist": ["Menotti, Gian Carlo"]
    },
    # Op. 33 — Nocturne (Homage to John Field)
    {
        "composer": "Barber, Samuel",
        "form": "Nocturne",
        "title": "Nocturne",
        "catalog_info": [op(33)],
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard",
        "publication_year": 1959,
        "nickname": "Homage to John Field"
    },
    # Op. 38 — Piano Concerto
    {
        "composer": "Barber, Samuel",
        "form": "Concerto",
        "title": "Piano Concerto",
        "catalog_info": [op(38)],
        "instrumentation": ["Piano", "Orchestra"],
        "instrumentation_category": "Concerto",
        "publication_year": 1962,
        "subpieces": [
            mvt(1, tempos=["Allegro appassionato"]),
            mvt(2, tempos=["Canzone: Moderato"]),
            mvt(3, tempos=["Allegro molto"]),
        ]
    },
    # Op. 38a — Canzone
    {
        "composer": "Barber, Samuel",
        "form": "Canzone",
        "title": "Canzone",
        "catalog_info": [{"catalog": "Op.", "catalog_number": "38a"}],
        "instrumentation": ["Flute", "Piano"],
        "instrumentation_category": "Chamber",
        "publication_year": 1962
    },
    # Op. 39 — Andromache's Farewell
    {
        "composer": "Barber, Samuel",
        "form": "Scena",
        "title": "Andromache's Farewell",
        "catalog_info": [op(39)],
        "instrumentation": ["Soprano", "Orchestra"],
        "instrumentation_category": "Vocal",
        "publication_year": 1962,
        "text_author": "Euripides"
    },
    # Op. 40 — Antony and Cleopatra
    {
        "composer": "Barber, Samuel",
        "form": "Opera",
        "title": "Antony and Cleopatra",
        "catalog_info": [op(40)],
        "instrumentation": ["Voices", "Orchestra"],
        "instrumentation_category": "Opera",
        "publication_year": 1966,
        "librettist": ["Zeffirelli, Franco"]
    },
    # Op. 41 — Despite and Still
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Despite and Still",
        "catalog_info": [op(41)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1968,
        "subpieces": [
            {**mvt(1, title="A Last Song"), "text_author": "Graves, Robert"},
            {**mvt(2, title="My Lizard"), "text_author": "Roethke, Theodore"},
            {**mvt(3, title="In the Wilderness"), "text_author": "Graves, Robert"},
            {**mvt(4, title="Solitary Hotel"), "text_author": "Joyce, James"},
            {**mvt(5, title="Despite and Still"), "text_author": "Graves, Robert"},
        ]
    },
    # Op. 43 — The Lovers
    {
        "composer": "Barber, Samuel",
        "form": "Cantata",
        "title": "The Lovers",
        "catalog_info": [op(43)],
        "instrumentation": ["Baritone", "Chorus", "Orchestra"],
        "instrumentation_category": "Choral",
        "publication_year": 1971,
        "text_author": "Neruda, Pablo"
    },
    # Op. 45 — Three Songs
    {
        "composer": "Barber, Samuel",
        "form": "Song Cycle",
        "title": "Three Songs",
        "catalog_info": [op(45)],
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song",
        "publication_year": 1972,
        "subpieces": [
            {**mvt(1, title="Now Have I Fed and Eaten Up the Rose"), "text_author": "Klopstock, Friedrich"},
            {**mvt(2, title="A Green Lowland of Pianos"), "text_author": "Harasymowicz, Jerzy"},
            {**mvt(3, title="O Boundless, Boundless Evening"), "text_author": "Stadler, Ernst"},
        ]
    },
    # Op. 46 — Ballade
    {
        "composer": "Barber, Samuel",
        "form": "Ballade",
        "title": "Ballade",
        "catalog_info": [op(46)],
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard",
        "publication_year": 1977
    },
    # Op. 47 — Third Essay for Orchestra
    {
        "composer": "Barber, Samuel",
        "form": "Essay",
        "number": 3,
        "title": "Third Essay for Orchestra",
        "catalog_info": [op(47)],
        "instrumentation": ["Orchestra"],
        "instrumentation_category": "Orchestral",
        "publication_year": 1978
    },
    # Agnus Dei (choral arrangement of Op. 11a)
    {
        "composer": "Barber, Samuel",
        "form": "Agnus Dei",
        "title": "Agnus Dei",
        "instrumentation": ["Chorus"],
        "instrumentation_category": "Choral",
        "publication_year": 1967
    },
    # Interlude No. 1 "Adagio for Jeanne"
    {
        "composer": "Barber, Samuel",
        "form": "Interlude",
        "number": 1,
        "title": "Adagio for Jeanne",
        "instrumentation": ["Piano"],
        "instrumentation_category": "Keyboard"
    },
    # Slumber Song of the Madonna
    {
        "composer": "Barber, Samuel",
        "form": "Song",
        "title": "Slumber Song of the Madonna",
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song"
    },
    # There's Nae Lark
    {
        "composer": "Barber, Samuel",
        "form": "Song",
        "title": "There's Nae Lark",
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song"
    },
    # Love at the Door
    {
        "composer": "Barber, Samuel",
        "form": "Song",
        "title": "Love at the Door",
        "instrumentation": ["Voice", "Piano"],
        "instrumentation_category": "Song"
    },
]


def main():
    print(f"Loading {DATA_FILE}...")
    with open(DATA_FILE, encoding="utf-8") as f:
        pieces = json.load(f)

    # Verify no existing Barber entries
    existing = [p for p in pieces if p.get("composer") == "Barber, Samuel"]
    if existing:
        print(f"WARNING: Found {len(existing)} existing Barber entries. Aborting.")
        sys.exit(1)

    print(f"Currently {len(pieces)} pieces. Adding {len(BARBER_PIECES)} Barber pieces...")

    pieces.extend(BARBER_PIECES)

    print(f"Now {len(pieces)} pieces. Saving...")
    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(pieces, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("Done.")

if __name__ == "__main__":
    main()
