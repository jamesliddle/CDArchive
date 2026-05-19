"""
Generate CanonPiece and CanonAlbum JSON files from the raw iTunes extraction
for Gustav Mahler.

Inputs:
  data/mahler_itunes_raw.json              (produced by extract_mahler.py)

Outputs (for review/import, not canonical until the user Imports them):
  data/mahler_pieces.json                  (array of CanonPiece)
  data/mahler_albums.json                  (array of CanonAlbum)

Design notes:
- Pieces are defined by hand (this module) with canonical top-level movement
  structure — Symphony #2 has 5 movements, Symphony #3 has 6, etc.
- Album tracks take their subpiece_path directly from the iTunes track name's
  "Work - Movement" split. If the movement is "1. Allegro…" it matches the
  Number=1 subpiece directly; if it is "1a. Allegro…" the loose-match path
  added to PieceReferenceIndex will credit it against movement 1.
- The Boulez "Mahler Symphonies" 14-disc box set comes out of iTunes as 13
  separate "album artist" groupings because soloists differ per disc; we
  re-stitch them into a single CanonAlbum indexed by iTunes's Disc Number.
- Artist name parsing is best-effort — the iTunes "Album Artist" string is a
  comma-separated list of all the performers on a disc, which we split and
  tag with a light heuristic for instrument/role (conductor, chorus, etc.).
"""

import json
import re
import unicodedata
from pathlib import Path
from collections import defaultdict

ROOT = Path(__file__).parent.parent
RAW  = ROOT / "data" / "mahler_itunes_raw.json"
OUT_PIECES = ROOT / "data" / "mahler_pieces.json"
OUT_ALBUMS = ROOT / "data" / "mahler_albums.json"

COMPOSER = "Mahler, Gustav"


# ──────────────────────────────────────────────────────────────────────────
# Piece definitions
# ──────────────────────────────────────────────────────────────────────────
#
# Each entry produces one CanonPiece. iTunes work-title variants that all map
# to the same piece are listed in ``itunes_titles`` for the resolver in this
# script (not in the Canon).
#
# Subpiece tempo descriptions match what iTunes track names show for the
# simplest/canonical recording of each movement, so an album whose track name
# is "Work - 1. <tempo>" resolves via strict match. Rehearsal-split albums
# (1a./1b./…) rely on the loose-match path.

def symphony(number, key_tonality, key_mode, pub_year, comp_years, nickname=None,
             subtitle=None, subpieces=None, itunes_titles=None):
    p = {
        "composer": COMPOSER,
        "form": "Symphony",
        "number": number,
        "key_tonality": key_tonality,
        "key_mode": key_mode,
        "instrumentation_category": "Orchestra",
        "instrumentation": ["Orchestra"],
        "publication_year": pub_year,
        "composition_years": comp_years,
    }
    if nickname:
        p["nickname"] = nickname
    if subtitle:
        p["subtitle"] = subtitle
    if subpieces:
        p["subpieces"] = subpieces
    p["_itunes_titles"] = itunes_titles or []   # stripped before write
    return p


def movement(number, tempo, title=None):
    m = {"number": number}
    if title:
        m["title"] = title
    if tempo:
        m["tempos"] = [{"number": 1, "tempo_description": tempo}]
    return m


def song(number, title, first_line=None):
    s = {"number": number, "title": title}
    if first_line:
        s["first_line"] = first_line
    return s


