# Map boss overlay — findings and implementation plan

Research notes and an implementation plan for showing **bosses and mini-bosses** on the
read-only tower map (Game Database → Map). Companion documents: `map_db_analysis.md` (what
`masters.db` stores about the world map) and `map_viewer_notes.md` (the viewer's data mapping).
Labels: **confirmed** = shown directly by the database; **inferred** = purpose is clear but not
proven in-game; **unknown** = not enough evidence.

Source, opened read-only (`mode=ro`), no changes made:

| | |
|---|---|
| Database | `D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db` (≈ 59 MB) |
| Method | Python `sqlite3` URI read-only connection |

The database is game data; only identifiers and the small samples needed to explain the model
are recorded here.

> **Status: implemented.** The overlay described in §7 shipped as part of the Map feature:
> `MapNode` carries the boss facts, `MapDataService` loads them through
> `src/LidUtils.Data/MapBossCatalog.cs`, `TowerBossRoutes` classifies route strings, and the
> viewer draws `BOSS` / `FFM` badges, a boss column, and boss-coloured routes (toggle:
> **Bosses** in the toolbar). Coverage: Core route tests, data-service fixture + installed-DB
> smoke assertions, and view-model tests. Remaining known limits are listed in §8.
>
> **Terminology correction (post-implementation).** `MBOSS` means **main boss**, not "mini-boss".
> The four `MBOSS1..4` are the tower's **section bosses**, and their in-game names (from the
> `master_text` `ENEMY` entries `TXT_MAXSHARP` / `TXT_COLONELJACKSON` / `TXT_MRCROWLEY` /
> `TXT_GUNKANYAMA`) are **Max Sharp, Colonel Jackson, Mr Crowley, Gunkanyama**. `STAGE_BOSS1..4`
> are the same four bosses in their section arena (MET→1, ARC→2, AMS→3, RFT→4). The viewer now
> shows the English names, uses `BOSS` (roaming or arena) and `FFM` badges, and no longer prints
> the Japanese `master_mboss.name` values. Read "mini-boss" below as "main/section boss".
>
> **Roaming badges removed.** Almost every floor can spawn a roaming section boss, so badging it
> was clutter. The viewer now only badges the section **arena** (`BOSS`) and paid Force Men rooms
> (`FFM`); the roaming spawn data (`MainBossMin/Max/Types`) is still loaded on `MapNode` and
> documented here for a possible future filter, but is no longer surfaced in the map, list,
> tooltips or details.

---

## 1. Summary

`masters.db` does specify where bosses and mini-bosses appear, but there is **not one table** —
there are three separate systems, at three different granularities. All three map cleanly onto the
viewer's existing node/edge model:

| System | Granularity | Primary tables | Renders as |
|---|---|---|---|
| **Roaming section bosses** (Max Sharp, Colonel Jackson, Mr Crowley, Gunkanyama) | per area **placement** (`flrid` × `areaid`) | `master_floor.mbs*`, `master_floor_drop_gen` (`PTGENTP_MBOSS1..4`), `master_stage_mboss`, `master_mboss` | node badge `BOSS`: section boss can spawn here (+ name where known) |
| **Paid Force-Man rooms** ("4FORCEMEN") | fixed **floors**, mounted as side areas | `master_stage_gate`, `master_gate`, `master_area_setting_unit` (`*_SMALLBOSS`) | node badge `FFM`: paid boss room + fee/difficulty |
| **Section boss arenas** | top floor of each section, dedicated area | `master_area_setting_unit` (`*_BOSS`), `master_stage_mboss` (`STAGE_BOSSn`) | node badge `BOSS`: arena for the same named boss |
| **Boss-gated routes** | per rotation **edge** | `master_area_connect_escalator.key` / `.gate` | edge styling + readable label |

The current viewer already loads the route keys and gates into `MapEdge.Key` / `MapEdge.Gate`
(and computes `IsGated`), but only prints the raw `KGF_*` string. Node-level boss data is not
loaded at all. Everything below is read-only and needs no game-closed gating.

