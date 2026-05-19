"""
Generate CanonAlbum JSON for the Beethoven-symphony albums extracted by
extract_beethoven_symphonies.py.

Input:  data/beethoven_symphonies_itunes_raw.json
        data/Classical Canon pieces.json   (for piece_ref resolution)
Output: data/beethoven_symphonies_albums.json

For each iTunes track the generator tries to resolve a canonical piece by
(composer, work title) and the movement number inside that piece. Successful
resolutions emit a TrackPieceRef; failures fall back to a `description` field
on the track.

Non-destructive. Pure JSON generation — merge step is separate.
"""

import io
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT      = Path(__file__).parent.parent
DATA      = ROOT / "data"
RAW_INPUT = DATA / "beethoven_symphonies_itunes_raw.json"
PIECES_IN = DATA / "Classical Canon pieces.json"
OUTPUT    = DATA / "beethoven_symphonies_albums.json"


# ── DisplayTitle replica (must match CanonPiece.BuildDisplayTitle) ──────────

def title_case(s):
    if not s:
        return s
    return s[0].upper() + s[1:] if s[0].islower() else s


def catalog_of(piece):
    ci = piece.get("catalog_info") or []
    if not ci:
        return ""
    c = ci[0]
    cat = (c.get("catalog") or "").strip()
    num = (c.get("catalog_number") or "").strip()
    sub = (c.get("catalog_subnumber") or "").strip()
    if num and sub:
        return f"{cat} {num} #{sub}".strip()
    if num:
        return f"{cat} {num}".strip()
    if sub:
        return f"{cat} #{sub}".strip()
    return cat


def key_display(piece):
    kt = (piece.get("key_tonality") or "").strip()
    km = (piece.get("key_mode") or "").strip().lower()
    if not kt:
        return ""
    return kt.lower() if km == "minor" else kt


def display_title(piece, include_nick_sub=True):
    title    = piece.get("title")
    form     = piece.get("form")
    number   = piece.get("number")
    subtitle = piece.get("subtitle")
    nickname = piece.get("nickname")
    key      = key_display(piece)
    catalog  = catalog_of(piece)

    if title:
        parts = [title]
        if key:
            parts[0] += f" in {key}"
        if catalog:
            parts.append(catalog)
        result = ", ".join(parts)
        if include_nick_sub:
            if subtitle:
                result += f", {subtitle}"
            if nickname:
                result += f' "{nickname}"'
        return result

    main = ""
    if form:
        main = title_case(form)
        if number is not None:
            main += f" #{number}"
    if key:
        main += f" in {key}"
    main = main.strip()

    parts = [main] if main else []
    if catalog:
        parts.append(catalog)
    result = ", ".join(parts)
    if include_nick_sub:
        if subtitle:
            result += f", {subtitle}"
        if nickname:
            result += f' "{nickname}"'
    return result


def subpiece_label(subpiece):
    """Replicate the canonical subpiece label format: '{N}. [{Form}. ]{Tempos joined by ' - '}'.

    Examples (Beethoven symphonies):
      (n=1, form=None, tempos=['Allegro con brio'])            -> '1. Allegro con brio'
      (n=3, form='Scherzo', tempos=['Allegro vivace'])         -> '3. Scherzo. Allegro vivace'
      (n=4, form='Finale', tempos=['Allegro molto','Poco andante','Presto'])
                                                               -> '4. Finale. Allegro molto - Poco andante - Presto'
    """
    n = subpiece.get("number")
    form = subpiece.get("form")
    title = subpiece.get("title")
    tempos = [t.get("tempo_description") or "" for t in (subpiece.get("tempos") or [])]
    tempos = [x for x in tempos if x]

    parts = []
    if n is not None:
        parts.append(f"{n}.")
    if form:
        parts.append(f"{form}.")
    if title and not tempos:
        parts.append(title)
    if tempos:
        parts.append(" - ".join(tempos))
    return " ".join(p for p in parts if p).strip()


# ── iTunes track parsing ────────────────────────────────────────────────────

def parse_itunes_composer(s):
    """'Beethoven, Ludwig van (1770–1827)' -> 'Beethoven, Ludwig van'."""
    if not s:
        return ""
    return re.sub(r"\s*\([^)]*\)\s*$", "", s).strip()


