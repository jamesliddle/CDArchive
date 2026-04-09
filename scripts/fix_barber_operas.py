"""
Add subpieces to the Barber opera entries in Classical Canon pieces.json.

Vanessa (Op. 32)  — 32 tracks from "Barber Vanessa Mitropoulos"
Antony and Cleopatra (Op. 40) — 2 excerpt tracks
"""
import json, re, sys

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

# ---------------------------------------------------------------------------
# Subpieces derived from iTunes track names (prefix "Work, Op. N - NN. " stripped)
# ---------------------------------------------------------------------------

VANESSA_SUBPIECES = [
    "Act I. Potage crème aux perles",
    "Act I. No, I cannot understand",
    "Act I. Must the winter come so soon?",
    "Act I. Listen! . . . They are here . . .",
    "Act I. Do not utter a word, Anatol",
    "Act I. Yes, I believe I shall love you",
    "Act I. Who are you?",
    "Act II. And then?\u2014He made me drink",
    "Act II. No, you are not as good a skater",
    'Act II. \u201cUnder the willow tree . . .\u201d',
    "Act II. Erika, I am so happy",
    "Act II. Our arms entwined",
    "Act II. Did you hear her?",
    "Act II. Outside this house the world has changed",
    "Act II. Orchestral Interlude\u2014Hymn",
    "Act III. The Count and Countess d\u2019Albany",
    "Act III. I should never have been a doctor",
    "Act III. Here you are!",
    "Act III. At last I found you",
    "Act III. Nothing to worry about",
    "Act IV. Scene 1. Why did no one warn me?",
    "Act IV. Scene 1. Why must the greatest sorrows",
    "Act IV. Scene 1. There, look!",
    "Act IV. Scene 1. Anatol, tell me the truth!",
    "Act IV. Scene 1. Take me away",
    "Act IV. Scene 1. Grandmother!\u2014Yes, Erika",
    "Act IV. Intermezzo",
    "Act IV. Scene 2. By the time we arrive",
    "Act IV. Scene 2. For every love there is a last farewell",
    "Act IV. Scene 2. And you, my friend",
    "Act IV. Scene 2. To leave, to break",
    "Act IV. Scene 2. Goodbye, Erika",
]

ANTONY_SUBPIECES = [
    "Act I. Give Me Some Music",
    "Act III. Give Me My Robe",
]

def make_subpieces(titles):
    return [{"number": i + 1, "title": t} for i, t in enumerate(titles)]

def find_entry(pieces, catalog_number):
    for p in pieces:
        for ci in (p.get("catalog_info") or []):
            if ci.get("catalog") == "Op." and ci.get("catalog_number") == catalog_number:
                return p
    return None

def main():
    with open(DATA_FILE, encoding="utf-8") as f:
        pieces = json.load(f)

    vanessa = find_entry(pieces, "32")
    if vanessa is None:
        print("ERROR: Vanessa (Op. 32) not found"); sys.exit(1)
    if vanessa.get("subpieces"):
        print("WARNING: Vanessa already has subpieces — overwriting")
    vanessa["subpieces"] = make_subpieces(VANESSA_SUBPIECES)
    print(f"Vanessa: added {len(VANESSA_SUBPIECES)} subpieces")

    antony = find_entry(pieces, "40")
    if antony is None:
        print("ERROR: Antony and Cleopatra (Op. 40) not found"); sys.exit(1)
    if antony.get("subpieces"):
        print("WARNING: Antony and Cleopatra already has subpieces — overwriting")
    antony["subpieces"] = make_subpieces(ANTONY_SUBPIECES)
    print(f"Antony and Cleopatra: added {len(ANTONY_SUBPIECES)} subpieces")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(pieces, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("Saved.")

if __name__ == "__main__":
    main()
