# Tower run state and the bivouac anchor — save analysis

## Purpose and scope

This document records what the exported saves reveal about how the game represents an **in-progress tower run**, why quitting **away from the Waiting Room** leaves the active fighter exposed to the death/continue penalty, and how the **bivouac item** records a resume anchor so the character can continue where it was left.

It is a companion to `save_analysis.md` and was produced from the following fixtures:

| File | What it is | Run state | Confidence as reference |
|---|---|---|---|
| `data/save_current.json` | Privacy-scrubbed, early snapshot of the same account (edited: fighter HP boosted, names blanked) | At the Waiting Room | Clean-base reference |
| `data/76561197974144168.json` | Real export of account "Coop" (`/user/psnacid 76561197974144168`), taken mid-run | Inside `MET_FLR_04` / `MET_AREA_040`, **no bivouac** | Mid-run reference |
| `data/bivuac.json` | Real export of the same account ~1 h later, taken **after using the bivouac item** | Inside `MET_FLR_02` / `MET_AREA_020`, **bivouac set** | Bivouac reference |
| `data/save_progress.json` | Higher-progress, privacy-scrubbed older sample of the same account lineage (2020-era records, resumed 2026) | At the Waiting Room | Cross-check for staleness semantics |

The two 2026 fixtures belong to the same account: identical `/user/created`, identical fighter `cid`s under `/soul/deathbag/1`, overlapping `/login_bonus` day stamps, same locale settings. `save_current.json` was scrubbed for bundling (identity/country/region blanked) and was touched by earlier editor experiments (fighter HP `999999`).

Timestamps referenced below are Unix seconds; day stamps are midnight-UTC.

Labels: **confirmed** where values are shown directly by these fixtures, **inferred** where the shape/cross-references make the purpose clear but only in-game testing can prove behavior, **unknown** where the data cannot distinguish alternatives.

---

## 1. What a clean "at the base" save looks like

From `data/save_current.json` and `data/save_progress.json` (both taken at the Waiting Room):

| Marker | Clean-base value |
|---|---|
| `/soul/stgid`, `/soul/flrid`, `/soul/areaid`, `/soul/unitid` | `""` (empty) |
| `/floor/rlg/user` | empty |
| `/floor/rlg/archive` | empty |
| `/floor/pop/*`, `/floor/item`, `/floor/trbox`, `/floor/dust` | empty |
| `/soul/pause`, `/soul/pause_x/y/z`, `pause_pitch/yaw/roll`, `pause_dv` | `""` / `0.0` / `0` |
| `/continue_count` | `0` |
| per-fighter `chr.*.pause` | `""` |

There is no single "in the base" flag. Being at the base is simply the absence of any open-run record.

---

## 2. How a mid-run save is recorded

From `data/76561197974144168.json` (standing in `MET_AREA_040`):

### 2.1 Location cursor on `/soul`

| Pointer | Observed mid-run value |
|---|---|
| `/soul/stgid` | `S_MET` |
| `/soul/flrid` | `MET_FLR_04` |
| `/soul/areaid` | `MET_AREA_040` |
| `/soul/unitid` | `METRO_START_V01` (current unit formation) |
| `/soul/termid` | `TERMID_1788861600` (season the run belongs to) |
| `/soul/team_id` | `104` (not run-specific; team membership) |
| `/continue_count` (root) | `1` (continues consumed in the current run) |

`termid`/`team_id`/`tmplid` are season/team identifiers and differ between fixtures for unrelated reasons — do not treat them as run markers.

### 2.2 The run session under `/floor/rlg`

| Pointer | Mid-run | Clean base |
|---|---|---|
| `/floor/rlg/user` | `[{"flrid","areaid","arcid"}]` — one entry per floor entered since leaving base | empty |
| `/floor/rlg/archive` | matching `arcid` records with `termid`, `ref_stageid`, `ref_areaid`, `flag_result`, plus the persisted run payload | empty |

The two sides share one `arcid` GUID. The archive record contains:

- `rlg`: a large binary blob (the serialized run layout),
- `units`: readable JSON describing the floor's unit formations and their spawn points (`START_V01`, `A03_CV_V01`, `A04_CV_V01`, `GOAL_V01`, `ELEVATOR`, … with `pntid` target lists such as `MET_ZMB_TGT_00`).