---

## 2. Roaming section bosses

### 2.1 Presence and count — confirmed

`master_floor` (and its split mirror `master_tmpfloor_stage`) carries four mini-boss columns per
placement:

```text
mbsmin, mbsmax, mbslvlmin, mbslvlmax
```

`mbsmin`/`mbsmax` is the mini-boss **spawn count range** on that placement; `mbslvl*` is the
difficulty tier (small integers, tracking the band). Placements with a non-zero range:

| Band prefix | Placements with `mbsmin>0 OR mbsmax>0` |
|---|---:|
| `MET` | 28 |
| `ARC` | 36 |
| `AMS` | 53 |
| `RFT` | 52 |
| `HVN` | 152 |
| `HZM` | 0 |

There are no `mbs` rows for Hazama, and `S_HZM`/`S_LAS` have no `master_stage_mboss` rows: those
bands do not use this system (see §6).

Example (`master_floor`, Metro):

```text
MET_FLR_02 / MET_AREA_020  mbsmin=0 mbsmax=0   (no mini-boss)
MET_FLR_03 / MET_AREA_031  mbsmin=1 mbsmax=1   mbslvlmin=1 mbslvlmax=1
MET_FLR_03 / MET_AREA_032  mbsmin=1 mbsmax=1
MET_FLR_07 / MET_AREA_070  mbsmin=1 mbsmax=1   mbslvlmin=3 mbslvlmax=3
MET_FLR_10 / MET_AREA_101  mbsmin=1 mbsmax=1   (band-boss floor)
```

Because the column is on the placement, it automatically follows the daily rotation: a floor may
have a mini-boss on one layout and none on another.

### 2.2 Variant / drop table — confirmed where present, sparse

`master_floor_drop_gen` has a row per placement × drop category. The mini-boss categories are:

```text
PTGENTP_MBOSS1, PTGENTP_MBOSS2, PTGENTP_MBOSS3, PTGENTP_MBOSS4
```

Distinct `(flrid, areaid, type)` placements that carry one of these:

| Band | Placements | Examples |
|---|---:|---|
| `MET` | 2 | `MET_FLR_03/MET_AREA_031 → MBOOST1`, `MET_FLR_06/MET_AREA_061 → MBOOST2` |
| `ARC` | 2 | `ARC_FLR_01/ARC_AREA_011 → MBOOST3`, `ARC_FLR_06/ARC_AREA_061 → MBOOST2` |
| `AMS` | 3 | `AMS_FLR_03/AMS_AREA_031 → MBOOST3`, `AMS_FLR_06/AMS_AREA_060 → MBOOST1`, `AMS_FLR_06/AMS_AREA_061 → MBOOST2` |
| `RFT` | 5 | e.g. `RFT_FLR_02/RFT_AREA_022` |
| `HVN` | 392 | `HVN_AREA_017` on every 5th floor carries all four (`MBOSS1..4`) |

Each row has a numeric `grp` + `freq` (the drop group, not the spawn type). So the drop table
**confirms a mini-boss variant only on a handful of band placements**; most placements with
`mbsmin>0` have no `PTGENTP_MBOSS*` entry. For those, the variant must be inferred from
`master_stage_mboss`.

### 2.3 Type map — inferred (and lossy)

`master_stage_mboss` (60 rows) maps a stage + a **unit token** to a mini-boss type:

```text
pntid='MET_MBS_TGT_00' stgid='S_MET' unit='A14_BR' attp='MBSATTP_NORMAL' type='MBOSS1' freq=0
pntid='MET_MBS_TGT_00' stgid='S_MET' unit='A17_BR' attp='MBSATTP_NORMAL' type='MBOSS2' freq=-1
pntid='MET_MBS_TGT_00' stgid='S_MET' unit='A27_GRP' attp='MBSATTP_PLAYER' type='MBOSS3' freq=-1
pntid='MET_MBS_TGT_00' stgid='S_MET' unit='BOSS'   attp='MBSATTP_NORMAL' type='STAGE_BOSS1' freq=-1
```

