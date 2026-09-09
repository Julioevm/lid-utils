# World map data in `masters.db` — floors, themes, connections, and the daily rotation

## Purpose and scope

This document explores what the installed game database stores about the **game world map**: the
themed floor bands, how individual floors and their alternate layouts are modelled, how floors
connect to each other, and how (and how often) the layout changes. It ends with an assessment of
whether that map data could be made **configurable** through LidUtils.

Source, opened read-only (`mode=ro`), no changes made:

| | |
|---|---|
| Database | `D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db` (≈ 59 MB) |
| Tables | 221 (excluding internal `sqlite_*` and `monitoring`) |
| Method | Python `sqlite3` URI read-only connection; schema/row/aggregate queries |

Labels used below:

- **Confirmed** — visible directly in this database and consistent across the related tables.
- **Inferred** — the schema, keys, and cross-references give a clear purpose, but only in-game
  testing proves behavior.
- **Unknown** — the data cannot distinguish alternatives without more evidence.

The database is game data; nothing here is copied wholesale, only identifiers and small samples
needed to illustrate the model.

---

## The world map at a glance

The climbable world is a **vertical tower of numbered floors**, grouped into **themed bands**
(stages). Each floor slot can host **one of several prepared map layouts** ("areas"), and the
allowed **floor-to-floor connections depend on a daily-rotating template**. The database keeps all
templates and the whole multi-year rotation calendar locally; the save file only records the
player's personal unlock/position state.

Data model (conceptual):

```text
stage (theme world, S_MET/S_ARC/…)                       master_stage
 └─ visible floor band (1–10, 11–20, …, 451 max)         master_floor.sname
     └─ floor slot (MET_FLR_04)                          master_floor.id
         └─ candidate area layouts (MET_AREA_040, …)     master_floor (one row per slot×area)
             ├─ content profile (enemy/spawn/difficulty) master_floor columns + master_tmpfloor_*
             ├─ physical chunks ("units") that make it    master_area_setting_unit (+ _setting)
             └─ vertical transitions to other areas       master_area_escalator
 template (4HMA, A, B, C, D) picks the live per-floor
   area set + allowed connections                         master_area_connect_node / _escalator
 term calendar: which template is active when            master_area_template_term
 elevator cars and their selectable stops                 master_elevator(_stop_floor)
 content reuse (rotated bands borrow other bands' data)   master_ref_area_setting / ref_boss_area_setting
```

---

## 1. Themed worlds (stages) and floor bands

`master_stage` holds the seven themed worlds. Every map entity is namespaced by a stage id and a
string prefix.

| Stage | Prefix | Name (JP) | drcat |
|---|---|---|---|
| `S_MET` | `METRO`/`MET` | 地下鉄工事現場 (subway work site) | `DRCAT_HOMECENTER` |
| `S_ARC` | `ARCADE`/`ARC` | アーケード | `DRCAT_MILITARY` |
| `S_AMS` | `AMUSEMENT`/`AMS` | 遊園地 (amusement park) | `DRCAT_FANTASY` |
| `S_RFT` | `ROOF`/`RFT` | 屋上 (rooftop) | `DRCAT_SPORT` |
| `S_HZM` | `HAZAMA`/`HZM` | 狭間 | `DRCAT_NONE` |
| `S_HVN` | `HEAVEN`/`HVN` | 天国 (heaven) | `DRCAT_NONE` |
| `S_LAS` | `LAST`/`LAS` | ラスボス (last boss) | `DRCAT_NONE` |

The visible tower floor number (`sname`, also `no`) is assigned per stage in consecutive bands
(**confirmed**): `S_MET` 1–10, `S_ARC` 11–20, `S_AMS` 21–30, `S_RFT` 31–40, `S_HZM` 41–50
(`S_LAS` also sits at 41 as a special), and `S_HVN` from 51 upward. `master_lastfloor` marks
`HVN_FLR_0401` / `HVN_AREA_016` as the last floor, so the visible climb tops out at 451 in this
dataset.

Band example from `master_floor`:

```text
MET_FLR_04  -> sname '4'   (F4)
AMS_FLR_01  -> sname '21'  (F21)
ARC_FLR_01  -> sname '11'  (F11)
HVN_FLR_0001-> sname '51'
```