PIECES = [
    # Symphony #1 in D (1887–88, rev. 1893–96; pub. 1898). "Titan" nickname was
    # used for early versions but withdrawn; we omit it from the canonical form
    # since the iTunes titles also omit it.
    symphony(
        1, "D", "major", 1898, "1887-1888",
        subpieces=[
            movement(1, "Langsam. Schleppend. Wie ein Naturlaut - Immer sehr gemächlich"),
            movement(2, "Kräftig bewegt, doch nicht zu schnell - Trio. Recht gemächlich"),
            movement(3, "Feierlich und gemessen, ohne zu schleppen"),
            movement(4, "Stürmisch bewegt"),
        ],
        itunes_titles=["Symphony #1 in D", "Symphony #1"],
    ),
    # Symphony #2 in C minor "Resurrection" (1888–94, pub. 1897)
    symphony(
        2, "C", "minor", 1897, "1888-1894",
        nickname="Resurrection",
        subpieces=[
            movement(1, "Allegro maestoso"),
            movement(2, "Andante moderato"),
            movement(3, "In ruhig fließender Bewegung"),
            movement(4, "Urlicht. Sehr feierlich, aber schlicht"),
            movement(5, "Im Tempo des Scherzos. Wild herausfahrend"),
        ],
        itunes_titles=['Symphony #2 in c "Resurrection"'],
    ),
    # Symphony #3 in D minor (1893–96, rev. 1906). 6 movements.
    symphony(
        3, "D", "minor", 1898, "1893-1896",
        subpieces=[
            movement(1, "Kräftig. Entschieden"),
            movement(2, "Tempo di Menuetto. Sehr mäßig"),
            movement(3, "Comodo. Scherzando. Ohne Hast"),
            movement(4, "Sehr langsam. Misterioso"),
            movement(5, "Lustig im Tempo und keck im Ausdruck"),
            movement(6, "Langsam. Ruhevoll. Empfunden"),
        ],
        itunes_titles=["Symphony #3 in d"],
    ),
    # Symphony #4 in G (1899–1900, rev. 1901–10). 4 movements.
    symphony(
        4, "G", "major", 1902, "1899-1900",
        subpieces=[
            movement(1, "Bedächtig, nicht eilen"),
            movement(2, "In gemächlicher Bewegung, ohne Hast"),
            movement(3, "Ruhevoll, poco adagio"),
            movement(4, "Sehr behaglich"),
        ],
        itunes_titles=["Symphony #4 in G"],
    ),
    # Symphony #5 in C# minor (1901–02, rev. later). 5 movements.
    symphony(
        5, "C-sharp", "minor", 1904, "1901-1902",
        subpieces=[
            movement(1, "Trauermarsch. In gemessenem Schritt. Streng. Wie ein Kondukt"),
            movement(2, "Stürmisch bewegt, mit größter Vehemenz"),
            movement(3, "Scherzo. Kräftig, nicht zu schnell"),
            movement(4, "Adagietto. Sehr langsam"),
            movement(5, "Rondo-Finale. Allegro - Allegro giocoso. Frisch"),
        ],
        itunes_titles=["Symphony #5 in c-sharp"],
    ),
    # Symphony #6 in A minor "Tragic" (1903–04). 4 movements. The Scherzo/
    # Andante order was later reversed by Mahler; we follow the iTunes order.
    symphony(
        6, "A", "minor", 1906, "1903-1904",
        nickname="Tragic",
        subpieces=[
            movement(1, "Allegro energico, ma non troppo. Heftig, aber markig"),
            movement(2, "Scherzo. Wuchtig"),
            movement(3, "Andante moderato"),
            movement(4, "Finale. Sostenuto - Allegro moderato - Allegro energico"),
        ],
        itunes_titles=["Symphony #6 in a"],
    ),
    # Symphony #7 in E minor "Song of the Night" (1904–05). 5 movements.
    symphony(
        7, "E", "minor", 1909, "1904-1905",
        subpieces=[
            movement(1, "Langsam (Adagio) - Allegro risoluto, ma non troppo"),
            movement(2, "Nachtmusik. Allegro moderato"),
            movement(3, "Scherzo. Schattenhaft. Fließend, aber nicht schnell"),
            movement(4, "Nachtmusik. Andante amoroso"),
            movement(5, "Rondo-Finale. Tempo I (Allegro ordinario) - Gemessen! Nicht schnell! Tempo II (Allegro moderato ma energico)"),
        ],
        itunes_titles=["Symphony #7 in e"],
    ),
    # Symphony #8 in E♭ "Symphony of a Thousand" (1906–07). iTunes splits this
    # into 24 flat tracks labelled "01." through "24." — 8 sections in Part I
    # and 16 in Part II — so we model it as 24 flat movements whose titles
    # embed the Part I / Part II grouping text. The zero-padded numeric-prefix
    # loose matcher lets "01. Part I…" resolve to movement number 1.
    symphony(
        8, "E-flat", "major", 1910, "1906-1907",
        nickname="Symphony of a Thousand",
        subpieces=[
            movement( 1, "Allegro impetuoso", title="Part I. Hymnus. Veni, creator spiritus. Allegro impetuoso"),
            movement( 2, "A tempo. Etwas (aber unmerklich) gemäßigter; immer sehr fließend", title="Part I. Hymnus. Veni, creator spiritus. A tempo. Etwas (aber unmerklich) gemäßigter; immer sehr fließend"),
            movement( 3, "Etwas drängend - Noch einmal so langsam (Nicht schleppend)", title="Part I. Hymnus. Veni, creator spiritus. Etwas drängend - Noch einmal so langsam (Nicht schleppend)"),
            movement( 4, "Tempo I. (Allegro, etwas hastig)", title="Part I. Hymnus. Veni, creator spiritus. Tempo I. (Allegro, etwas hastig)"),
            movement( 5, "Sehr fließend - Noch einmal so langsam als vorher. Nicht schleppend", title="Part I. Hymnus. Veni, creator spiritus. Sehr fließend - Noch einmal so langsam als vorher. Nicht schleppend"),
            movement( 6, "Plötzlich sehr breit und leidenschaftlichen Ausdrucks - Mit plötzlichem Aufschwung", title="Part I. Hymnus. Veni, creator spiritus. Plötzlich sehr breit und leidenschaftlichen Ausdrucks - Mit plötzlichem Aufschwung"),
            movement( 7, "Veni, creator spiritus", title="Part I. Hymnus. Veni, creator spiritus. Veni, creator spiritus"),
            movement( 8, "Wieder frisch", title="Part I. Hymnus. Veni, creator spiritus. Wieder frisch"),
            movement( 9, "Poco adagio", title="Part II. Schlußszene aus Goethes Faust II. Poco adagio"),
            movement(10, "Più mosso (Allegro moderato)", title="Part II. Schlußszene aus Goethes Faust II. Più mosso (Allegro moderato)"),
            movement(11, "Wieder langsam", title="Part II. Schlußszene aus Goethes Faust II. Wieder langsam"),
            movement(12, "Moderato", title="Part II. Schlußszene aus Goethes Faust II. Moderato"),
            movement(13, "Allegro - (Allegro appassionato)", title="Part II. Schlußszene aus Goethes Faust II. Allegro - (Allegro appassionato)"),
            movement(14, "Allegro deciso (Im Anfang noch nicht eilen)", title="Part II. Schlußszene aus Goethes Faust II. Allegro deciso (Im Anfang noch nicht eilen)"),
            movement(15, "Molto leggiero", title="Part II. Schlußszene aus Goethes Faust II. Molto leggiero"),
            movement(16, "Schon etwas langsamer und immer noch mäßiger - Wie die gleiche Stelle im I. Teil", title="Part II. Schlußszene aus Goethes Faust II. Schon etwas langsamer und immer noch mäßiger - Wie die gleiche Stelle im I. Teil"),
            movement(17, "Im Anfang (die ersten vier Takte) noch etwas gehalten", title="Part II. Schlußszene aus Goethes Faust II. Im Anfang (die ersten vier Takte) noch etwas gehalten"),
            movement(18, "Sempre l'istesso tempo", title="Part II. Schlußszene aus Goethes Faust II. Sempre l'istesso tempo"),
            movement(19, "Äußerst langsam. Adagissimo", title="Part II. Schlußszene aus Goethes Faust II. Äußerst langsam. Adagissimo"),
            movement(20, "Fließend", title="Part II. Schlußszene aus Goethes Faust II. Fließend"),
            movement(21, "Una poenitentium", title="Part II. Schlußszene aus Goethes Faust II. Una poenitentium"),
            movement(22, "Unmerklich frischer werden", title="Part II. Schlußszene aus Goethes Faust II. Unmerklich frischer werden"),
            movement(23, "Sehr langsam", title="Part II. Schlußszene aus Goethes Faust II. Sehr langsam"),
            movement(24, "Sehr langsam beginnend", title="Part II. Schlußszene aus Goethes Faust II. Sehr langsam beginnend"),
        ],
        itunes_titles=['Symphony #8 in E-flat "Symphony of a Thousand"'],
    ),
    # Symphony #9 in D (1909–10, pub. posth. 1912). 4 movements.
    symphony(
        9, "D", "major", 1912, "1909-1910",
        subpieces=[
            movement(1, "Andante comodo"),
            movement(2, "Im Tempo eines gemächlichen Ländlers (Fernerhin mit Tempo I. beseichnet) Etwas täppisch und sehr derb"),
            movement(3, "Rondo-Burleske. Allegro assai. Sehr trotzig"),
            movement(4, "Adagio. Sehr langsam und noch zurückhaltend"),
        ],
        itunes_titles=["Symphony #9 in D"],
    ),
    # Symphony #10 in F# (1910, unfinished). Most recordings are of the Adagio
    # (1st movement) only; a few use Cooke's performing version.
    symphony(
        10, "F-sharp", "major", 1924, "1910",
        subtitle="unfinished",
        subpieces=[
            movement(1, "Adagio"),
        ],
        itunes_titles=[
            "Symphony #10 in F-sharp",
            "Symphony #10 in F-sharp (unfinished)",
        ],
    ),

    # ── Orchestral / vocal works ────────────────────────────────────────
    # Das Lied von der Erde — tenor + alto + orchestra (1908–09, pub. 1912).
    {
        "composer": COMPOSER,
        "title": "Das Lied von der Erde",
        "title_english": "The Song of the Earth",
        "instrumentation_category": "Orchestra",
        "instrumentation": ["tenor", "alto", "Orchestra"],
        "publication_year": 1912,
        "composition_years": "1908-1909",
        "subpieces": [
            song(1, "Das Trinklied vom Jammer der Erde"),
            song(2, "Der Einsame im Herbst"),
            song(3, "Von der Jugend"),
            song(4, "Von der Schönheit"),
            song(5, "Der Trunkene im Frühling"),
            song(6, "Der Abschied"),
        ],
        "_itunes_titles": ["Das Lied von der Erde"],
    },

    # Das klagende Lied — cantata (1878–80, rev. 1893/1898).
    # 8 tracks in iTunes (Boulez records both parts; Waldmärchen + rest).
    {
        "composer": COMPOSER,
        "title": "Das klagende Lied",
        "title_english": "Song of Lamentation",
        "form": "Cantata",
        "instrumentation_category": "Orchestra",
        "instrumentation": ["soloists", "chorus", "Orchestra"],
        "publication_year": 1899,
        "composition_years": "1878-1880",
        "subpieces": [
            song(1, "Waldmärchen - Beim Weidenbaum, im kühlen Tann"),
            song(2, "Der Spielmann - Ein Spielmann zog einst des Weges daher"),
            song(3, "Der Spielmann - Ach Spielmann, lieber Spielmann mein!"),
            song(4, "Hochzeitsstück - Vom hohen Felsen erglänzt das Schloß"),
            song(5, "Hochzeitsstück - Was ist der König so stumm und bleich"),
            song(6, "Hochzeitsstück - Ach Spielmann, lieber Spielmann mein!"),
            song(7, "Hochzeitsstück - Auf springt der König von seinem Thron"),
            song(8, "Hochzeitsstück - Ach Bruder, lieber Bruder mein!"),
        ],
        "_itunes_titles": ["Das klagende Lied"],
    },

    # Totenfeier — tone poem, early version of what became the 1st movement
    # of Symphony #2. Stokowski recorded the original 1888 version.
    {
        "composer": COMPOSER,
        "title": "Totenfeier",
        "title_english": "Funeral Rite",
        "subtitle": "early version of the 1st movement of Symphony #2",
        "form": "Tone Poem",
        "instrumentation_category": "Orchestra",
        "instrumentation": ["Orchestra"],
        "composition_years": "1888",
        "_itunes_titles": [
            "Totenfeier (early version of the 1st movement of Symphony #2)",
            "Totenfeier",
        ],
    },

    # ── Song cycles ────────────────────────────────────────────────────
    # Lieder eines fahrenden Gesellen — baritone + orchestra/piano (1884–85)
    {
        "composer": COMPOSER,
        "title": "Lieder eines fahrenden Gesellen",
        "title_english": "Songs of a Wayfarer",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["baritone", "Orchestra"],
        "publication_year": 1897,
        "composition_years": "1884-1885",
        "subpieces": [
            song(1, "Wenn mein Schatz Hochzeit macht"),
            song(2, "Ging heut' morgen übers Feld"),
            song(3, "Ich hab ein glühend Messer"),
            song(4, "Die zwei blauen Augen"),
        ],
        "_itunes_titles": ["Lieder eines fahrenden Gesellen"],
    },
    # Kindertotenlieder — orch. song cycle (1901–04)
    {
        "composer": COMPOSER,
        "title": "Kindertotenlieder",
        "title_english": "Songs on the Death of Children",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["voice", "Orchestra"],
        "publication_year": 1905,
        "composition_years": "1901-1904",
        "subpieces": [
            song(1, "Nun will die Sonn' so hell aufgehn"),
            song(2, "Nun seh' ich wohl, warum so dunkle Flammen"),
            song(3, "Wenn dein Mütterlein tritt zur Tür herein"),
            song(4, "Oft denk' ich, sie sind nur ausgegangen"),
            song(5, "In diesem Wetter, in diesem Braus"),
        ],
        "_itunes_titles": ["Kindertotenlieder"],
    },
    # Rückert-Lieder — 5 Rückert settings (1901–02)
    {
        "composer": COMPOSER,
        "title": "Rückert-Lieder",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["voice", "Orchestra"],
        "publication_year": 1905,
        "composition_years": "1901-1902",
        "subpieces": [
            song(1, "Blicke mir nicht in die Lieder"),
            song(2, "Ich atmet' einen linden Duft"),
            song(3, "Liebst du um Schönheit"),
            song(4, "Ich bin der Welt abhanden gekommen"),
            song(5, "Um Mitternacht"),
        ],
        "_itunes_titles": ["Rückert-Lieder"],
    },
    # Des Knaben Wunderhorn — song collection (various selections; 12 in the
    # Boulez recording). The piece covers the standard 12-song published set.
    {
        "composer": COMPOSER,
        "title": "Des Knaben Wunderhorn",
        "title_english": "The Youth's Magic Horn",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["voice", "Orchestra"],
        "publication_year": 1899,
        "composition_years": "1892-1898",
        "subpieces": [
            song(1, "Der Schildwache Nachtlied"),
            song(2, "Verlor'ne Müh'"),
            song(3, "Trost im Unglück"),
            song(4, "Wer hat dies Liedlein erdacht?"),
            song(5, "Das irdische Leben"),
            song(6, "Revelge"),
            song(7, "Des Antonius von Padua Fischpredigt"),
            song(8, "Rheinlegendchen"),
            song(9, "Lied des Verfolgten im Turm"),
            song(10, "Wo die schönen Trompeten blasen"),
            song(11, "Lob des hohen Verstandes"),
            song(12, "Der Tamboursg'sell"),
        ],
        "_itunes_titles": [
            "Des Knaben Wunderhorn",
            "Des Knaben Wunderhorn Lieder",
        ],
    },
    # Lieder und Gesänge aus der Jugendzeit — 14 songs in 3 volumes (1880s)
    # The Hampson recital has selections from Vols II and III only.
    {
        "composer": COMPOSER,
        "title": "Lieder und Gesänge, Vol. II",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["voice", "piano"],
        "publication_year": 1892,
        "subpieces": [
            song(1, "Um schlimme Kinder artig zu machen"),
            song(2, "Ich ging mit Lust durch einen grünen Wald"),
            song(3, "Aus! Aus!"),
            song(4, "Starke Einbildungskraft"),
        ],
        "_itunes_titles": ["Lieder und Gesänge, Vol. II"],
    },
    {
        "composer": COMPOSER,
        "title": "Lieder und Gesänge, Vol. III",
        "form": "Song Cycle",
        "instrumentation_category": "Vocal",
        "instrumentation": ["voice", "piano"],
        "publication_year": 1892,
        "subpieces": [
            song(1, "Zu Straßburg auf der Schanz'"),
            song(2, "Ablösung im Sommer"),
            song(3, "Scheiden und Meiden"),
            song(4, "Nicht Wiedersehen!"),
            song(5, "Selbstgefühl"),
        ],
        "_itunes_titles": ["Lieder und Gesänge, Vol. III"],
    },
]


