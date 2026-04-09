"""
split_opera_titles.py
---------------------
For every leaf subpiece in an Opera piece whose title follows the pattern
"Form. First line of text", split it into separate `form` and `first_line`
fields and remove the `title` field.

Also handles "Appendix. Form. First line" items from Idomeneo by stripping
the structural "Appendix." prefix before applying the same split.

Only touches items that currently have a `title` but no existing `form` or
`first_line`.  Non-matching titles (overtures, scene labels, plain first-line
items already stored without a form prefix, etc.) are left untouched.
"""

import json, re, sys

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

# ---------------------------------------------------------------------------
# Whitelist of known musical-form prefixes (all lowercase).
# These are the values that will be stored in the `form` field.
# Spellings listed here are the *canonical* stored form; see FORM_ALIASES for
# normalisations from variant spellings found in the data.
# ---------------------------------------------------------------------------
KNOWN_FORMS = {
    # Simple forms
    "aria", "recitative", "recitativo", "chorus", "duet", "trio",
    "finale", "finale i", "finale ii", "cavatina", "quartet", "quartetto",
    "duettino", "quintet", "quintetto", "terzet", "terzettino",
    "monologue", "sextet", "lied", "arietta", "chaconne",
    "rondo", "rondò", "rondeaux", "melodrama", "vaudeville",
    "introduction", "interlude", "intermezzo", "dialogue", "dialog",
    "arioso", "canzonetta", "cavata", "romanze", "andantino", "march",
    "ballet", "dance", "minuet", "minuetto", "march",
    # Compound / qualified forms
    "recitative and aria", "recitative and duet", "recitative and chorus",
    "recitative (pantomime)", "recitative and scene",
    "aria and duet", "aria and trio", "aria and chorus",
    "duet and chorus", "chorus and solo",
    "aria with chorus", "sextet with chorus", "quintet with chorus",
    "cavatina with chorus", "march and recitative",
    "aria (rondo)", "chorus of the janissaries",
}

# Variant spellings found in the data → canonical stored form
FORM_ALIASES = {
    "recititave":  "recitative",   # typo in source data
    "rondò":       "rondo",
    "rondeaux":    "rondo",
    "quartetto":   "quartet",
    "quintetto":   "quintet",
    "recitativo":  "recitative",
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def canonical_form(raw: str) -> str:
    """Return the canonical lowercase form name, resolving aliases."""
    lo = raw.lower()
    return FORM_ALIASES.get(lo, lo)


def try_split(title: str) -> tuple[str, str] | None:
    """
    If `title` begins with a known form prefix followed by '. ', return
    (canonical_form, first_line).  Otherwise return None.

    Tries longest possible match first so that compound forms like
    'Recitative and Aria' are preferred over just 'Recitative'.
    """
    # Try matches from longest prefix to shortest to catch compound forms first
    # We look for "Prefix. " at the start (with exactly one period-space boundary)
    for form in sorted(KNOWN_FORMS, key=len, reverse=True):
        prefix = form + ". "
        if title.lower().startswith(prefix.lower()):
            first_line = title[len(prefix):]
            return canonical_form(form), first_line
    return None


def process_piece(piece: dict, in_opera: bool) -> int:
    """Recursively process subpieces; returns count of items changed."""
    cat = piece.get("instrumentation_category", "")
    is_opera = in_opera or cat == "Opera"

    changed = 0
    subpieces = piece.get("subpieces")
    if not subpieces:
        return 0

    for sp in subpieces:
        changed += process_piece(sp, is_opera)

        if not is_opera:
            continue
        # Only process items that have a title but neither form nor first_line
        title = sp.get("title", "")
        if not title or sp.get("form") or sp.get("first_line"):
            continue
        # Leaf items only (items with their own subpieces are structural)
        if sp.get("subpieces"):
            continue

        working_title = title

        # Strip structural "Appendix. " prefix if present
        if working_title.lower().startswith("appendix. "):
            working_title = working_title[len("appendix. "):]

        result = try_split(working_title)
        if result:
            form, first_line = result
            sp["form"] = form
            sp["first_line"] = first_line
            del sp["title"]
            changed += 1

    return changed


def main():
    with open(DATA_FILE, encoding="utf-8") as f:
        data = json.load(f)

    total = 0
    for piece in data:
        total += process_piece(piece, in_opera=False)

    print(f"Split {total} title(s) into form + first_line.")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print("Saved.")


if __name__ == "__main__":
    main()