The stage naming in saves matches this: the fixtures used by `run_and_bivuac_analysis.md`
refer to `S_MET`, `MET_FLR_02/04`, `MET_AREA_020/040`.

---

## 2. Floors are slots; "areas" are the actual layouts

`master_floor` (1,001 rows, 136 columns) is the core floor table. Its primary key is effectively
**(floor slot `id`, candidate `areaid`)** — one row per *placement*, not per floor:

```text
id=MET_FLR_02  areaid=MET_AREA_020   (base layout of F2)
id=MET_FLR_02  areaid=MET_AREA_022   (second base corridor on F2)
id=MET_FLR_02  areaid=MET_AREA_PAX   (event/debug layout)
```

Each row then carries two kinds of information:

1. **Identity/placement**: `id` (slot), `areaid`, `idx` (node index shared with the connect
   tables), `no`/`sname` (visible floor number), `name` (localization key such as
   `AREA_NAME.TXT_MET_0010`), `stgid`, `esc_direction`, `is_closed_area`, `fes`…
2. **Content/difficulty profile** (~120 columns of spawn tuning for that placement): zombie
   counts/levels/exp/attack-up (`zmb*`, `zk*`, `mbs*`), treasure boxes (`tb*`, `mtb*`, `ltb*`),
   vending machines (`vm*`), breakable objects (`bo*`), beasts/mushrooms, hunter/pitch data,
   shops (`shpnum`, `shopid`), stamps (`stpnum`), recovery (`rcvrymn`), co-op matching
   (`matching_group`, `matching1..4_*`), and so on. The exact meaning of every column is not yet
   decoded (**unknown** for the bulk), but the naming pattern is consistent with a spawn/difficulty
   profile per placement.

`master_tmpfloor_stage` (and `_gimic`, `_item`, `_mushroom`, `_beast`) mirror the same per-placement
profile split into one table per content category. Rows exist for exactly the same `(id, areaid)`
pairs, e.g. `AMS_FLR_01/AMS_AREA_010`. They look like the runtime-split counterpart of the merged
`master_floor` rows (which of the two the client actually reads is **unknown**; both are kept in
sync in this build).

### Area id conventions

Area ids follow recognizable patterns (**confirmed** by cross-table joins, exact meaning inferred):

| Pattern | Meaning observed | Example |
|---|---|---|
| `<STG>_AREA_0XY` | "base" corridor placed on floor slot X (hundreds/decade encode slot; the unit digit is a variant within the slot) | `MET_AREA_020`, `MET_AREA_022` |
| `<STG>_AREA_V###` | per-template side/alternate area (`V` = variant) | `MET_AREA_V103`, `AMS_AREA_V001` |
| `_TUTRIAL`, `_PAX`, `_HVN`, `_JT_Check` | tutorial / PAX demo / heaven-gate / junction-check specials | `MET_AREA_TUTRIAL` |
| `HVN_AREA_00x_R##` | rotated Heaven areas (`R00`–`R03`) | `HVN_AREA_001_R00` |

Which areas actually appear on a floor depends on the active **template** — see §5.

---

## 3. What an area is made of: units and kis tables

`master_area_setting` (263 rows) + `master_area_setting_unit` (3,245 rows) describe each area as a
list of **units** — named level chunks:

```text
stgid=S_MET areaid=MET_AREA_020 unit=METRO_START_V01  kis=''
stgid=S_MET areaid=MET_AREA_020 unit=METRO_A18_ST_V01 kis='METRO_A18_ST_V01_KIS_ETZ'
stgid=S_MET areaid=MET_AREA_020 unit=METRO_GOAL_V01  kis=''
```

Observed unit-name suffixes (`_ST`, `_CV`, `_BR`, `JT…`, `GOAL`, `START`, `ELEVATOR`, `_GRP`)
look like per-chunk roles (start/goal portals, junctions, elevator rooms…). `kis` names a spawn/
placement sub-table applied to that unit (e.g. `…_KIS_BEAST`, `…_KIS_GATE`, `…_KIS_ETZ`) —
**inferred**, not yet cross-referenced.

`master_area_setting` adds per-area overrides as JSON in `conds`/`replace_units`. Example
(`S_AMS`, all areas that also host an elevator room):