- `unit` is a level-chunk token (`A14_BR`, `A18_ST`, `BOSS`, …). The full unit names in
  `master_area_setting_unit` are prefixed per stage (`METRO_A14_BR`, `ARCADE_...`, `AMUSEMENT_...`,
  `ROOFTOP_...`, `HEAVEN_...`).
- `attp` is `MBSATTP_NORMAL` (a roaming/normal spawn point) or `MBSATTP_PLAYER` (a player-triggered
  encounter). Meaning **inferred**.
- `freq` is `100` (fixed), `0` (random/none) or `-1` (special); exact semantics **unknown**.
- `path` names a window/door spawn (`MET_STGBOSS_WND_01`, `MET_A18_WND_00`); mostly empty.

`master_mboss` (8 rows) stores a Japanese `name` per type. Those names describe the enemy
archetype, not the character, so the viewer maps the type id to the boss's English in-game name
(`master_text` `ENEMY` rows) instead:

| id | `master_mboss.name` (jpn) | Viewer name (from `master_text` ENEMY) |
|---|---|---|
| `MBOSS1` / `STAGE_BOSS1` | 聴力強化型 / ボス聴力強化型 | **Max Sharp** (`TXT_MAXSHARP`) |
| `MBOSS2` / `STAGE_BOSS2` | 視力強化型 / ボス視力強化型 | **Colonel Jackson** (`TXT_COLONELJACKSON`) |
| `MBOSS3` / `STAGE_BOSS3` | 捕食強化型 / ボス捕食強化型 | **Mr Crowley** (`TXT_MRCROWLEY`) |
| `MBOSS4` / `STAGE_BOSS4` | U10 / U10強化型 | **Gunkanyama** (`TXT_GUNKANYAMA`) |

`STAGE_BOSS1..4` map to the section arena (MET→1, ARC→2, AMS→3, RFT→4), so each section's arena
is the same character that can roam its floors. The `QUEST_DES`/`TXT_BEAT_MBOSS*` text that
mentions **"3 Shock Terrors COEN, JIN-DIE, GOTO-9, along with U-10"** describes the paid
**Four Force Men** rooms (`*_SMALLBOSS` areas, badge `FFM`), which are the roaming Shock Terrors
and a separate system from the section bosses.

**Caveat / join hazard.** Matching `master_stage_mboss.unit` against
`master_area_setting_unit.unit` by simple `LIKE '%'||unit||'%'` over-matches badly, because tokens
like `GOAL` and `BOSS` occur inside `METRO_GOAL`, `AMUSEMENT_SMALLBOSS`, etc. A correct join must
strip the stage prefix (`METRO_`, `ARCADE_`, `AMUSEMENT_`, `ROOFTOP_`, `HEAVEN_`, `LASTBOSS_`) and
match the remaining token at a boundary (or use the `kis` sub-table). Even then an area can
contain several chunk tokens, so a placement may be associated with **several possible** variants.
Treat per-area variant as **inferred**, and prefer the `PTGENTP_MBOSS*` value when present.

---

## 3. Paid Force-Man rooms ("4FORCEMEN")

### 3.1 Gate placement and fee — confirmed

`master_stage_gate` (16 rows) names the floor that hosts the paid room; `master_gate` (16 rows)
gives the price and difficulty label:

```text
master_stage_gate: pntid='MET_RC_TGT_00' flrid='MET_FLR_04' stgid='S_MET' unit='SMALLBOSS' gateid='GATE_FFM_WS_01' freq=100
master_gate:       id='GATE_FFM_WS_01' type='PAYMONEY' val0='440' val1='4FORCEMEN.TXT_NORMAL'
```

The real-floor rows (the 12 below) are the pay-to-enter mini-boss rooms:

| Band | Floor | Gate id | Fee (`val0`) | Difficulty (`val1`) |
|---|---|---|---:|---|
| Metro | F4 | `GATE_FFM_WS_01` | 440 | `4FORCEMEN.TXT_NORMAL` |
| Metro | F5 | `GATE_FFM_WS_02` | 4400 | `...TXT_HARD` |
| Metro | F8 | `GATE_FFM_WS_03` | 44000 | `...TXT_NIGHTMARE` |
| Arcade | F2 | `GATE_FFM_RN_01` | 440 | NORMAL |
| Arcade | F4 | `GATE_FFM_RN_02` | 4400 | HARD |
| Arcade | F7 | `GATE_FFM_RN_03` | 44000 | NIGHTMARE |
| Amusement | F2 | `GATE_FFM_BT_01` | 440 | NORMAL |
| Amusement | F6 | `GATE_FFM_BT_02` | 4400 | HARD |
| Amusement | F10 | `GATE_FFM_BT_03` | 44000 | NIGHTMARE |
| Rooftop | F2 | `GATE_FFM_PW_01` | 440 | NORMAL |
| Rooftop | F5 | `GATE_FFM_PW_02` | 4400 | HARD |
| Rooftop | F8 | `GATE_FFM_PW_03` | 44000 | NIGHTMARE |

The remaining 4 `master_stage_gate` rows have `flrid=''`, `unit='SMALLBOSS2'` (`freq=100` for
Metro/Arcade/Amusement, `-1` for Rooftop) and map to the `_04` "HELL" gate variants (`val0='0'`,
`val1='4FORCEMEN.TXT_HELL'`). With no floor of their own they are **inferred** to belong to the
`*_AREA_HVN` `SMALLBOSS2` copies mounted on F1 by the `Neo` layout.

### 3.2 Which area is the room — confirmed

The rooms are side areas whose units include a `*_SMALLBOSS` chunk
(`master_area_setting_unit`):

| Band | Boss-room areas (unit `*_SMALLBOSS`) | Mounted (template, floor) |
|---|---|---|
| Metro | `MET_AREA_V440`, `MET_AREA_V450`, `MET_AREA_V480` | `4HMA` on F4, F5, F8 |
| Arcade | `ARC_AREA_V420`, `ARC_AREA_V440`, `ARC_AREA_V470` | `4HMA` on F2, F4, F7 |
| Amusement | `AMS_AREA_V032`, `AMS_AREA_V033`, `AMS_AREA_V034` | `4HMA` on F2, F6, F10 |
| Rooftop | `RFT_AREA_V029`, `RFT_AREA_V030`, `RFT_AREA_V031` | `4HMA` on F2, F5, F8 |

The floors line up exactly with the `master_stage_gate.flrid` values. **Important:** these areas
are mounted **only by the `4HMA` rotation** (plus a `Neo`-only `*_AREA_HVN` `SMALLBOSS2` room on
F1). In the A–D rotations those floors have different side areas and no `*_SMALLBOSS` unit, so the
paid rooms are a `4HMA`-only feature in this build. The overlay therefore lights them up only on
those days — which is correct behavior, not a bug.

### 3.3 Fee text

`master_gate.val1` is a localization key `4FORCEMEN.TXT_*`; `master_text` (`sct='4FORCEMEN'`)
contains `TXT_BATTLE_FEE` ("Battle fee"), `TXT_BATTLE_ENTRY`, and the `TXT_NORMAL`/`HARD`/
`NIGHTMARE`/`HELL` labels.

---

## 4. Section boss arenas

Each themed band ends in a dedicated boss area with a `*_BOSS` unit, mounted with `isdef=1` on the
band's top floor in **every** rotation:

| Band | Boss area | Floor | Unit | Type (`master_stage_mboss`) |
|---|---|---|---:|---|
| Metro | `MET_AREA_101` | F10 | `METRO_BOSS` | `STAGE_BOSS1` |
| Arcade | `ARC_AREA_101` | F20 | `ARCADE_BOSS` | `STAGE_BOSS2` |
| Amusement | `AMS_AREA_100` | F30 | `AMUSEMENT_BOSS` | `STAGE_BOSS3` |
| Rooftop | `RFT_AREA_101` | F40 | `ROOFTOP_BOSS` | `STAGE_BOSS4` |