# ──────────────────────────────────────────────────────────────────────────
# Build DisplayTitle the way CanonPiece.BuildDisplayTitle does. This is what
# the resolver will match piece_refs against (composer + piece_title).
# ──────────────────────────────────────────────────────────────────────────

def display_title_for_piece(p):
    """Replicates CanonPiece.BuildDisplayTitle(includeCatalog=true)."""
    title  = p.get("title")
    form   = p.get("form")
    number = p.get("number")
    key_t  = p.get("key_tonality")
    key_m  = p.get("key_mode")
    subtitle = p.get("subtitle")
    nickname = p.get("nickname")

    # Form the Key display string: uppercase for major, lowercase for minor.
    if key_t:
        key_display = key_t.lower() if (key_m or "").lower() == "minor" else key_t
    else:
        key_display = ""

    if title:
        parts = [title]
        if key_display:
            parts[0] += f" in {key_display}"
        result = ", ".join(parts)
        if subtitle:
            result += f", {subtitle}"
        if nickname:
            result += f' "{nickname}"'
        return result

    # Form + number + key path (no explicit title)
    main = ""
    if form:
        main = form  # TitleCase handled by caller; our forms are already cased
        if number is not None:
            main += f" #{number}"
    if key_display:
        main += f" in {key_display}"
    result = main.strip()
    if subtitle:
        result += f", {subtitle}"
    if nickname:
        result += f' "{nickname}"'
    return result


