# LET IT DIE Utilities

A Windows desktop tool for inspecting Let It Die's `masters.db` and safely editing local save files.

## Current status

The settings browser through Milestone 4 is implemented. The application can:

- Check `D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db` first.
- Discover additional Steam libraries from `libraryfolders.vdf`.
- Let the user select `masters.db` manually and remember that selection.
- Validate the SQLite header and run `PRAGMA quick_check`.
- Confirm the required constant tables and columns are present.
- Calculate database and schema fingerprints.
- Display validation results in a Windows interface.
- Load integer, floating-point, and string constants asynchronously.
- Search by key, value, or source table, sort columns, and filter by value type or key-prefix category.
- Edit database setting drafts directly in the table, with inline validation, undo, and persisted row-based favorites.
- Show the database path, validation status, modified time, and fingerprints.
- Inspect every table/view, its columns, row count, and a read-only preview capped at 100 rows.
- Load a versioned, strictly validated curated settings catalog.
- Show curated labels, descriptions, categories, units, ranges, display formats, conversions, and risk levels.
- Keep exact raw database text visible beside each editable draft.
- Clearly mark constants missing from the catalog as undocumented and experimental.
- Save favorite settings in local application preferences.
- Stage integer, floating-point, and string constant changes entirely in memory.
- Validate staged numeric values against catalog ranges and increments.
- Review exact original/proposed raw-value diffs, reset one setting, or reset all pending changes.
- Flag undocumented/experimental settings and unusually large numeric changes.
- Revalidate and fingerprint the source database through read-only access before staging; if it changed, discard pending changes and require a reload.
- Discover `.sav` files in `D:\SteamLibrary\steamapps\common\LET IT DIE\Savedata` or open one manually.
- Validate and decode the BRG v2/zlib save container and expose its scalar JSON values as searchable paths.
- Stage string, number, boolean, and null-safe edits entirely in memory while preserving all unedited JSON bytes exactly.
- Recheck the source fingerprint and block writes while LET IT DIE is running.
- Create and verify a timestamped backup under `%LOCALAPPDATA%\LidUtils\backups\saves` before every save write.
- Atomically replace and then revalidate an edited save; automatically restore the verified backup if post-write verification fails.

All `masters.db` access remains read-only and uses short-lived, non-pooled connections. Database edits are still staging-only. Save-file edits use a separate backup-first workflow and require explicit confirmation. The initial settings catalog is intentionally small while behavior is researched; undocumented constants remain accessible with an explicit warning.

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

An optional local smoke test can validate an installed game database without writing to it:

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
