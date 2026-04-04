"""
Fix punctuation issues in Barber entries:
- Op. 1 #3: split em-dash tempo into two separate tempo entries
- Op. 26 #4: extract "Fuga" as form, "Allegro con spirito" as tempo
- Op. 38 #2: extract "Canzone" as form, "Moderato" as tempo
"""
import json

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"


def find_barber(pieces, op_num):
    for p in pieces:
        if p.get("composer") != "Barber, Samuel":
            continue
        for ci in p.get("catalog_info", []):
            if ci.get("catalog") == "Op." and ci.get("catalog_number") == str(op_num):
                return p
    return None


def main():
    with open(DATA_FILE, encoding="utf-8") as f:
        pieces = json.load(f)

    # --- Op. 1 #3: "Molto adagio — Tempo primo" → two tempos ---
    op1 = find_barber(pieces, 1)
    mvt3 = op1["subpieces"][2]
    assert mvt3["number"] == 3
    mvt3["tempos"] = [
        {"number": 1, "tempo_description": "Molto adagio"},
        {"number": 2, "tempo_description": "Tempo primo"},
    ]
    print(f"Op. 1 #3 tempos: {mvt3['tempos']}")

    # --- Op. 26 #4: form="Fuga", tempo="Allegro con spirito" ---
    op26 = find_barber(pieces, 26)
    mvt4 = op26["subpieces"][3]
    assert mvt4["number"] == 4
    mvt4["form"] = "Fuga"
    mvt4["tempos"] = [{"number": 1, "tempo_description": "Allegro con spirito"}]
    print(f"Op. 26 #4: form={mvt4['form']}, tempos={mvt4['tempos']}")

    # --- Op. 38 #2: form="Canzone", tempo="Moderato" ---
    op38 = find_barber(pieces, 38)
    mvt2 = op38["subpieces"][1]
    assert mvt2["number"] == 2
    mvt2["form"] = "Canzone"
    mvt2["tempos"] = [{"number": 1, "tempo_description": "Moderato"}]
    print(f"Op. 38 #2: form={mvt2['form']}, tempos={mvt2['tempos']}")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(pieces, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("Saved.")


if __name__ == "__main__":
    main()