# ──────────────────────────────────────────────────────────────────────────
# iTunes → Canon reference resolver (for building piece_refs per track)
# ──────────────────────────────────────────────────────────────────────────

def build_itunes_title_lookup(pieces):
    """Map every iTunes work title to the canonical (composer, piece_title)."""
    lookup = {}
    for p in pieces:
        canonical_title = display_title_for_piece(p)
        for itunes_title in p.get("_itunes_titles", []):
            lookup[itunes_title] = canonical_title
    return lookup


def split_work_and_movement(track_name):
    """Split 'Work - Movement' on the first ' - '. Returns (work, movement)."""
    idx = track_name.find(" - ")
    if idx < 0:
        return track_name.strip(), None
    return track_name[:idx].strip(), track_name[idx + 3:].strip()


# ──────────────────────────────────────────────────────────────────────────
# Album assembly
# ──────────────────────────────────────────────────────────────────────────

# The Boulez set is split across many "album artist" strings in iTunes
# because soloists differ per disc. All 13 iTunes groupings with this title
# belong to one physical box set.
BOULEZ_TITLE = "Mahler Symphonies Boulez"

# Albums that are multi-composer compilations/samplers where Mahler is just
# one track. We still catalog them, but only the Mahler tracks get piece_refs;
# non-Mahler tracks are left uncatalogued with a description note.
MULTICOMPOSER_ALBUM_TITLES = {
    "Classic CD Sampler 37",
    "Gramophone Editor's Choice 1997 January",
    "Stokowski Bach Toccata and Fugue and rehearsals",
    "Mahler Symphony 2 Brahms Symphony 4 Stokowski",   # Brahms on disc 2
}