(`AMS_AREA_100` is the boss room; `AMS_AREA_101` is a neighbouring area on the same floor.)

The boss-area nodes already appear in the viewer, so this is just a badge. The transition into the
next band is a gated edge (see §5), e.g. `AMS_FLR_09/AMS_AREA_090 → AMS_FLR_10/AMS_AREA_101`
requires `KGF_AMS_BOSS_CLEAR`.

---

## 5. Boss-gated routes (already loaded)

`master_area_connect_escalator` (`dir=0`) carries the route's `key` (a prerequisite clear) and
`gate` (a button/door). The boss-related values are structured:

| Pattern | Meaning | Example |
|---|---|---|
| `KGF_<STG>_MIDBOSS00..02_CLEAR` | route opens after a section boss is cleared | `KGF_AMS_MIDBOSS01_CLEAR` |
| `KGF_<STG>_MIDBOSS01_02_CLEAR` | opens after two are cleared | `KGF_AMS_MIDBOSS01_02_CLEAR` |
| `KGF_<STG>_BOSS_CLEAR` | route opens after the band boss is cleared | `KGF_AMS_BOSS_CLEAR` |
| `KGF_<STG>_MB##_BTN_*` | mini-boss trigger/button | `KGF_AMS_MB01_BTN_AREA_060_GOAL` |
| `KGF_<STG>_BOSS_BTN_*` | band-boss trigger/button | `KGF_AMS_BOSS_BTN_AREA_090_GOAL` |
| `KGF_RFT_FIXED_AREA_BOSS_0001..0005` | Rooftop fixed boss keys | `KGF_RFT_FIXED_AREA_BOSS_0002` |

Amusement (F21–30) uses `MIDBOSS00/01/02` + `BOSS`; Rooftop (F31–40) uses the
`FIXED_AREA_BOSS_*` family. Metro and Arcade instead gate on `KGF_*_BTNGT_*` / `KGF_*_GATEKEY_*`
(ordinary buttons/keys, not boss clears). These strings reach the viewer today unchanged:

```csharp
// src/LidUtils.Core/MapModels.cs
public sealed record MapEdge(..., int Ci, string Key, string Gate)
{
    public bool IsGated => !string.IsNullOrWhiteSpace(Key) || !string.IsNullOrWhiteSpace(Gate);
}
```

---

## 6. Bands without this data (recorded for scope)

- **Hazama (`S_HZM`, F41–50):** no `mbs*` values, no `master_stage_mboss` rows, no `*_BOSS` unit,
  no gate/key strings on its escalator rows. The final bosses are not represented through these
  tables (or are `S_LAS`); the overlay will correctly show nothing here.
- **Last boss (`S_LAS`):** 5 node rows, one area `LAS_AREA_011` with `LASTBOSS_*` dummy/cutscene
  units. It sits at floor 41 alongside Hazama. Could be badged as "last boss" later.
- **Heaven (`S_HVN`, F51+):** a separate boss set, currently out of scope for the viewer. The
  data is there for a later iteration:
  - `HVN_AREA_003..006` = `STAGE_BOSS1..4`
  - `HVN_AREA_007..010` = `MBOSS1..4`
  - `HVN_AREA_011..015`, `HVN_AREA_018..022` = `RUSH1..10`
  - `master_ref_boss_area_setting` (1,164 rows, all `HVN_*`) resolves which of those areas a
    rotated floor uses; `HVN_AREA_017` (every 5th floor) carries all four `PTGENTP_MBOSS*` drops.
  - The plain `master_stage_mboss` S_HVN rows only expose the `BOSS` unit, so the area identity
    must be resolved through `master_ref_boss_area_setting` / `master_ref_area_setting`.

