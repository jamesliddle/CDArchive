"""
capitalise_forms.py
-------------------
Applies title-case capitalisation to every `form` field in the JSON that
currently starts with a lower-case letter (i.e. the forms added by the
split_opera_titles script).

Uses the same rule as CanonFormat.TitleCase in the C# codebase:
  - Capitalise the first letter of every word
  - EXCEPT small connector words (and, or, of, in, for, the, a, an)
    when they are not the first word.

Examples:
  "aria"                  → "Aria"
  "recitative and aria"   → "Recitative and Aria"
  "chorus of the janissaries" → "Chorus of the Janissaries"
  "aria (rondo)"          → "Aria (Rondo)"
"""

import json

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

LOWER_WORDS = {"and", "or", "of", "in", "for", "the", "a", "an"}


def title_case(value: str) -> str:
    words = value.split(" ")
    result = []
    for i, word in enumerate(words):
        # Strip any leading punctuation (e.g. "(") to find the actual first letter
        stripped = word.lstrip("(")
        if stripped and (i == 0 or stripped.lower() not in LOWER_WORDS):
            # Capitalise the first alphabetic character, keeping leading punctuation
            lead = word[: len(word) - len(stripped)]
            word = lead + stripped[0].upper() + stripped[1:]
        result.append(word)
    return " ".join(result)


def fix_forms(obj) -> int:
    changed = 0
    if isinstance(obj, list):
        for item in obj:
            changed += fix_forms(item)
    elif isinstance(obj, dict):
        if "form" in obj and obj["form"] and title_case(obj["form"]) != obj["form"]:
            old = obj["form"]
            obj["form"] = title_case(old)
            if obj["form"] != old:
                changed += 1
        for v in obj.values():
            changed += fix_forms(v)
    return changed


def main():
    with open(DATA_FILE, encoding="utf-8") as f:
        data = json.load(f)

    total = fix_forms(data)
    print(f"Capitalised {total} form value(s).")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print("Saved.")


if __name__ == "__main__":
    main()
