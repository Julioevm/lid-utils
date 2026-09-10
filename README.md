# LET IT DIE Utilities

A Windows desktop utility for inspecting and safely editing Let It Die's `masters.db` and supported local save files.

## Current status

The app finds and validates compatible Let It Die game databases, provides a searchable browser for constants and schema data, and lets you stage and review exact setting changes. Confirmed changes are applied in one SQLite transaction only after the game is closed and a full verified backup has been created. Original-value checks, integrity verification, audit records, and automatic recovery prevent partial writes.

The save editor is available now. It opens supported local `.sav` files, exposes searchable scalar values, stages edits for confirmation, and applies them through a fingerprint-checked, backup-first atomic replacement workflow while the game is closed. It can also export decoded JSON for inspection.

Account storage editing uses the installed game's item catalogue. Load a save and a compatible database, select a storage slot, then stage adding/replacing an item, clearing it, or expanding storage by ten slots. Review the preview and pending operations before applying; undo/reset does not write to the save. Existing capacity is read from the save, not reset to a fixed starting size.

The catalogue remains browseable for definitions that cannot safely be constructed; unsupported entries explain why they cannot be added. New items use baseline templates, not custom equipment upgrades or cooked variants. Character inventories and equipped items are outside this feature's scope. Automated checks cannot establish in-game acceptance: keep a backup and test edited saves separately. See [INVENTORY_PLAN.md](INVENTORY_PLAN.md) for scope and validation details.

The database backup browser can restore snapshots for the selected database when the schema still matches. Backups and their metadata live under `%LOCALAPPDATA%\LidUtils\backups\databases`, audit records live under `%LOCALAPPDATA%\LidUtils\audit\databases`, and the global backup limit defaults to five. Catalog information remains helpful context, but every valid constant in the three supported tables can be changed.

Game Database → Map is a read-only tower-map viewer. It renders the five main daily rotations (4HMA, A–D) with the themed bands Metro F1–F10, Arcade F11–F20, Amusement F21–F30, Rooftop F31–F40 and Hazama F41–F50 on one vertical scale, showing each mounted area (base corridors and rotation-only side areas), elevator stops, and the allowed upward connections. Elevator services are drawn as colour-coded route lines, one colour per car, joining every floor the car stops at (including the Waiting Room hub of the main elevator at the base) so unlocked cars are visible even where no stairs connect the floors. Pick a rotation or press Today to jump to the one the term calendar marks as live, restrict the view to a single band, drag to pan, and click a dot or a list row to inspect an area's details, routes, keys and gates. Boss encounters are overlaid on the map: the four section bosses (Max Sharp, Colonel Jackson, Mr Crowley, Gunkanyama) get a BOSS badge on their arena floor, and paid Four Force Men rooms get an FFM badge (both also appear in the area list, with fees in the details). Roaming section-boss spawns are loaded but deliberately not badged, since they cover most floors. Routes gated behind a boss clear or trigger are drawn in the matching colour. Everything is read from masters.db without the game needing to be closed; Heaven (F51–F451) is a later iteration.

See [settings/CONTRIBUTING.md](settings/CONTRIBUTING.md) for the catalog format, validation rules, and contribution checklist.

## Development

Requirements:

- Windows
- .NET 8 SDK

Build and test:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false' # Work around a broken optional workload manifest if needed.
dotnet restore LidUtils.sln
dotnet build LidUtils.sln --no-restore
dotnet test LidUtils.sln --no-build
```

An optional local smoke test validates the installed database read-only, then exercises apply and restore only on a temporary snapshot:

```powershell
$env:LID_UTILS_SMOKE_DB = 'D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db'
dotnet test LidUtils.sln --no-build
```

An optional save smoke test reads the installed save and exercises editing only on a temporary copy:

```powershell
$env:LID_UTILS_SMOKE_SAVE = 'D:\SteamLibrary\steamapps\common\LET IT DIE\Savedata\your-save.sav'
dotnet test LidUtils.sln --no-build
```

Run the desktop application:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet run --project src\LidUtils.App\LidUtils.App.csproj
```

See [PLAN.md](PLAN.md) for the complete roadmap and safety requirements.