---

## 7. Implementation plan

### 7.1 Core model (`src/LidUtils.Core/MapModels.cs`)

Extend `MapNode` with boss facts (default to "none" so existing construction sites and tests stay
valid):

```csharp
public sealed record MapNode(..., double OfsX)
{
    // ... existing members ...

    /// <summary>Roaming mini-boss spawn count range (master_floor.mbsmin..mbsmax); 0,0 = none.</summary>
    public int MiniBossMin { get; init; }
    public int MiniBossMax { get; init; }

    /// <summary>Possible Shock Terror variants for this area (MBOSS1..4), inferred from
    /// master_stage_mboss; empty when unknown.</summary>
    public IReadOnlyList<MapBossType> MiniBossTypes { get; init; } = [];

    /// <summary>True for the paid Force-Man (4FORCEMEN) room area (contains a *_SMALLBOSS unit).</summary>
    public bool IsForceManRoom { get; init; }

    /// <summary>Paid gate for this floor (master_stage_gate + master_gate), when present.</summary>
    public MapBossGate? ForceManGate { get; init; }

    /// <summary>True for the band-boss room area (contains a *_BOSS unit).</summary>
    public bool IsStageBossRoom { get; init; }

    /// <summary>Band-boss type for this stage (STAGE_BOSS1..4), when known.</summary>
    public MapBossType? StageBoss { get; init; }
}
```

New small records:

```csharp
/// <summary>One mini-boss / stage-boss type (master_mboss).</summary>
public sealed record MapBossType(string Id, string Name);

/// <summary>A paid Force-Man gate: master_stage_gate + master_gate.</summary>
public sealed record MapBossGate(string GateId, int Fee, string DifficultyKey, string DifficultyLabel);

/// <summary>Classification of an escalator key/gate string.</summary>
public enum MapBossRouteKind { None, MiniBossClear, StageBossClear, BossTrigger, KeyOrButton }

public sealed record MapBossRoute(MapBossRouteKind Kind, string Label);
```

And classify edges. Rather than mutate `MapEdge`, add a computed helper (kept in Core so it is
unit-testable):

```csharp
public static class TowerBossRoutes
{
    /// <summary>Classifies KGF_* key/gate strings into a friendly boss route description.</summary>
    public static MapBossRoute Classify(string key, string gate);
}
```

Classification rules (regex over the confirmed patterns above):

- `KGF_(?<stg>\w+)_MIDBOSS(?<n>\d{2})(?:_(?<n2>\d{2}))?_CLEAR` → `MiniBossClear`
- `KGF_(?<stg>\w+)_BOSS_CLEAR` / `KGF_RFT_FIXED_AREA_BOSS_\d+` → `StageBossClear`
- `KGF_(?<stg>\w+)_(MB\d{2}|BOSS)_BTN_` → `BossTrigger`
- anything else with a key/gate → `KeyOrButton`

### 7.2 Data loading (`src/LidUtils.Data/MapDataService.cs`)

Add read-only lookups alongside the existing loaders and keep the whole load in one read-only
connection:

1. `LoadMiniBossNamesAsync` → `master_mboss` (`id`, `name`) into `Map<string, MapBossType>`.
2. `LoadPlacementBossAsync` → `master_floor` (`id`, `areaid`, `mbsmin`, `mbsmax`) for placements
   with a non-zero range, plus the `master_floor_drop_gen` `PTGENTP_MBOSS%` variant where present.
   Key by `flrid + "\x1f" + areaid`.
3. `LoadBossAreaKindsAsync` → `master_area_setting_unit` joined with `master_floor_drop_gen` /
   unit tokens to mark areas as Force-Man room (`*_SMALLBOSS`) or stage-boss room (`*_BOSS`).
   Prefer to detect these from the unit name suffix (`_SMALLBOSS`, `_BOSS`) rather than
   `master_stage_mboss`, to avoid the over-match hazard in §2.3.
