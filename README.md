# LET IT DIE Utilities

A Windows desktop utility for inspecting and safely editing Let It Die's `masters.db` and supported local save files.

## Current status

The app finds and validates compatible Let It Die game databases, provides a searchable browser for constants and schema data, and lets you stage and review exact setting changes. Confirmed changes are applied in one SQLite transaction only after the game is closed and a full verified backup has been created. Original-value checks, integrity verification, audit records, and automatic recovery prevent partial writes.

The save editor is available now. It opens supported local `.sav` files, exposes searchable scalar values, stages edits for confirmation, and applies them through a fingerprint-checked, backup-first atomic replacement workflow while the game is closed. It can also export decoded JSON for inspection.

The database backup browser can restore snapshots for the selected database when the schema still matches. Backups and their metadata live under `%LOCALAPPDATA%\LidUtils\backups\databases`, audit records live under `%LOCALAPPDATA%\LidUtils\audit\databases`, and the global backup limit defaults to five. Catalog information remains helpful context, but every valid constant in the three supported tables can be changed.

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