# Rough (label, catalogue) hints for the discs we can name with high
# confidence. All remaining albums get empty label/catalogue and the user
# can fill them in. The script only needs the *title* for identity; the
# user can edit everything else in the UI after import.
KNOWN_LABELS = {
    # intentionally left mostly empty — easier for the user to edit in-app
}


def parse_performers(artist_string):
    """Best-effort performer parsing: split on commas, tag last name as
    conductor if we can detect a known conductor surname, else leave role
    empty and let the user fix up in the UI."""
    conductors = {
        "Leonard Bernstein", "Pierre Boulez", "Georg Solti",
        "Claudio Abbado", "Eliahu Inbal", "Leopold Stokowski",
        "George Szell", "Riccardo Chailly", "Giuseppe Sinopoli",
    }
    if not artist_string:
        return None
    parts = [p.strip() for p in artist_string.split(",") if p.strip()]
    perf = []
    for p in parts:
        entry = {"name": p}
        if p in conductors:
            entry["instrument"] = "Conductor"
        perf.append(entry)
    return perf if perf else None


def build_track_ref(track_name, title_lookup):
    """Build piece_refs for one iTunes track. Returns (piece_refs, description).
    If the work title doesn't map to a known piece, returns ([], description)
    so the track is catalogued as uncatalogued with the raw name."""
    work, movement = split_work_and_movement(track_name)
    canonical = title_lookup.get(work)
    if not canonical:
        return None, track_name  # uncatalogued — keep full track name in description
    ref = {
        "composer": COMPOSER,
        "piece_title": canonical,
    }
    if movement:
        ref["subpiece_path"] = [movement]
    return [ref], None