def split_work_movement(name):
    """Split 'Work Title - Movement' into (work, movement).
    Returns (work, None) when there is no ' - ' separator."""
    if not name:
        return "", None
    if " - " in name:
        work, mvmt = name.split(" - ", 1)
        return work.strip(), mvmt.strip()
    return name.strip(), None


def split_compound_movements(mvmt_str):
    """Split '3. Allegro - 4. Allegro' into ['3. Allegro', '4. Allegro'].
    A split happens only where ' - ' is followed by a digit then a period,
    so tempo descriptions with embedded hyphens stay whole."""
    return re.split(r" - (?=\d+\.)", mvmt_str)


def parse_movement_number(mvmt_segment):
    """'1. Allegro con brio' -> (1, 'Allegro con brio').
    Also handles split-track forms '4a. Presto' / '4b. Allegro assai' by
    keeping just the numeric part (the canon has one subpiece #4, so both
    split tracks resolve to the same ref — acceptable granularity loss).
    Returns (None, full_string) if no leading 'N[letter].' prefix."""
    m = re.match(r"^(\d+)[a-zA-Z]?\.\s*(.*)$", mvmt_segment.strip())
    if m:
        return int(m.group(1)), m.group(2).strip()
    return None, mvmt_segment.strip()


def normalize_title(s):
    """Normalize a work title for lookup. Canon stores keys as '♭'/'♯' but
    iTunes writes '-flat'/'-sharp'. Also lowercases and collapses whitespace."""
    if not s:
        return ""
    s = s.replace("\u266D", "-flat").replace("\u266F", "-sharp")
    s = re.sub(r"\s+", " ", s)
    return s.strip().lower()


# ── Piece index ─────────────────────────────────────────────────────────────

def build_piece_index(pieces):
    """composer -> { normalised_work_title: piece }
    Registers both full (with nickname/subtitle) and stripped forms so tracks
    whose work title may or may not include the nickname still resolve.
    Keys pass through normalize_title() so the canon's ♭/♯ glyphs match
    iTunes's spelled-out '-flat'/'-sharp'."""
    idx = defaultdict(dict)
    for p in pieces:
        composer = (p.get("composer") or "").strip()
        if not composer:
            continue
        full     = display_title(p, include_nick_sub=True)
        stripped = display_title(p, include_nick_sub=False)
        for variant in {full, stripped}:
            if variant:
                idx[composer][normalize_title(variant)] = p
    return idx


def find_subpiece(piece, number):
    for sp in piece.get("subpieces") or []:
        if sp.get("number") == number:
            return sp
    return None


# ── Duration ────────────────────────────────────────────────────────────────

def ms_to_duration(ms):
    if not ms:
        return None
    secs = int(round(ms / 1000))
    h, r = divmod(secs, 3600)
    m, s = divmod(r, 60)
    if h > 0:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m}:{s:02d}"


# ── Resolution per track ────────────────────────────────────────────────────

class ResolutionStats:
    def __init__(self):
        self.resolved = 0
        self.unresolved_piece = 0
        self.unresolved_subpiece = 0
        self.unresolved_examples = []

    def note_unresolved(self, composer, work, reason, track_name):
        self.unresolved_examples.append(f"{reason}: [{composer}] {work!r} — track: {track_name!r}")


def resolve_refs(itunes_track, piece_index, stats):
    """Produce a list of TrackPieceRef dicts for an iTunes track.
    Returns [] when the track can't be resolved — caller emits description-only."""
    composer = parse_itunes_composer(itunes_track.get("composer") or "")
    if not composer:
        return []

    work, mvmt_str = split_work_movement(itunes_track.get("name") or "")
    composer_idx = piece_index.get(composer) or {}
    piece = composer_idx.get(normalize_title(work))

    if piece is None:
        # Try stripping trailing nickname: '... "Eroica"' form.
        stripped = re.sub(r'\s*"[^"]*"\s*$', "", work).strip()
        if stripped and stripped.lower() != work.lower():
            piece = composer_idx.get(normalize_title(stripped))

    if piece is None:
        stats.unresolved_piece += 1
        stats.note_unresolved(composer, work, "piece-not-found", itunes_track.get("name"))
        return []

    piece_title = display_title(piece, include_nick_sub=True)

    if not mvmt_str:
        # Whole-piece ref.
        stats.resolved += 1
        return [{"composer": composer, "piece_title": piece_title}]

    refs = []
    any_subpiece_unresolved = False
    for seg in split_compound_movements(mvmt_str):
        n, _rest = parse_movement_number(seg)
        if n is None:
            any_subpiece_unresolved = True
            continue
        sp = find_subpiece(piece, n)
        if sp is None:
            any_subpiece_unresolved = True
            continue
        refs.append({
            "composer":      composer,
            "piece_title":   piece_title,
            "subpiece_path": [subpiece_label(sp)],
        })

    if not refs:
        stats.unresolved_subpiece += 1
        stats.note_unresolved(
            composer, work,
            f"subpiece-not-found (mvmt='{mvmt_str}')",
            itunes_track.get("name"),
        )
        return []

    if any_subpiece_unresolved:
        # Partial match: some segments resolved, some didn't. Still emit
        # what we have — better than nothing. Note it for the log.
        stats.note_unresolved(
            composer, work,
            f"partial-subpiece (mvmt='{mvmt_str}')",
            itunes_track.get("name"),
        )

    stats.resolved += 1
    return refs


