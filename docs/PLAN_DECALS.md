# Plan: Decals (Skills) section for the save editor

Adds a **Decals** tab to the save editor. The tab lists every decal (called "skills"
in-game) the player owns, resolves display names and metadata from `masters.db`,
and lets the owned quantity be edited through the normal staged-change pipeline.

## Data findings

### Save side — `/soul/skl/psskl`

Array of owned decal rows, one row per decal type the player has ever owned:

```json
{"sklid":"SKL_ATKUP_01_P","cnt":1,"updated":1788359077,"is_checked":0}
```

- `sklid` joins to `master_skill.id` in the game database.
- `cnt` is the owned quantity of that decal type (0–3 observed in real saves).
- Unowned decal types have **no row at all**. Granting a never-owned decal requires
  structural array insertion and is out of scope for this feature.
- The scalar scanner already flattens each row to pointers such as
  `/soul/skl/psskl/12/cnt`, so quantity edits are plain scalar stages handled by the
  existing staging, review, backup, and apply pipeline. `psskl` is **not** on the
  `IsStorageConflictPointer` blocklist in `SaveFileService`, so scalar edits are safe.

### Save side — `/soul/skl/eqskl` (equipped decals)

Map keyed by loadout/bag id (`"1"`, `"424242"`, negative keys are empty loadouts).
Each value is a list of equipped decals per fighter:

```json
{"424242":[
  {"cid":"a724b7b7-…","sklid":"SKL_DRAIN_01_P","slot":0},
  {"cid":"a724b7b7-…","sklid":"SKL_HPUP_01","slot":3}
]}
```

- **Equipped count per decal type** = number of entries with that `sklid`, summed
  across every loadout key.
- `eqskl` pointers **are** on the storage-conflict blocklist; this feature only
  *reads* them, which is unaffected.
- Real saves can hold `cnt = 0` for a decal that is currently equipped. Validation
  must therefore constrain **edits**, never flag pre-existing save states.

### Database side — `masters.db` `master_skill`

| Column | Use |
| --- | --- |
| `id` | Joins to `sklid` |
| `name` | Localized via the existing `master_text` join in `ItemCatalogService.Localized()` |
| `premium` | Premium decal flag (the `_P` id suffix correlates but the column is authoritative) |
| `rarity` | Rarity (drives the default sort) |
| `type` / `category` | Type label |
| `platform` | Skip rows with `platform != 0`, consistent with the other loaders |

## Scope

### In scope (v1)

- List **owned** decals only, joined with `master_skill` definitions.
- Edit owned quantity (`cnt`) via staged scalar changes (min **0**, max **4**).
- Filter: text search, **premium only** toggle.
- Sort: by rarity (default, descending), plus name.
- Display equipped count per decal type.
- Validation: edits must be whole numbers between 0 and the cap of 4. Owned and
  equipped decals are independent in-game, so the equipped count never constrains
  the owned quantity; it is displayed for information only.

### Out of scope (phase 3+)

- Removing ownership (deleting `psskl` rows reindexes the array and would conflict
  with scalar-pointer mixing; needs storage-style conflict guards).
- Editing `updated` / `is_checked`.
- Equipping/unequipping or per-fighter loadout editing; v1 shows only the aggregate
  equipped count per decal type (fighter names are a later addition).

## Implementation

### 1. Core — models and inventory reader (`LidUtils.Core`)

New `DecalInventory.cs`, following the `StorageEngine` pattern:

- `DecalOwnedRow`: `Index` (array position), `SkillId`, `Count`, `Updated`,
  `IsChecked`, `CountPointer` (`/soul/skl/psskl/<i>/cnt`).
- `EquippedDecalEntry`: `SkillId`, `FighterId` (`cid`), `Slot`, `LoadoutKey`.
- `DecalInventory.Read(string json)` parses both `psskl` and `eqskl` from the
  snapshot JSON; returns rows plus `EquippedCountBySkill` (per-`sklid` totals).
- New `DecalDefinition` record: `SkillId`, `DisplayName`, `Premium`, `Rarity`,
  `TypeLabel`.

### 2. Data — decal catalog loading (`LidUtils.Data`)

- Extend `ItemCatalogService` (or add a sibling loader) to read `master_skill` with
  the existing `Localized()` name join; surface `premium`, `rarity`, `type`.
- Skip `platform != 0` rows; report schema gaps through the existing warnings list.
- Keep decals in a dedicated result type — `ItemCatalogCategory` models storage
  families and should not be extended with a decal member.

### 3. App — view model (`SaveEditorViewModel.cs`)

- `Decals` collection of `DecalCollectionRow` (owned row + resolved definition).
- Row fields: rarity stars, name, type, premium badge, equipped count, current
  quantity, new-value draft, validation error, staged flag, undo.
- Filter state: `DecalSearch`, `IsPremiumOnly`; sort state with rarity-descending
  default. Rebuild the view on filter/sort changes.
- Qty edit path: validate (whole number, `>= equippedCount`, `<= 4`), then
  `_staging.Stage(row.Entry, count)` on the row's `CountPointer`; empty/invalid
  drafts clear the staged change, mirroring `StageFieldDraft`.
- Wire into the existing lifecycle: build rows in `SetSnapshot`, clear in `Clear`,
  resync in `ResetAllChanges` / `RefreshPendingChanges` / `SyncValueRowFromStaging`
  (same shape as the `Currencies` list).
- Load decal definitions inside the existing `ConfigureStorageCatalogAsync`
  database-validation path; without a database the tab still lists rows using
  `sklid` with unknown rarity (sorted last) and no premium/type metadata.
- Staged decals appear in the existing Staged changes review and Apply flow with no
  pipeline changes.