def duration_from_ms(ms):
    """Convert an iTunes 'Total Time' (ms) to "m:ss" or "h:mm:ss"."""
    if not ms:
        return None
    total_sec = int(ms) // 1000
    h, rem = divmod(total_sec, 3600)
    m, s = divmod(rem, 60)
    if h > 0:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m}:{s:02d}"


def build_album_from_group(title, groups, title_lookup):
    """Construct one CanonAlbum from a list of iTunes (artist, tracks) groups.
    Used both for single-entry albums and for the Boulez box set where many
    iTunes groups share the title."""
    # Collect all discs, preferring iTunes Disc Number. Duplicate disc numbers
    # across groups (shouldn't normally happen within one physical album) are
    # merged by track number.
    disc_buckets = defaultdict(list)      # disc_num → list[(track_num, track_dict)]
    all_performers = []

    for g in groups:
        # Parse performers once per group (they differ per disc in the Boulez box).
        perf = parse_performers(g["album_artist"])
        if perf:
            for p in perf:
                if p not in all_performers:
                    all_performers.append(p)

        for disc in g["discs"]:
            dn = disc["disc_number"]
            for t in disc["tracks"]:
                disc_buckets[dn].append(t)

    # Emit discs in order
    discs = []
    for dn in sorted(disc_buckets.keys()):
        tracks = []
        # iTunes sometimes reports the same (disc, track) twice when two
        # groupings both claim it; dedupe by track number.
        seen_track_nums = set()
        for t in sorted(disc_buckets[dn], key=lambda t: (t.get("track_number") or 0)):
            tn = t.get("track_number")
            if tn in seen_track_nums:
                continue
            seen_track_nums.add(tn)

            name = t.get("name") or ""
            composer = t.get("composer") or ""
            is_mahler = "Mahler" in composer

            track = {"track_number": tn}
            dur = duration_from_ms(t.get("total_time_ms"))
            if dur:
                track["duration"] = dur

            if is_mahler:
                refs, desc = build_track_ref(name, title_lookup)
                if refs:
                    track["piece_refs"] = refs
                elif desc:
                    track["description"] = desc
            else:
                # Non-Mahler track on a mixed album: describe verbatim so the
                # album record is still complete.
                track["description"] = f"{composer}: {name}" if composer else name

            tracks.append(track)

        discs.append({"disc_number": dn, "tracks": tracks})

    album = {
        "title": title,
        "discs": discs,
    }
    if all_performers:
        album["performers"] = all_performers
    return album