`rlg.user`/`archive` **accumulate one record per floor** walked since the excursion began: `data/bivuac.json` holds `MET_FLR_01` (`723565dd-…`) and `MET_FLR_02` (`e9367396-…`) after a floor-1→floor-2 climb, while the elevator-started session in `76561197974144168.json` holds only its single `MET_FLR_04` entry. All records stay open until the player returns to the base.

### 2.3 Live floor contents scoped to the current floor/area

Populated only while the run is open and stamped with the current `flrid`/`areaid`:

- `/floor/pop/item|trbox|msr|bst` (and empty slots `mbs|ffm|vm|xzmb|xzk`)
- `/floor/item`, `/floor/trbox`, `/floor/dust`
- staged enemies in `/zombie/*` (`zmbs`, `bodylvls`, `eqpts`, `eqskls`, `mstlvls`, `pspts`, `rwds`, `flrzmbs` placements at `MET_ZMB_TGT_*`)
- `/beast/flrbsts`

### 2.4 The running fighter

`/soul/chr/chrs/1/*` — the fighter with `state: "USE"` is the one the run belongs to (Morgan, `cee7df32-…`, in this fixture), carrying current `hp`, `select_arm_slots`, and a zeroed `bloodnium_result` run tracker. `FREE`/`GUARD`/`ENEMY` states exist for other roster entries.

### 2.5 A red herring: `/soul/area_start_time`

Set to the moment the current area was entered (`07:49:15` UTC in the mid-run fixture), but **not** a clean indicator of "currently mid-run": the 2020-era base fixture still holds a July-2020 value (`1596105121`). It is only ever overwritten when entering an area, never zeroed at the base.

---

## 3. The quit penalty and how it would manifest

**Inferred.** A mid-run save without a bivouac anchor stores *no character position* anywhere (the only coordinate fields in the save are the `pause_*` ones, which are empty). It also carries an open run session (`arcid` + `rlg` blob) that cannot be cleanly continued. The observed game behavior — the character being killed when the game is not quit at the base — is consistent with the game resolving that orphaned open run on next load as a death:

- The active (`USE`) fighter is marked dead and enters the continue/revive flow.
- Look for the aftermath under `/diedchara` (a new `dchrarcs` record whose `cid` is the killed fighter, plus `hunter.dest` entries) and items/currency moved into `/soul/deathbag`, `/soul/present`, or floor drops.

