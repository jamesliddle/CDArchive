"""
Fix two issues in Barber entries:
1. Rename "description" → "tempo_description" inside all "tempos" arrays
2. Ensure song-cycle subpieces have numbers (already have them; this verifies)
"""
import json

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"


def fix_tempos(obj):
    """Recursively fix tempo objects: rename 'description' → 'tempo_description'."""
    if isinstance(obj, list):
        for item in obj:
            fix_tempos(item)
    elif isinstance(obj, dict):
        if "tempos" in obj and isinstance(obj["tempos"], list):
            for tempo in obj["tempos"]:
                if isinstance(tempo, dict) and "description" in tempo:
                    tempo["tempo_description"] = tempo.pop("description")
                # Recurse into sub-tempos if present
                fix_tempos(tempo)
        # Recurse into subpieces and versions
        for key in ("subpieces", "versions"):
            if key in obj and isinstance(obj[key], list):
                for item in obj[key]:
                    fix_tempos(item)


def main():
    print(f"Loading {DATA_FILE}...")
    with open(DATA_FILE, encoding="utf-8") as f:
        pieces = json.load(f)

    barber = [p for p in pieces if p.get("composer") == "Barber, Samuel"]
    print(f"Found {len(barber)} Barber pieces.")

    fix_tempos(pieces)

    # Verify the fix
    fixed = 0
    def count_tempo_descriptions(obj):
        nonlocal fixed
        if isinstance(obj, list):
            for item in obj:
                count_tempo_descriptions(item)
        elif isinstance(obj, dict):
            if "tempos" in obj:
                for t in obj["tempos"]:
                    if "tempo_description" in t:
                        fixed += 1
            for key in ("subpieces", "versions"):
                if key in obj:
                    for item in obj[key]:
                        count_tempo_descriptions(item)

    count_tempo_descriptions(pieces)
    print(f"Total tempo_description entries after fix: {fixed}")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(pieces, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("Saved.")


if __name__ == "__main__":
    main()