```json
"conds":         [{"key":"STAGE","value":"S_HZM,S_HVN"}],
"replace_units": [{"srcunit":"AMUSEMENT_ELEVATOR",
                   "dstunit":"AMUSEMENT_A14_CV",
                   "dstkis":"AMUSEMENT_A14_CV_KIS",
                   "conds":[{"key":"STAGE","value":"S_HZM,S_HVN"}]}]
```

This says: when the *stage* context is `S_HZM`/`S_HVN`, swap the elevator unit of this area for a
different chunk with its own `kis` set. So the same physical area shell can be re-skinned for
different stage contexts without duplicating rows (**confirmed**; 41 of 263 areas carry
`replace_units`). `dlm` (0/1) is present on every row; meaning **unknown**.

---

## 4. Connectivity: how areas and floors link

Four tables encode the vertical graph. All floors/areas/units referenced here already exist in the
area tables, so the DB is internally consistent enough to re-derive the whole tower graph.

| Table | Rows | What it stores |
|---|---|---|
| `master_area_connect_node` | 4,484 | per **template × floor slot** list of present areas (+ elevator + x offset) |
| `master_area_connect_node_TEST` | 698 | a newer/partial copy of the same model (fewer floors) |
| `master_area_connect_node_repeat_straight` | 25 | compressed "N floors in a row, straight chain" rules |
| `master_area_connect_escalator` | 9,398 | per **template**: allowed directed transitions between areas of adjacent floors |
| `master_area_escalator` | 1,814 | unit-level lower→upper escalator edges (no template column) |

### Nodes (`master_area_connect_node`)

```text
id=4HMA stgid=S_AMS flrid=AMS_FLR_01 areaid=AMS_AREA_010 elvflrid=ELV_MAIN_AMS_FLR_01 isdef=1 ofsx=0.0
id=4HMA stgid=S_AMS flrid=AMS_FLR_01 areaid=AMS_AREA_011 elvflrid=                 isdef=1 ofsx=500.0
id=A    stgid=S_AMS flrid=AMS_FLR_02 areaid=AMS_AREA_V001 elvflrid=ELV_SUB01_AMS_FLR_02_A  isdef=0 ofsx=940.0
```

- `id` = template pattern (see §5). Rows exist for the five main patterns plus special/debug
  layouts (`Tut`, `PAX`, `Neo`, `JT_Check`, `Test`, `R`–`V`).
- One row per (pattern, floor, area). Base corridors appear under every main pattern with
  `isdef=1`; template-specific side areas appear only under their pattern with `isdef=0`.
- `elvflrid` names the elevator stop that services that area (`ELV_MAIN_<slot>` for main-elevator
  floors, `ELV_SUBxx_…_<pattern letter>` for side-area elevators). The trailing letter of sub-
  elevator ids matches the pattern letter (`…_FLR_02_A` on pattern `A` rows), which confirms the
  templates have their own side elevators.
- `ofsx` = horizontal offset (negative = left of the shaft), i.e. the database knows the relative
  x-position of each area — enough to reproduce the vertical "tower map" diagram.

A concrete example of the pattern differences on one floor (`MET_FLR_05`):

```text
4HMA: MET_AREA_050, MET_AREA_051, MET_AREA_V103, MET_AREA_V450
A:    MET_AREA_050, MET_AREA_051, MET_AREA_V103
B:    MET_AREA_050, MET_AREA_051, MET_AREA_V030, MET_AREA_V150
C:    MET_AREA_050, MET_AREA_051, MET_AREA_V030, MET_AREA_V250
D:    MET_AREA_050, MET_AREA_051
```

Base areas 050/051 are identical in all templates; the **side areas and their elevator service
change per template** — that is one of the two levers of the layout rotation.

### Escalator edges

`master_area_escalator` stores physical lower→upper unit edges and includes the band transitions,
e.g. `MET_FLR_10` → `ARC_FLR_01` (F10 → F11). It has no template column, so it is the shared
"geometry graph" of the tower shaft.

`master_area_connect_escalator` is the per-template logic graph. Each row is one allowed transition
off a given floor area:

```text
id=D stgid=S_MET flrid=MET_FLR_02 areaid=MET_AREA_020 dir=0
    fromunit=METRO_GOAL_V01  ->  toflr=MET_FLR_03 toarea=MET_AREA_031 tounit=METRO_START_V01 ci=0
id=D stgid=S_MET flrid=MET_FLR_03 areaid=MET_AREA_032 dir=0
    fromunit=METRO_GOAL      ->  toflr=MET_FLR_05 toarea=MET_AREA_051 tounit=METRO_START   ci=5
id=D stgid=S_MET flrid=MET_FLR_03 areaid=MET_AREA_V330 dir=0
    fromunit=METRO_GOAL      ->  toflr=MET_FLR_10 toarea=MET_AREA_V310 tounit=METRO_START  ci=6
```

- `dir=0` rows lead onward/upward from a floor's `GOAL`; `dir=1` rows lead back down from a
  `START` (including to `HEAD`, the tower head below F1) — **inferred** from unit names.
- Rows can span multiple floors (`ci` 0–6; `ci` > 1 skips intermediate floors, i.e. shortcut
  stairs/hatches). `ci` semantics otherwise **unknown** (`-1` marks dead ends in this sample).
- `key`/`gate` name unlock conditions (`KGF_MET_GATEKEY_AREA010`, `KGF_MET_BTNGT_GL_AREA010`…), and
  `enable` is an empty string everywhere sampled. `master_gate`/`master_stage_gate` define paid
  mini-boss room gates (e.g. `GATE_FFM_WS_01` = 440 KC at `MET_FLR_04`/unit `SMALLBOSS`).

`master_area_connect_node_repeat_straight` compresses long straight chains (mostly the Heaven
band): e.g. `flrid_prefix='HVN_FLR_'`, `start_index=1`, `end_index=400`, with a small cycling
`areaids` list. Two ids (`4HMA` and `A`–`D`) both appear here, which suggests this table is the
authoring-level version of the same node model (**inferred**).

### Elevators and stops

`master_elevator` (16) lists cars — `ELV_MAIN` ("MAIN ELEVATOR", `clridx=0`) plus per-stage sub
cars (`ELV_MET_SUB1/2`, `ELV_AMS_SUB01..05`, `ELV_RFT_SUB_{A..D}01/02`, …) whose letters line up
with the template letters. `master_elevator_stop_floor` (61) lists the selectable stops per car:

```text
id=ELV_MAIN_MET_FLR_01 elvid=ELV_MAIN name=ELEVATOR.TXT_ELV_MAIN_MET_FLR_01
id=ELV_MAIN_AMS_FLR_05 elvid=ELV_MAIN …
id=ELV_SUB2_MET_FLR_03_B elvid=ELV_MET_SUB2 …   (pattern-B side elevator)
id=ELV_MAIN_HUB elvid=ELV_MAIN …                  (waiting room)
```

Main-elevator stops are a subset of floors per band (MET 1,3,4,5,6,8,9,10; AMS 1,3,5,7,10; …),
which is what the in-game elevator menu offers once the save unlocks them.

---

## 5. The rotation: templates selected by a term calendar

This is the part that answers "the layout changes periodically".

### Template ids

`master_area_connect_node` and `master_area_connect_escalator` both carry a pattern id. The five
main values are `4HMA`, `A`, `B`, `C`, `D`; each covers the same ~809 floor slots with slightly
different area sets (892–893 rows each). A few other ids (`Tut`, `PAX`, `Neo`, `JT_Check`, `Test`,
`R`–`V`) hold small debug/event layout fragments (e.g. the `PAX` demo floor `MET_AREA_PAX`, the
`Tut` tutorial area `MET_AREA_TUTRIAL`).

### The term calendar

`master_area_template_term` (4,019 rows: `termid` PK, `expires`, `tmplid`) maps a *term* to the
template active during it:

```text
TERMID_1437386400  expires=1437386400 (2015-07-20) tmplid=A
TERMID_1466157600  expires=1466157600 (2016-06-17) tmplid=B   <- start of daily rotation
TERMID_1466244000  expires=1466244000 (2016-06-18) tmplid=C
… (2015: weekly 604800s; from 2016-06-17 onward: daily 86400s) …
last term 2027-04-30
```

Facts (**confirmed**):