def main():
    with RAW.open(encoding="utf-8") as f:
        raw = json.load(f)

    # Group raw albums by iTunes title.
    by_title = defaultdict(list)
    for a in raw:
        by_title[a["album_title"]].append(a)

    # Build the canonical piece list (strip our internal _itunes_titles hint).
    title_lookup = build_itunes_title_lookup(PIECES)
    canon_pieces = []
    for p in PIECES:
        cp = {k: v for k, v in p.items() if not k.startswith("_")}
        canon_pieces.append(cp)

    # Emit pieces file
    with OUT_PIECES.open("w", encoding="utf-8") as f:
        json.dump(canon_pieces, f, indent=2, ensure_ascii=False)

    # Emit albums file
    canon_albums = []
    for title, groups in sorted(by_title.items()):
        album = build_album_from_group(title, groups, title_lookup)
        canon_albums.append(album)

    with OUT_ALBUMS.open("w", encoding="utf-8") as f:
        json.dump(canon_albums, f, indent=2, ensure_ascii=False)

    # Report
    print(f"Wrote {OUT_PIECES}  ({len(canon_pieces)} pieces)")
    print(f"Wrote {OUT_ALBUMS}  ({len(canon_albums)} albums)")
    print()
    print("Piece titles (how piece_refs will address them):")
    for p in canon_pieces:
        print(f"  {display_title_for_piece(p)}")
    print()
    print("Albums:")
    for a in canon_albums:
        n_discs  = len(a["discs"])
        n_tracks = sum(len(d["tracks"]) for d in a["discs"])
        n_refs   = sum(1 for d in a["discs"] for t in d["tracks"] if t.get("piece_refs"))
        print(f"  [{n_discs}d / {n_tracks}t / {n_refs}ref] {a['title']}")


if __name__ == "__main__":
    main()