# ── Album construction ─────────────────────────────────────────────────────

def build_album(raw_album, piece_index, stats):
    discs = []
    for raw_disc in raw_album["discs"]:
        tracks = []
        for rt in raw_disc["tracks"]:
            refs = resolve_refs(rt, piece_index, stats)
            track = {"track_number": rt.get("track_number")}
            dur = ms_to_duration(rt.get("total_time_ms"))
            if dur:
                track["duration"] = dur
            if refs:
                track["piece_refs"] = refs
            else:
                track["description"] = rt.get("name") or "(unknown)"
            tracks.append(track)
        discs.append({"disc_number": raw_disc["disc_number"], "tracks": tracks})

    notes_parts = []
    if raw_album.get("album_artist"):
        notes_parts.append(f"Album Artist: {raw_album['album_artist']}")
    if raw_album.get("year"):
        notes_parts.append(f"iTunes Year: {raw_album['year']}")
    notes = "\n".join(notes_parts) if notes_parts else None

    album = {
        "title": raw_album["album_title"],
        "discs": discs,
    }
    if notes:
        album["notes"] = notes
    return album


# ── Main ────────────────────────────────────────────────────────────────────

def main():
    raw    = json.loads(RAW_INPUT.read_text(encoding="utf-8"))
    pieces = json.loads(PIECES_IN.read_text(encoding="utf-8"))

    piece_index = build_piece_index(pieces)
    stats = ResolutionStats()

    albums = [build_album(a, piece_index, stats) for a in raw]

    # Disambiguate same-title collisions inside this batch (iTunes sometimes
    # lists two "albums" with the same title but different album_artist when a
    # boxed set splits soloist-included discs off from the rest). A simple
    # "(N)" suffix keeps both in the file; the merge step uses title as the
    # dedup key against the existing canon, so we must avoid title collisions
    # within the batch or the second entry would be dropped.
    seen = defaultdict(int)
    for a in albums:
        t = a["title"]
        seen[t] += 1
    dup_counters = defaultdict(int)
    disambiguated = []
    for a in albums:
        t = a["title"]
        if seen[t] > 1:
            dup_counters[t] += 1
            a["title"] = f"{t} ({dup_counters[t]})"
            disambiguated.append(a["title"])

    OUTPUT.write_text(
        json.dumps(albums, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    if disambiguated:
        print()
        print("--- Disambiguated duplicate titles ---")
        for t in disambiguated:
            print(f"  {t}")

    total_tracks = sum(len(d["tracks"]) for a in albums for d in a["discs"])
    print(f"Albums:                 {len(albums)}")
    print(f"Tracks total:           {total_tracks}")
    print(f"Tracks resolved:        {stats.resolved}")
    print(f"Tracks piece-not-found: {stats.unresolved_piece}")
    print(f"Tracks subpiece-unres.: {stats.unresolved_subpiece}")
    print(f"Output:                 {OUTPUT}")

    if stats.unresolved_examples:
        print()
        print("--- Unresolved / partial examples (first 30) ---")
        for line in stats.unresolved_examples[:30]:
            print(f"  {line}")
        if len(stats.unresolved_examples) > 30:
            print(f"  ... and {len(stats.unresolved_examples) - 30} more")


if __name__ == "__main__":
    main()