None of the supplied fixtures shows a resolved penalty for the running fighter (the newest `dchrarcs` entries in `76561197974144168.json` are other/hunter characters, not Morgan's `cid`), so all three fixtures are *pre*-penalty captures.

---

## 4. The bivouac anchor — `/soul/pause`

**Confirmed.** `data/bivuac.json` shows that using the bivouac item writes **only** the `/soul/pause` cluster. Nothing else about the run record changes in kind compared to a plain mid-run save:

| Pointer | Plain mid-run | After bivouac |
|---|---|---|
| `/soul/pause` | `""` | `"PAUSE"` |
| `/soul/pause_x` | `0.0` | `-3138.208` |
| `/soul/pause_y` | `0.0` | `-1778.7708` |
| `/soul/pause_z` | `0.0` | `-359.4235` |
| `/soul/pause_yaw` | `0.0` | `-29895.0` |
| `/soul/pause_pitch`, `/soul/pause_roll`, `/soul/pause_dv` | `0.0` / `0` | unchanged |
| `/soul/chr/chrs/1/*.pause` | `""` | `""` (per-fighter pause is not used by this flow) |

All other run state (cursor, `/floor/rlg` user/archive with `arcid`, floor population, `/continue_count`) is **identical in kind** to the non-bivouac mid-run save — the pause cluster is purely an added resume anchor (marker + world position + orientation).

Notes:

- `pause_yaw` is in the engine's raw rotation units (looks like degrees×100, unclamped: `-29895` ≈ `-298.95°` ≡ `61.05°`). Reuse values from real captures rather than inventing them.
- No `BIVU*`/`PAUSE*` string tokens exist in any of the four exports beyond the literal marker value `"PAUSE"`; the item identity lives in game data, not in these fields.

### 4.1 The three observable states

| State | cursor (`stgid/flrid/areaid`) | `/floor/rlg` user/archive | `/soul/pause*` | Next-load expectation |
|---|---|---|---|---|
| Clean base | empty | empty | empty/zeros | normal |
| Mid-run, no bivouac | set | open | empty/zeros | run unresolved → death/continue penalty |
| Mid-run, bivouac | set | open | `"PAUSE"` + coords + yaw | resume at the pause point |

---

## 5. Related observation: a purchased 1-day VIP pass

Analyzing the same fixture pair (`save_current.json` → `76561197974144168.json`) for the in-game purchase of a 1-day VIP pass found a small, self-contained signature under `/soul/vip`:

| Pointer | Before | After 1-day VIP purchase |
|---|---|---|
| `/soul/vip/oneday_pass_num` | `0` | `1` — the pass is banked |
| `/soul/vip/last_use_day` | `-1` | `1788903685` (= the purchase instant, `2026-09-08 21:41:25` UTC) |
| `/soul/vip/sequence` | `0` | `1` |
| `/soul/vip/flag`, `type`, `pass_num`, `expired_time`, `automatic_renewal`, `friendship`, `rest_week_flag` | … | all unchanged (`flag` stays `0`, `expired_time` stays `0`) |

Interpretation: purchasing the pass **banks a token** (`oneday_pass_num`) and stamps `last_use_day`/`sequence`; it does **not** activate VIP in the save. The older sample (`save_progress.json`, logins flagged `is_vip: 1` for a month) shows an *applied* pass where `expired_time` = activation moment + 30 days while `flag` remains `0` — so `flag` is not a reliable "VIP active" indicator, and `expired_time > now` is the consistent active marker across samples.

---

## 6. Editing implications

**Unknowns that need an in-game test** (backup + copy first):

1. Whether the client validates the `arcid`/`rlg` blob or the pause coordinates against the server on load.
2. Whether `soul.pause` must be accompanied by a matching open `/floor/rlg` entry for the current floor.
3. Whether the pause position must be a physically reachable point or is only read as placement coordinates.

### 6.1 Recipe: make an edited mid-run save continue-able

A bare mid-run save stores **no position**, so a synthesized bivouac needs coordinates that are valid for the target floor — copy them from a real bivouac capture in the *same area* (or use that area's spawn/`START_V01` point), never invent them:

- `/soul/pause` = `"PAUSE"`
- `/soul/pause_x/y/z` = copied position
- `/soul/pause_yaw` = copied raw yaw

Keep the cursor and `/floor/rlg` records intact; clearing them converts the save to the at-base state instead (below).

### 6.2 Recipe: make an edited mid-run save look "at base"

Treats the run as abandoned (the fighter is no longer inside it) rather than continued:

- `/soul/stgid`, `/soul/flrid`, `/soul/areaid`, `/soul/unitid` → `""`
- `/floor/rlg/user`, `/floor/rlg/archive` → empty
- `/floor/pop/*`, `/floor/item`, `/floor/trbox`, `/floor/dust` → empty
- floor-scoped spawn lists (`/zombie/flrzmbs`, `/beast/flrbsts`, `/floor/zako.flrzks`, `/floor/bo.flrbos`, `/floor/mboss.flrmbss`, `/floor/ffm.flrffms`, `/floor/vm.flrvms`, `/floor/gate.flrgates`, `/floor/stamp.flrstamps`) → empty — do **not** clear the persistent collections (`zks`, `bos`, `gates`, `stamps`, …), which legitimately exist at the base
- `/continue_count` → `0`
- fighter state/HP left consistent with resting at the base

### 6.3 VIP note for the editor

The current VIP activation flow stages `flag = 1` + `expired_time = now + days`. A real in-game purchase never does that; it banks a pass and leaves `flag`/`expired_time` untouched. If the goal is to reproduce a purchase, the signature is `oneday_pass_num += 1`, `last_use_day = now`, `sequence += 1`. If the goal is an *applied* VIP, `expired_time` is the field the game's active checks appear to trust (`flag` is `0` even while active). `/soul/bag_slot` (used by the in-progress bag-expansion staging) is absent from every supplied save — bag capacity is structural (the variable-length slot rows under `/soul/deathbag/<uid>/<cid>`).

---

## 7. Recommended next experiments

1. Capture a save **after bivouac on a specific floor**, quit, reload in game, and confirm the character resumes at the recorded `pause_*` position — proves whether coordinates are validated.
2. Capture an **at-base** save, then the **same excursion's** mid-run and bivouac saves, and diff the trio to freeze the exact bivouac delta (expected: only the `/soul/pause` cluster).
3. Capture the aftermath of a forced mid-run quit (no bivouac) to record where the penalty lands (`/diedchara`, `/soul/deathbag`, hunter logs) and which fighter state results.
4. Encode whichever transition is proven as a versioned rule with tests, mirroring the three fixtures above.

See `save_analysis.md` for the broader save architecture and the project's edit-safety conventions.
