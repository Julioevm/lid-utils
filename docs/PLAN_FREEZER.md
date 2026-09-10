# Plan: Freezer character editor

Adds a **Freezer** tab under **Save editor** for managing the player's fighters.
The screen uses an always-visible fighter list on the left and a detail workspace
on the right. All edits use the existing staged-change, review, backup, atomic
apply, and undo/reset workflow.

## Product shape

### Left column — fighters

The left column is the selection and navigation surface. Each fighter row shows:

- name, grade, and fighter class/type;
- status badge: **In use**, **Freezer**, **Defender**, **Dead**, or **Unknown**;
- roster slot when one exists;
- a compact HP indicator;
- a staged-change marker when that fighter has pending edits.

Controls above the list:

- text search by fighter name;
- status filter, defaulting to **All**;
- sort by roster order (default), name, grade, or status.

Keep the selected fighter stable while filters and staged previews refresh. If the
selection disappears, select the first visible fighter. An empty save shows a
friendly empty state rather than a blank detail panel.

### Right side — selected fighter

The right side starts with a summary header and uses three compact sections:

1. **Profile** — name, status, roster slot, fighter type, grade, limit break,
   current HP, available XP (`gain_exp`), carried currencies, body/appearance id, and character id.
2. **Stats** — level, HP, STR, DEX, VIT, STM, LUK, skill, bag, rage, and the
   corresponding bonus values.
3. **Death Bag** — capacity summary and a slot grid showing slot number, item type,
   resolved item name, equipment state, entity id, and ownership/reference warnings.

The first release should make the profile and stats useful as a joined inspector,
while only enabling edits whose rules are understood. Raw IDs remain available in
secondary text/tooltips for diagnostics.

## Save data and joins

Characters are relational records joined by `cid`; they are not safe to model as a
single JSON row.

| Purpose | Save path |
| --- | --- |
| Fighter records | `/soul/chr/chrs/<uid>` |
| Roster placement | `/soul/chr/slots/<uid>` |
| Allocated stats | `/bodyuser/<uid>` |
| Death Bag slots | `/soul/deathbag/<uid>/<cid>` |
| Equipped decals | `/soul/skl/eqskl/<uid>` |
| Active death/recovery data | `/diedchara/dchrs/<uid>` and `/soul/prison/<uid>` |

Resolve `<uid>` from `/user/uid`, falling back to `/soul/uid`; never assume it is
`1`. Preserve the JSON scalar type found in the loaded save because equivalent
fields can be strings in one save and numbers in another.

Status is derived as follows:

- `USE` -> **In use**
- `FREE` -> **Freezer**
- `GUARD` -> **Defender**
- a matching active `diedchara` record -> **Dead**
- any unrecognized state -> **Unknown (<raw value>)**

`ENEMY` is not enough on its own to assert that a fighter is dead; the active
death record is the stronger signal. Historical archive rows must not create
duplicate fighters in the list.

## Scope

### Version 1 — joined viewer and safe edits (implemented)

- List every owned fighter and join profile, roster, stat, equipped-decal, and
  Death Bag data by `cid`.
- Edit fighter name as an existing scalar value, preserving its source type and
  enforcing a conservative length/character policy once confirmed from samples.
- Show status and roster slot read-only until their legal transition rules are
  captured from controlled before/after saves.
- Show all progression stats read-only. Direct stat, grade, XP, and limit-break
  editing is deferred because their caps and derived-field relationships are not
  established.
- Show the Death Bag with human-readable names resolved through the existing item
  catalog and entity tables.
- Expand the selected fighter's Death Bag by 1, 5, or 10 slots, capped at 70,
  through a new selected-fighter operation. This reserves the final 10 slots for
  the VIP expansion, which has a separate absolute ceiling of 80 slots.
- Surface broken joins, duplicate IDs, invalid slots, unresolved entities, and
  owner/reference inconsistencies as non-destructive warnings.

### Version 2 — bounded character management (stat allocations implemented)

The six allocated body-stat levels (HP, STR, DEX, VIT, STM, and LUK) can be
edited when the selected validated `masters.db` provides an exact
`master_body_detail(type, grade, limit_break)` definition. The editor uses
`param_lv_max` as the only cap authority and refuses missing/unsupported
combinations or values outside `1..cap`; it never silently clamps an existing
save value. It stages the edited stat and the body `lvl` scalar together. The
level rule preserves unlock progression: `new lvl = original lvl + sum(draft
primary stats - original primary stats)`, so skill/bag/rage contributions remain
untouched. G7–G9 effective variants are represented by the game's grade-6
limit-break 2–4 master rows. The profile's **Available XP** field edits the
fighter's `gain_exp` scalar (the current spendable experience), validating it as
a non-negative whole number and preserving the save's source scalar type.
Bonuses, current HP, grade, limit break, skill, bag, and rage remain read-only.

Remaining bounded character-management work:

Add these only after controlled save pairs establish every companion mutation:

- move a fighter between **Freezer**, **In use**, and **Defender**;
- change roster slot with collision handling and exactly-one-active validation;
- restore/recover a dead fighter;
- equip and unequip decals per fighter.

### Version 3 — Death Bag editing (add, replace, and clear implemented)

- Add or replace a supported catalog item in a selected fighter Death Bag slot.
  This reuses the storage catalog templates but materializes the instance as
  player-owned (`owner` = `USER`, plus the active player `uid` on equipment) in
  the game's normal inventory registries, applies current timestamps, and
  normalizes the row's `site` and `arm_slot` to an unequipped state.
