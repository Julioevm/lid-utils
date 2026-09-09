# Map viewer notes — per-rotation area maps with a material overlay

Research notes for a possible **read-only map view** in LidUtils. Nothing here is implemented; this
file records the data mapping, the display plan, and the caveats found while validating the idea
against the installed `masters.db`. Companion document: `map_db_analysis.md` (what the database
stores, in full).

Labels follow the other analysis docs: **confirmed** = shown directly by the database;
**inferred** = purpose is clear but untested in-game; **unknown** = not enough evidence.

---

## 1. Idea: show the map per rotation

The database holds five full map templates — `4HMA`, `A`, `B`, `C`, `D` — selected daily through
`master_area_template_term` (§5 of the analysis). Each template defines:

- **Nodes** — `master_area_connect_node` filtered by `id = template`.
  Key `(flrid, areaid)` is unique per template (892–893 rows each). A node is *one mounted area on
  one floor slot*; several nodes can share a floor (side-by-side areas, e.g. `MET_FLR_02` hosts
  both `MET_AREA_020` and `MET_AREA_022`).
- **Edges** — `master_area_connect_escalator` filtered by the same `id`, taking `dir = 0`
  (onward/upward) rows: source node `(flrid, areaid)` → target `(toflr, toarea)`. The `dir = 1`
  rows are the downward mirror and are dropped for display.

So "a map" for one rotation is a **directed acyclic graph** from `HEAD` (tower head, below F1) up
through the bands to the top floor.

Validation performed on this build (**confirmed**):

| Check | Result |
|---|---|
| Node keys unique per template | yes (892–893 rows each) |
| Edge sources present in the template's node set | 0 dangling of ~1,860 |
| Edge targets present in the template's node set | exactly 1 dangling per template (skip/flag it) |
| Dead-end rows (`toflr=''`, `ci=-1`) | 18–25 per template (ignore for display) |
| Duplicate area-pair edges to dedupe | ≤ 1 per template (edges differ only by unit) |

Edge metadata worth keeping for display: `ci` (a shortcut stair is `ci > 1`, e.g. F3-side area
`V330` reaches F7 and F10 directly), `key`/`gate` (unlock strings `KGF_*`), and whether the node is
`isdef = 1` (base corridor, present in every template) or `0` (template-only side area).

### Suggested layout

- **Y** = visible floor number: from `master_floor.sname` / `no` for the band (floors 1–10 Metro,
  11–20 Arcade, 21–30 Amusement, 31–40 Rooftop, 41–50 Hazama, Heaven 51+).
- **X** = `ofsx` from the node row (the database stores the horizontal offset of each area — this
  is the coordinate the game's own tower map uses).
- Node box shows area short id (`MET_AREA_040`) + its elevator stop (`ELV_MAIN_MET_FLR_04`) when
  present; edges are arrows; base corridors solid, template-only side areas dashed; gated edges
  dotted/annotated.
- Controls: template selector, band/floor-range filter, and a "today" shortcut that resolves the
  active template from `master_area_template_term` (`expires` vs now) to show the current map.
- Scope: MET/ARC/AMS/RFT/HZM are small (30–45 upward edges each) and easy to render. Heaven is
  large (≈750 upward edges for template D) and should render separately or by its `R00`–`R03`
  89-floor rotations.

---

## 2. Idea: overlay the materials found on each area

The database answers "what materials can be found on this floor/area" through a short, verified
chain (§8 of the analysis):

```text
area node (flrid, areaid)
  └─ master_floor.itemgenid            (or master_tmpfloor_item mirror + itemmin/itemmax)
       └─ master_item_gen rows         (itemid + weight `freq`)
            └─ master_item             (itemtype = ITTP_MATERIAL ⇒ ITMT_* crafting material)
                 └─ master_text        (MATERIAL.TXT_ITMT_* display names)
```

Because the item group is stored on the **placement row**, materials are per area node — the exact
granularity the map view shows. Side areas on the same floor are often material-specialised
(**confirmed**, examples in the analysis): on F4, base `MET_AREA_040` = iron, `V240` = wood,
`V241`/`V340` = oil, `V140` = iron, `042` = general tier-1 mix. Overlaying this on each node
automatically reflects the daily rotation, because the node roster changes with the template.

### What to render per node

- Material **family set** (iron/wood/copper/aluminum/oil/fiber + stone sets) with each family's
  tier range (`_1`…`_8`, stored as `master_item.rarity`) and weight (`freq`) from the group.
- Roll-up to the **floor slot** when several placements are merged, or leave node-level so users
  can distinguish the specialised side areas.
- Names via `master_text` (`MATERIAL.TXT_ITMT_*`, language `jpn`/`int` available) if a friendly
  label is wanted.

### Caveats found while tracing the data

- `master_floor_material` (19 rows: `flrid`, `areaid`, `mat`) is **not** crafting loot — its values
  are surface/theme names (`Moss`, `Blood`, `kinsi01`, `treeroot`, `Ivy`, `Desert`, `Hand01`).
  Role unknown; do not confuse with the material overlay.
- The `ITEM_GEN_*` groups only contain material items in the samples checked, but verify by
  filtering on `itemtype = ITTP_MATERIAL` before rendering.
- Rotated Heaven placements carry their own groups (`ITEM_GEN_61`–`67`+); only 10 of the 1,001
  placements use `refareaid` indirection, and those need the reference resolved first.
- A separate, unresolved loot layer exists in `master_floor_drop_gen` / `master_floor_drop_ptlvl`
  (per placement × drop-source category: treasure boxes, zombies, mini-bosses…, with numeric
  `grp`/`lvl`). Its numeric namespace does not match `ITEM_GEN_*`; **unknown** how to resolve it.
  It would add per-source loot detail beyond the simple material overlay.

---

## 3. Suggested scope for a first version

1. Read-only viewer for one band (e.g. Metro 1–10) across the five templates using the node +
   escalator data; render nodes/edges with the layout above.
2. Add the material chips per node from the item-gen chain.
3. Heaven and the rotated `R00`–`R03` sets as a follow-up.

All data access is read-only and needs no game-closed gating; the existing
`ReadOnlyDatabaseBrowser`-style access can be reused.
