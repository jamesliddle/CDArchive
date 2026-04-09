"""
Restructure opera subpieces from a flat list with "Act N. [Scene M.] Title"
prefixes into a proper hierarchy: Opera → Act → [Scene →] item.

Only operas where at least one subpiece title starts with an Act/Part prefix
are touched.  Operas whose subpieces have no such prefix (Il sogno di Scipione,
Bastien und Bastienne, etc.) are left unchanged.

Standalone items that appear before any act begin are kept at the top level
(e.g. an Overture).  Standalone items that appear after acts have started
(e.g. Idomeneo's "Appendix." and "Intermezzo." insertions) are placed as
direct children of the most-recently-active act.
"""
import json, re, sys

DATA_FILE = r"C:\Users\james\source\repos\CDArchive\data\Classical Canon pieces.json"

ACT_SCENE_RE = re.compile(
    r'^(Act\s+[IVX]+|Part\s+[IVX]+)\.\s+(Scene\s+\d+)\.\s*(.*)',
    re.IGNORECASE)
ACT_ONLY_RE = re.compile(
    r'^(Act\s+[IVX]+|Part\s+[IVX]+)\.\s+(.*)',
    re.IGNORECASE)


def parse_title(title):
    """Return (act_name, scene_name, remainder) or (None, None, title)."""
    m = ACT_SCENE_RE.match(title)
    if m:
        return m.group(1), m.group(2), m.group(3).strip()
    m = ACT_ONLY_RE.match(title)
    if m:
        return m.group(1), None, m.group(2).strip()
    return None, None, title


def has_act_structure(subpieces):
    return any(ACT_ONLY_RE.match(sp.get("title") or "") for sp in subpieces)


def restructure(flat_subpieces):
    """Convert flat list into hierarchical act/scene structure."""
    result = []
    current_act = None   # dict {"title": ..., "subpieces": [...]}
    current_scene = None  # dict {"title": ..., "subpieces": [...]}
    current_scene_name = None

    for sp in flat_subpieces:
        title = sp.get("title") or ""
        act_name, scene_name, remainder = parse_title(title)

        if act_name is None:
            # ── Standalone item (no Act/Part prefix) ─────────────────────────
            current_scene = None
            current_scene_name = None
            item = {"title": remainder or title}
            if current_act is not None:
                # Nest under the current act as a direct child
                current_act["subpieces"].append(item)
            else:
                # Before any act (e.g. Overture) → top level
                result.append(item)
            continue

        # ── Item that belongs to an act ───────────────────────────────────────
        if current_act is None or current_act["title"].lower() != act_name.lower():
            current_act = {"title": act_name, "subpieces": []}
            result.append(current_act)
            current_scene = None
            current_scene_name = None

        if scene_name is not None:
            # Create a new scene group when the scene changes
            if current_scene_name != scene_name:
                current_scene = {"title": scene_name, "subpieces": []}
                current_act["subpieces"].append(current_scene)
                current_scene_name = scene_name
            if remainder:
                current_scene["subpieces"].append({"title": remainder})
        else:
            # Act-level item with no scene
            current_scene = None
            current_scene_name = None
            if remainder:
                current_act["subpieces"].append({"title": remainder})

    return result


def assign_numbers(items, depth=0):
    """Recursively assign sequential 'number' to every item in a list."""
    for i, item in enumerate(items):
        item["number"] = i + 1
        if "subpieces" in item:
            assign_numbers(item["subpieces"], depth + 1)


def summarise(act_items, composer, title):
    print(f"  {composer} — {title}")
    for act in act_items:
        act_title = act.get("title", "?")
        children  = act.get("subpieces", [])
        scenes    = [c for c in children if c.get("subpieces")]
        direct    = [c for c in children if not c.get("subpieces")]
        if scenes:
            print(f"    {act_title}: {len(scenes)} scene(s), "
                  f"{len(direct)} direct item(s)")
        elif direct:
            print(f"    {act_title}: {len(direct)} item(s) (no scenes)")
        else:
            print(f"    {act_title}: (empty)")


def main():
    with open(DATA_FILE, encoding="utf-8") as f:
        pieces = json.load(f)

    changed = 0
    for piece in pieces:
        if piece.get("instrumentation_category") != "Opera":
            continue
        subs = piece.get("subpieces")
        if not subs:
            continue
        if not has_act_structure(subs):
            continue

        composer = piece.get("composer", "?")
        title    = piece.get("title", "?")

        hierarchical = restructure(subs)
        assign_numbers(hierarchical)
        piece["subpieces"] = hierarchical
        changed += 1
        summarise(hierarchical, composer, title)

    print(f"\nRestructured {changed} opera(s).")

    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(pieces, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("Saved.")


if __name__ == "__main__":
    main()