- Terms are pre-generated from 2015-07-20 through **2027-04-30** — a shipped schedule, not
  something derived live from a server ping.
- Cadence is **daily** (`expires` deltas of 86,400 s) for nearly the whole table (3,970 of 4,018
  gaps); the 2015 beta era used weekly gaps (47), with one 3-day transition.
- `tmplid` cycles through the five templates. Since 2024 the same pseudo-cyclic ~7-day macro
  sequence repeats (e.g. `C 4HMA D C 4HMA B A C …`), so a player sees `4HMA` and `C` about twice
  as often as `A`, `B`, `D`.

### What actually changes between templates

Two mechanisms (both **confirmed** at the data level, effect in-game inferred):

1. **Per-floor area roster** (node tables): base corridor areas are constant across templates
   (`isdef=1`); each template additionally mounts its own side areas (`V###`, `isdef=0`) with their
   own sub-elevators (`ELV_SUBxx_…_A` vs `…_B` vs `…_C` vs `…_D`).
2. **Allowed transitions** (escalator tables): the per-template logic graph gives different
   next-floor choices from the same floor, including multi-floor skips and key/gated routes
   (`ci`, `key`, `gate` columns), so the reachable route map differs from day to day.

### The Heaven band and "rotated" content copies

`S_HVN` is a large band (757 floor slots in the connect tables). Besides the plain numbered chain
(`HVN_FLR_0001…0401` → floors 51–451) there are four rotated copies, each 89 floors:

```text
HVN_FLR_R00_0001..0089  areas HVN_AREA_00x_R00
HVN_FLR_R01_…           areas HVN_AREA_00x_R01
HVN_FLR_R02_…           areas HVN_AREA_00x_R02
HVN_FLR_R03_…           areas HVN_AREA_00x_R03
```

`master_ref_area_setting` (337 rows) then points each rotated area at a *content provider* in a
different themed stage (`freq=100`):

```text
HVN_AREA_000_R00 -> MET_AREA_020, MET_AREA_022, MET_AREA_031, …   (Metro content)
HVN_AREA_000_R01 -> ARC_AREA_…                                    (Arcade content)
HVN_AREA_000_R02 -> AMS_AREA_…                                    (Amusement content)
HVN_AREA_000_R03 -> RFT_AREA_…                                    (Rooftop content)
```

In other words the high-band floors are remixed copies whose difficulty/spawn profile reuses the
lower themed bands; which of `R00`–`R03` is active follows the same template/term rotation. The
plain chain and `HZM` band reference content the same way (`master_ref_area_setting`; boss spots
via `master_ref_boss_area_setting`, 1,164 rows). This indirection is why content tables need no
duplication for rotated floors.

---

## 6. Could the map be configurable?

### What the data tells us about editability

All of the above is ordinary local SQLite in `masters.db` — structurally nothing stops an editor
from changing it. The practical question is integrity and who is authoritative:

**Integrity (mostly local, and re-derivable).** `master_floor`, `master_area_connect_node`,
`master_area_setting(_unit)`, and the `master_tmpfloor_*` tables share the same `(floor slot,
areaid)` keys (993/1,001 floor rows join to the node table). Escalator rows reference areas and
units that exist in the area tables. A change that only flips values *inside one table* on existing
rows is therefore self-consistent. A change that adds/removes rows or rewires connections must be
mirrored across several tables, and brand-new areas/units would reference level geometry that ships
with the game's assets rather than the DB, so inventing new pieces is not possible from the DB
alone.

**Who is authoritative for the live layout.** The term calendar is pre-generated years into the
future and is the only table referencing template ids outside the connect tables. Whether the
client picks the current template purely from `expires` (local time) or receives confirmation from
the service is **unknown**. The same caution applies as to all online-facing tables in the project's
existing posture (schedules, seasons, shop rotations): the game patches the DB regularly, and a
service check may simply ignore or revert a locally-edited calendar. Editing *content profiles* of a
given area is far more likely to stick in-game than editing which template the server says is live.

**Player state is separate.** Which elevator stops/floors the player has unlocked lives in the save
(`/soul/openelvflr`, `/soul/areaflag`, per `save_analysis.md`), not in `masters.db`. Re-pointing the
map DB affects the world model (what the next run can generate), not the already-recorded unlock
state.