### 4. App — view (`MainWindow.xaml`)

- New **Decals** `TabItem` after Storage in the save editor tab control.
- Toolbar: search box, "Premium only" `CheckBox`, sort selector, summary count.
- `DataGrid`: Rarity ★ | Decal | Type | Premium | Equipped | Current qty |
  New value | Undo — matching the existing dark theme, staged-row highlight, and
  busy-state enablement patterns.

### 5. Tests

- **Core**: `psskl` parsing (multi-row, empty, missing arrays), `eqskl` aggregation
  across loadout keys, pointer correctness, malformed-JSON errors.
- **Data**: decal loader against a fixture database — name resolution, premium and
  rarity surfacing, platform filter, warning paths.
- **App**: filter and sort behaviour (premium toggle, rarity order, unknown rarity
  last), quantity staging and validation (cap 4, equipped floor, invalid input,
  revert to original), undo, staged-state resync on reload/reset, no-database
  fallback display.

## Decisions

- Quantity cap is a hard **4** (game limit for stacked decals).
- Only owned decals are listed in v1; "not owned" placeholder rows are not shown.
- Equipped decals are displayed but not editable in v1.
- Owned and equipped decals are treated independently by the game: lowering an
  owned count does not unequip anything. There is therefore no equipped floor on
  quantity edits; the equipped count is informational only.

---

# Phase 2 plan — full decal list and granting unowned decals

Show the **entire** decal list from `masters.db` in the Decals tab and let the
player grant ownership of never-owned decals. Status: planned, not implemented.

## Decisions

- Full list = catalog decals ∪ owned rows; owned-but-unknown-id rows keep id-only
  display, catalog decals the player does not own show a Grant control.
- Grant quantity is **selectable 1–4** inline per row (default 1).
- Granting is surfaced as an **inline Grant button per unowned row** (no picker
  dialog).
- **Grants only** — removing ownership stays out of scope (array reindexing would
  break scalar-pointer mixing and needs storage-style conflict guards).
- Grants append to the end of `psskl`, which does not reindex existing rows, so
  grants and scalar `cnt` edits may be applied in the same apply operation (no
  storage-conflict-style guard is required).
- New rows are written as `{"sklid":"…","cnt":N,"updated":<now>,"is_checked":0}`;
  `is_checked = 0` and `updated = current unix seconds` match real-save patterns.
- A grant for a `sklid` already present in `psskl` is rejected defensively in the
  Core engine even though the UI hides the button for owned rows.

## Implementation

### 1. Core (`DecalInventory.cs`)

- `GrantDecalOperation` record: `SkillId`, `Quantity` (1–4).
- `DecalEngine.Apply(string json, IReadOnlyCollection<GrantDecalOperation> grants)`
  mirroring `StorageEngine.Apply` (JsonNode-based): locate `soul.skl.psskl`
  (create `skl`/`psskl` nodes when missing), reject duplicate `sklid` across the
  existing rows and within the batch, append
  `{"sklid","cnt","updated":<now>,"is_checked":0}`, serialize back.
- Small `DecalInventory` helper (e.g. `ContainsSkill`) for UI/staging checks.

### 2. Data

- No changes; `ItemCatalogService.LoadDecalsAsync` already returns the full
  platform-0 catalog.

### 3. Save pipeline (`SaveFileService.ApplyAsync`)

- Accept decal grant operations as an additional collection and apply them after
  scalar replacements and storage operations
  (scalar spans → `StorageEngine.Apply` → `DecalEngine.Apply`).
- The existing candidate-verification step is unaffected: appended rows cannot
  invalidate existing `/soul/skl/<i>/cnt` pointers.

### 4. App view model (`SaveEditorViewModel.cs`)

- Build decal rows from catalog ∪ owned: new `IsOwned` row state; unowned rows
  show no quantity editor.
- Grant staging mirroring the storage-operation flow: per-row `GrantQuantity`
  (1–4, default 1), `GrantDecal(row)`, `_decalGrantOperations` list with
  preview-on-current-JSON conflict check, `PendingDecalGrants` review rows
  (skill id, name when resolved, quantity), per-operation undo, cleared on
  `Clear` / reload / `ResetAllChanges`, counted in `PendingOperationCount` and
  `HasPendingChanges`.
- Granted rows flip to a staged "granted" highlight; after apply + reload they
  behave like normal owned rows (editable quantity).
- Apply path passes the decal operations through to the save service.

### 5. View (`MainWindow.xaml`)

- Grid additions: Owned indicator (✔/—), Grant button + 1–4 picker on unowned
  rows, staged-row highlight for pending grants.
- Toolbar gains a **Hide owned** toggle; summary text becomes "X of Y owned".

### 6. Tests

- **Core**: append semantics (existing array, missing `skl`/`psskl` nodes),
  duplicate rejection (existing row and in-batch), written field values
  (`cnt`, `updated` plausibility, `is_checked = 0`), malformed-JSON errors.
- **Data**: unchanged (catalog already covered).
- **Service**: mixed apply (scalar `cnt` edits + grants) verifies and writes
  correctly; fingerprint/backup flow untouched.
- **App**: full-list build (owned + catalog-only rows), grant staging, preview
  rejection on duplicate, undo, reset/reload clearing, pending counts, apply-path
  wiring.

## Risks / in-game verification

- `psskl` ordering: appends are assumed safe; load a granted save in-game and
  confirm the decal list shows the new decals.
- `is_checked` / `updated` semantics are unconfirmed; if the game misbehaves
  (e.g. decals invisible until "checked"), revisit defaults.
- The game enforces its own ownership limits; the UI cap stays at 4.