- Clear a selected fighter Death Bag slot, removing its instance only when no
  other reference exists; a slot is never emptied by orphaning a shared entity.
- A beast instance still receives a raw `BEAST`-owned reward mushroom, matching
  account storage.
- Structural Death Bag edits stay in the same staged review, backup, and atomic
  apply pipeline as storage operations and conflict with raw scalar edits under
  the Death Bag and entity paths.

Remaining Death Bag work:

- Move existing entities between Storage and a selected Death Bag.
- Verify the exact in-game companion fields for upgraded or cooked variants
  before creating them.

## Implementation

### 1. Core — character graph and validation

Add `CharacterInventory.cs` with:

- `CharacterInventory.Read(json)` to resolve UID and build one aggregate per `cid`;
- models for fighter profile, roster placement, body stats, status, Death Bag
  slots, equipped decals, and validation warnings;
- lookup indexes for character and entity IDs so joins are linear rather than
  repeatedly scanning arrays;
- tolerant parsing for missing sections, object/array variation, unknown enum
  values, and duplicate/malformed records;
- stable scalar pointers for editable existing fields such as fighter name.

Add a selected-fighter Death Bag operation, preferably alongside the existing
storage operations:

```text
ExpandCharacterDeathBagOperation(CharacterId, SlotCount)
```

It must resolve UID at apply time, find the bag by `cid`, preserve the existing row
shape, allocate unique sequential slot numbers, validate `SlotCount`, and enforce
the 70-row manual cap. The account-wide VIP operation alone may grow a bag from
70 to the absolute 80-row ceiling.

### 2. Data — names and safe apply

- Reuse the existing item-definition catalog for equipment, items, mushrooms, and
  beasts; expose a small resolver that accepts a bag `type` and entity `eid`.
- Extend the save apply pipeline only for structural Freezer operations. Preview
  them against the decoded JSON before staging, then apply them inside the same
  fingerprint check, verified backup, candidate verification, atomic write, and
  post-write re-read as other edits.
- Treat structural operations as conflicting with raw scalar edits under character,
  roster, Death Bag, and referenced entity paths unless the combination is proven
  safe and explicitly supported.

### 3. App — view model

Add:

- `Characters`, `VisibleCharacters`, and `SelectedCharacter`;
- filter/sort state and selection preservation;
- selected profile, stats, bag slots, equipped decals, and warnings;
- name-draft validation and staging through the existing scalar staging service;
- selected-bag expansion options and operation staging;
- per-fighter staged indicators derived from affected scalar pointers and structural
  operations.

Rebuild and resynchronize the character projection in `SetSnapshot`, reload,
reset-all, per-change undo, structural-operation undo, apply, and restore paths.
Include Freezer operations in the pending count and the existing **Staged changes**
review rather than adding a second apply button.

### 4. View

Add a **Freezer** `TabItem` after **Currency** and before **Storage** in
`MainWindow.xaml`.

Use a two-column grid:

- fixed/min-max left rail around 260–340 px for search, filter, sort, and fighter
  rows;
- flexible right workspace with the selected fighter header and Profile, Stats,
  and Death Bag sections.

Match the existing dark theme, validation messages, staged-row highlight, disabled
busy state, and keyboard accessibility. The fighter list remains visible while the
right section scrolls.

## Tests

### Core

- UID resolution from both supported paths and non-`1` keys.
- Join fighters to slots, stats, bags, and decals by `cid`, independent of array
  order.
- Status derivation, including active death records and unknown states.
- Missing/malformed sections, duplicate `cid`/`eid`, dangling references, and
  variable-size bags.
- Selected-fighter expansion, row-shape preservation, unique slots, cap behavior,
  invalid counts, and missing fighter/bag errors.

### App

- roster-order default, filters, sorting, selection retention, and empty state;
- selecting a fighter refreshes every right-side section;
- name validation, staging, undo, reload/reset synchronization, and staged badge;
- Death Bag expansion preview, review row, undo, apply, and per-fighter scope;
- catalog unavailable/unknown item fallback and warning display.

### Data/integration

- mixed safe scalar edits and selected-bag expansion;
- conflict rejection for overlapping raw character/bag edits;
- source fingerprint protection, backup verification, atomic replacement, and
  post-write verification;
- real-save smoke tests against both bundled UID and roster-size variants.

## Acceptance criteria for version 1

- Opening a supported save lists all owned fighters in roster order without
  assuming a fixed UID.
- Selecting a fighter shows its joined profile, stats, equipped decals, and Death
  Bag without duplicate or mismatched records.
- Unknown or incomplete data remains visible and produces a warning instead of
  crashing or silently disappearing.
- Renaming and selected-fighter bag expansion appear in **Staged changes**, can be
  undone/reset, and are not written before the user chooses **Apply**.
- Apply preserves the existing backup, conflict-detection, and verification
  guarantees.

## Evidence needed before later versions

Collect save pairs differing by exactly one action:

- rename one fighter;
- move one fighter Freezer -> active, active -> Freezer, and Freezer -> Defender;
- swap two roster slots;
- allocate one point to each stat and perform one level/grade/limit-break increase;
- die and recover once;
- move one weapon, armor piece, consumable, mushroom, and beast between Storage and
  a Death Bag;
- equip and unequip an item and a decal.

These diffs determine companion fields, legal transitions, ownership changes, and
timestamps without guessing from field names.