4. `LoadForceManGatesAsync` → `master_stage_gate` + `master_gate`, keyed by `flrid`.
5. `LoadStageBossAsync` → `master_stage_mboss` rows with `type LIKE 'STAGE_BOSS%'`, keyed by
   `stgid`; join `master_mboss` for the display name.
6. `LoadMiniBossVariantsAsync` → `master_stage_mboss` `MBOSS%` rows, joined to the area's units
   with a **strict** match (strip the stage prefix, compare the remaining token exactly). Store
   the set per `(stgid, areaid)`. Mark as inferred in the tooltip.

`LoadAsync` must add a `HasColumns` guard for the new tables and degrade gracefully (warn, do not
fail) when any are missing, matching the existing warnings pattern.

### 7.3 View model / UI (`src/LidUtils.App`)

- `MapNodeItem`: add `HasMiniBoss`, `HasForceManRoom`, `HasStageBoss`, `IsBossNode`, and a
  `BossLabel` for the marker. Reuse the existing `ToolTipText` builder to append boss lines.
- `MapEdgeItem`: add `BossRouteKind` and use it for styling (e.g. distinct dash/colour for
  `MiniBossClear` / `StageBossClear` vs. ordinary gated edges). Keep the current "gated" styling
  as the fallback.
- `MapAreaRow`: add a `BossLabel` column so the list view can show a boss tag.
- Tooltips:
  - Node: `Mini-boss spawn: 1–1 (Shock Terror 1)`, `Four Force Men room · 440 KC (NORMAL)`,
    `Band boss: Shock Terror`.
  - Edge: replace `key KGF_... / gate KGF_...` with the friendly label from
    `TowerBossRoutes.Classify`.
- Details panel (`BuildDetails`): add a "Bosses" block with the same info.
- Toolbar: an optional **"Show bosses"** toggle and/or a legend; optionally a filter that dims
  non-boss nodes. Start with a legend + badges only (smallest diff), add the filter later.
- Drawing: a small marker (skull/star) offset from the node, or a ring around boss nodes. Keep it
  inside the existing `Canvas` drawing in `MapView.xaml.cs`; no new dependencies.

### 7.4 Tests (`tests/LidUtils.Core.Tests`, `tests/LidUtils.Data.Tests`)

- `TowerBossRoutes.Classify` table test over the confirmed strings in §5, including
  `MIDBOSS01_02_CLEAR`, `RFT_FIXED_AREA_BOSS_0002`, and a plain `GATEKEY` string.
- `MapDataService` against the optional real DB (existing `LID_UTILS_SMOKE_DB` pattern): when
  present, assert the known Metro facts — `MET_FLR_04/MET_AREA_V440` is a Force-Man room with
  fee 440/NORMAL on `4HMA` and absent on `A`; `MET_FLR_03/MET_AREA_031` has `MiniBossMin=1`;
  `MET_FLR_10/MET_AREA_101` is a stage-boss room.
- Fixture/synthetic tests for the loader's strict unit-token matching, including the `GOAL` /
  `BOSS` over-match regression.

### 7.5 Documentation

- This file is the plan of record; its status note above tracks what shipped.
- The `map_db_analysis.md` boss pointer and the README Map paragraph link here.

---

## 8. Open questions / unknowns

- `master_stage_mboss.freq` (`100` / `0` / `-1`) and `attp` (`MBSATTP_NORMAL` vs `MBSATTP_PLAYER`)
  semantics — exact in-game effect untested.
- How the runtime picks a Shock Terror variant when several chunk tokens are present in one area.
- Whether the `4HMA`-only mounting of `*_SMALLBOSS` rooms is intended or an artifact of this
  build; in-game confirmation needed.
- Whether `master_floor.mbs*` and the split `master_tmpfloor_stage.mbs*` are both authoritative
  (they are kept in sync here, as with the rest of the floor/tmpfloor split).
- Heaven's `RUSH1..10` / ref-based boss selection is deliberately deferred.
