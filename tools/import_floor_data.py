#!/usr/bin/env python3
"""Convert the community "Rotations and Resources Table V2" sheet export into the
curated map-area catalog shipped in settings/map-area-info.json.

This is an offline authoring tool: it is not built or shipped with the app. Run it
when the community sheet is refreshed, review the diff, then re-run the tests.

Usage:
    python tools/import_floor_data.py "Floor_data.html" settings/map-area-info.json

See docs/map_area_info_plan.md for the column meanings and the rotation model.
"""

from __future__ import annotations

import argparse
import html as html_module
import json
import re
import sys
from collections import defaultdict

# Canonical ordering for the sheet's rotation ids (see docs/map_area_info_plan.md §3).
ROTATION_ORDER = ["0", "1", "2", "3", "4", "5", "6", "7", "B"]

NO_MATERIAL = "\u00d7"  # the sheet's "no loose materials" marker
STAMP_MARK = "\u25cf"  # the sheet's "stamp machine available" marker

SOURCE = {
    "name": "Let It Die: Rotations and Resources Table V2",
    "authors": "Oberlinx & Kaito (community)",
    "sheet": "Floor_data",
    "updated": "2021-02-08",
    "note": (
        "Community data. Rotation groups verified against the masters.db node rosters; "
        "see docs/map_area_info_plan.md."
    ),
}

# The sheet's rotation ids are grouped onto the app's five DB templates. The pairs
# (1,4) and (3,7) are identical rosters in the sheet and share the same weekday.
TEMPLATE_ROTATION = {"4HMA": "1", "D": "2", "C": "3", "B": "5", "A": "6"}

# DB area-name -> sheet area-name mismatches (normalized, see normalize()).
NAME_ALIASES = {"KOONI": "Kohni", "NANBOKUCHO": "Nabokucho"}


def normalize(name: str) -> str:
    return re.sub(r"[\s_\-]", "", name).upper()


def parse_sheet(path: str) -> list[list[str]]:
    with open(path, encoding="utf-8") as handle:
        document = handle.read()
    document = re.sub(r"<style.*?</style>", "", document, flags=re.S)
    document = re.sub(r"<script.*?</script>", "", document, flags=re.S)
    table = re.search(r"<table[^>]*class=\"waffle\"[^>]*>(.*?)</table>", document, flags=re.S)
    if table is None:
        raise SystemExit(f"No waffle table found in {path}")
    rows = re.findall(r"<tr[^>]*>(.*?)</tr>", table.group(1), flags=re.S)
    parsed: list[list[str]] = []
    for row in rows:
        cells = re.findall(r"<t[dh][^>]*>(.*?)</t[dh]>", row, flags=re.S)
        parsed.append([html_module.unescape(re.sub(r"<[^>]+>", "", cell)).strip() for cell in cells])
    return parsed


def cell(row: list[str], index: int) -> str:
    return row[index] if index < len(row) else ""


def build_areas(rows: list[list[str]]) -> list[dict]:
    # Column layout (0-based): 0=row id, 1=Floor, 2=Rot, 3=Area, 4=info, 5=YB,
    # 6=Tales, 7=Stamp, 8=Material, 9=Shop, 10=BOSS, 11=Trap, 12=Notes.
    grouped: dict[tuple, set[str]] = defaultdict(set)
    for row in rows[2:]:
        if len(row) < 4 or not cell(row, 1).strip():
            continue
        try:
            floor = int(cell(row, 1))
        except ValueError:
            continue
        name = cell(row, 3).strip()
        if not name:
            continue
        key = (
            floor,
            name,
            cell(row, 4).strip(),
            cell(row, 5).strip(),
            cell(row, 6).strip(),
            cell(row, 7).strip() == STAMP_MARK,
            cell(row, 8).strip(),
            cell(row, 9).strip(),
            cell(row, 10).strip(),
            cell(row, 11).strip(),
            cell(row, 12).strip(),
        )
        grouped[key].add(cell(row, 2).strip())

    areas: list[dict] = []
    for key, rotations in grouped.items():
        floor, name, info, yb, tales, stamp, material, shop, boss, trap, notes = key
        ordered = [value for value in ROTATION_ORDER if value in rotations]
        ordered += sorted(rotations - set(ROTATION_ORDER))
        areas.append(
            {
                "floor": floor,
                "name": name,
                "rotations": ordered,
                "info": info,
                "material": material,
                "stamp": stamp,
                "shop": shop,
                "boss": boss,
                "trap": trap,
                "yotsuyamaBionics": int(yb) if yb.isdigit() else None,
                "tales": tales or None,
                "notes": notes,
            }
        )

    areas.sort(key=lambda area: (area["floor"], normalize(area["name"]), area["rotations"]))
    return areas


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", help="Path to Floor_data.html")
    parser.add_argument("destination", help="Path to write the JSON catalog")
    args = parser.parse_args()

    rows = parse_sheet(args.source)
    areas = build_areas(rows)
    if not areas:
        raise SystemExit("No area rows were parsed; did the sheet layout change?")

    document = {
        "schemaVersion": 1,
        "source": SOURCE,
        "templateRotation": TEMPLATE_ROTATION,
        "nameAliases": NAME_ALIASES,
        "areas": areas,
    }
    with open(args.destination, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=1)
        handle.write("\n")

    print(f"Wrote {len(areas)} area rows to {args.destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