### Suggested posture (consistent with the project plan)

Map data is **not a suitable target for unrestricted row editing**. A sensible, staged posture:

1. **Read-only map explorer (low risk, high value).** Use the schema browser already in LidUtils
   plus a map-specific viewer that can render, for a chosen template (`4HMA`/`A`/`B`/`C`/`D`) and
   stage band: the floor list, the areas per floor, and the connection graph from the escalator
   tables. Because `ofsx` and band order are stored, a simplified "tower map" can be drawn entirely
   from local data. This also validates the model against what the game shows.
2. **Curated single-table tuning.** The per-placement content profiles (`master_floor` /
   `master_tmpfloor_*`) are value-tuning tables like the existing `master_const_*` catalogs, only
   keyed by (floor slot, area). A curated catalog (e.g. "enemy level band / count / money range on
   placement X") fits the app's existing staged-edit + backup + validation workflow. Elevator fare
   is already a plain constant (`ELEVATOR_BASIC_PRICE 90`, `ELEVATOR_FLOOR_PRICE 30`,
   `ELEVATOR_DISCOUNT_RATE 30` in `master_const_int`) and can be exposed like any other constant.
3. **Template override only as an explicitly experimental, single-value edit.** Rewriting
   `master_area_template_term.tmplid` for a term (or the area roster of one floor in one template)
   is technically a one-row change, but it is the piece most likely to be server-checked or
   overwritten by patches, and a wrong value desyncs map, elevator stops, and save expectations.
   Keep it out of curated catalogs; if exposed at all it should be an "experimental/unsupported"
   action with the strongest warnings, per the project's existing rules for undocumented settings.
4. **Exclude from editing:** adding or deleting floor slots, areas, escalator edges, elevator
   stops, or Heaven rotations. These need multi-table transactional changes plus client asset
   alignment and can brick navigation; they violate the "show exactly what will change / safe to
   reverse" bar. They can still be *inspected* through the explorer.

---

## 7. Feature ideas for LidUtils (ranked)

| # | Idea | Table(s) | Risk | Notes |
|---|---|---|---|---|
| 1 | Tower-map explorer per template/band (render floors, areas, edges from `ofsx` + escalators) | node/escalator/floor tables | none (read-only) | Reuses existing read-only DB access; schema already browsable |
| 2 | Show "today's map" by resolving the current term (`expires` vs now) → template | `master_area_template_term` + connect tables | none (read-only) | Also reveals cadence/rotation stats |
| 3 | Curated difficulty tuning for a placement (enemy level/count ranges, money) with min/max validation | `master_tmpfloor_*` / `master_floor` | medium | Existing-row value edits only, staged review, backup-first |
| 4 | Elevator/economy constants into the constant catalogs | `master_const_int` | low | `ELEVATOR_*`, `HEAVEN_NEO_ROUTE_OPEN_FLOOR` etc. |
| 5 | (Experimental, gated) template override for a term | `master_area_template_term` | high | Flag experimental; verify against server behavior first |
| 6 | Material/loot overlay per area node ("farm planner") | `master_floor.itemgenid` → `master_item_gen` → `master_item` | none (read-only) | See §8; renders which materials an area yields with weights/tiers |

---

## 8. Floor materials and loot tables

There is no single "materials per floor" table, but the floor → materials chain is short and fully
joinable (**confirmed** end-to-end on this build):

```text
placement (floor slot × area, master_floor / master_tmpfloor_item)
   ├─ itemgenid  →  master_item_gen  (items + weight per group)   e.g. ITEM_GEN_02
   │                   └─ master_item (itemid, type, rarity)      e.g. ITMT_IRON_1
   │                       └─ master_text (MATERIAL.TXT_*_ITMT_*)  display names
   └─ itemmin / itemmax   spawn-count range for loose drops
```

Material items are `master_item` rows of `itemtype = ITTP_MATERIAL` (`ITMT_*`): 106 items across
16 families — `ITMT_IRON`, `ITMT_WOOD`, `ITMT_COPPER`, `ITMT_ALUMI`, `ITMT_OIL`, `ITMT_FIBER`
(8 tiers each) plus the stone/steroid sets used by later content (`STONE_MIL`, `STONE_SPO`,
`STONE_DIY`, `STONE_FAN`, `STONE_JAC(_X/Y/Z)`, `STONE_TBR`, `STEROID`). `rarity` is the tier.
`master_item_gen` holds 82 groups (`ITEM_GEN_00`…`81`); floors/areas reference them by `itemgenid`.

Group contents are weighted (`freq`), and each placement picks one group. Two group styles are
visible: single-family groups (e.g. `ITEM_GEN_02` = iron `_1` 95% + iron `_2` 5%) and mixed
groups by tier (e.g. `ITEM_GEN_00` = all six base materials `_1`; `ITEM_GEN_12` = their `_3`
versions). Because the group sits on the **placement** row, materials are per area, not per floor
number — and the alternate layouts on one floor are often material-specialised. Real examples from
`S_MET` (each row is one placement of the floor):

```text
MET_FLR_02 / MET_AREA_020 → ITEM_GEN_30 (all six base materials, tier 1)
MET_FLR_02 / MET_AREA_022 → ITEM_GEN_03 (aluminum, tier 1–2)
MET_FLR_03 / MET_AREA_031 → ITEM_GEN_31 (fiber, tiers 1–5)
MET_FLR_03 / MET_AREA_V130 → ITEM_GEN_02 (iron, tier 1–2)
MET_FLR_03 / MET_AREA_V230 → ITEM_GEN_03 (aluminum, tier 1–2)
MET_FLR_04 / MET_AREA_V240 → ITEM_GEN_01 (wood, tier 1–2)
MET_FLR_04 / MET_AREA_V241 → ITEM_GEN_04 (oil, tier 1–2)
MET_FLR_04 / MET_AREA_V340 → ITEM_GEN_04 (oil, tier 1–2)
```

For the map feature this is ideal: the area nodes already differ per template, so overlaying the
material group/weights on each node automatically follows the daily rotation. Rotated Heaven
placements carry their own groups (`ITEM_GEN_61`–`67` and beyond); only 10 of the 1,001 placements
use `refareaid` indirection instead.

Related but separate tables:

- `master_floor_material` (19 rows: `flrid`, `areaid`, `mat`) — not crafting loot. Values are
  surface/theme names (`Moss`, `Blood`, `kinsi01`, `treeroot`, `Ivy`, `Desert`, `Hand01`…). Role
  unknown (possibly the area image/backdrop theme shown in the map panel).
- `master_floor_drop_gen` (83,970 rows) and `master_floor_drop_ptlvl` (10,260 rows) — per placement
  and **drop-source category** (`PTGENTP_TRBOX_L/M/S`, `PTGENTP_ZOMBIE`, `PTGENTP_MBOSS1..4`,
  `PTGENTP_SPL/SPM/SPXL`, `PTGENTP_TRZAKO`, `PTGENTP_HATER_L/M`, `PTGENTP_TRBOX_SPXL_RARE`) with
  numeric `grp`/`lvl` and `freq`. This is a second, finer-grained loot layer (treasure-box and
  kill drops). The numeric `grp` namespace does **not** match `ITEM_GEN_*` ids and is not yet
  resolved (**unknown**); it may reference `master_item.grp` groups or another reward table.

---

## 9. Open questions and unknowns

- `ci` (-1…6) and `dir` semantics in `master_area_connect_escalator`; `dlm`; `enable` column.
- Whether the active template is resolved locally from `expires` or confirmed by the service, and
  whether patching rewrites the term calendar.
- Which content set the client actually reads between `master_floor` and the `master_tmpfloor_*`
  split copies.
- The role of the `R00`–`R03` Heaven copies vs the plain numbered chain, and the exact mapping of
  `repeat_straight` area-id lists to floor indices.
- What the numeric `grp`/`lvl` columns in `master_floor_drop_gen` / `master_floor_drop_ptlvl`
  reference, and which physical spawns (treasure boxes, kills, boss rewards) they feed.
- The role of `master_floor_material` surface names (visual theme vs map-panel art).
- Meaning of the unit-name suffixes (`_ST`, `_CV`, `_BR`, `_GRP`) and the `kis` spawn tables.
- The `_TEST` node table and the special template ids (`Neo`, `PAX`, `Test`, `R`–`V`).
