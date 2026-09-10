# LET IT DIE Utilities

**LET IT DIE Utilities** is a Windows desktop app for exploring—and carefully editing—your local *LET IT DIE* save and game database files. Nothing is written until you review and apply the changes you have staged.

> **Use on local files only.** Keep a separate copy of any save you care about, and test edited saves at your own risk.

## What you can do today

### Edit a local save

- Browse and search decoded save values; stage individual scalar-value changes and export the decoded JSON.
- Manage account storage: add or replace supported catalogue items, clear slots, and expand capacity.
- Browse fighters and edit supported profile fields, available XP, and allocated stats; add, replace, or clear Death Bag items and expand their slots.
- Browse, filter, grant, and adjust owned decals.
- Manage VIP status and passes.
- Review every pending edit together, undo individual changes, and restore earlier backups.

### Explore the game database

- Find and validate a compatible `masters.db` installation.
- Search, favorite, and edit supported constant settings, or inspect the schema and table data in the Advanced view.
- Browse the localized item catalogue used by the storage editor.
- Restore compatible database backups and keep an audit trail of changes.

### Use the tower map

- Explore floors 1–50 for daily rotations 4HMA and A–D.
- See areas, upward routes, elevator stops and services, bosses, Four Force Men rooms, and boss-gated routes.
- Filter by tower band, pan and zoom, and select an area for its details.
- Toggle community-sourced area notes covering materials, shops, collectibles, trap rooms, and more.

## Built around safer edits

The app requires *LET IT DIE* to be closed before applying save or database changes. It stages edits in memory first, then creates and verifies a full backup, rechecks the source fingerprint, writes atomically, verifies the result, and records an audit entry. Restoring a backup also makes a fresh safety backup first.

Some data is intentionally read-only. The storage and Death Bag editors only create baseline templates for supported catalogue items—they do not create custom upgrades or cooked variants—and relocating existing entities or equipped-item editing is outside the current scope.

## Quick start

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.
2. Build and launch the app:

   ```powershell
   dotnet run --project src\LidUtils.App\LidUtils.App.csproj
   ```

3. Load a supported local save from **Save Editor**, or validate `masters.db` from **Game Database**. Stage your changes, review them, then apply.

## Data and credits

Tower-map area information is derived from the community **Rotations and Resources Table V2** by Oberlinx & Kaito. It ships in `settings/map-area-info.json`; see [the map-data notes](docs/map_area_info_plan.md) for details.

For the item catalogue format, validation rules, and contribution checklist, see [settings/CONTRIBUTING.md](settings/CONTRIBUTING.md).

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
