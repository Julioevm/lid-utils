# Let It Die Settings Manager — Project Plan

## Project goal

Build a Windows desktop application that discovers Let It Die's `masters.db`, presents selected internal game values through a clear graphical interface, and applies changes without putting the original database at unnecessary risk.

The first path checked will be:

```text
D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db
```

The database is SQLite and currently contains 222 tables. The initial editor will focus on the settings-like constant tables instead of exposing unrestricted editing across the entire database:

- `master_const_int`
- `master_const_float`
- `master_const_str`

## Guiding principles

- Never create a new empty database when the expected file cannot be found.
- Never write to the database before creating a verified backup.
- Apply a group of changes atomically: either all changes succeed or none do.
- Require the game to be closed before applying or restoring changes.
- Show the user exactly what will change before writing.
- Keep undocumented settings visibly marked as experimental.
- Keep backups and application metadata outside the game installation directory.
- Do not redistribute game data or include `masters.db` in the repository.
- Exclude purchases, DLC, online schedules, and service-related data from curated editing.

## Proposed technology

- C# and .NET 8
- WPF desktop interface
- `Microsoft.Data.Sqlite` for database access
- A lightweight MVVM-style separation between the interface, application logic, and database access
- xUnit for automated tests
- Windows x64 self-contained release package

## Proposed repository structure

```text
lid-utils/
├── src/
│   ├── LidUtils.App/              # WPF application
│   ├── LidUtils.Core/             # Models, validation, change tracking
│   └── LidUtils.Data/             # Discovery, SQLite access, backup/restore
├── tests/
│   ├── LidUtils.Core.Tests/
│   └── LidUtils.Data.Tests/
├── settings/
│   └── settings.catalog.json      # Curated labels, descriptions, units, ranges
├── PLAN.md
└── LidUtils.sln
```

## Milestone 0 — Project foundation

### Objective

Create a maintainable application skeleton and establish the safety rules that all later database work must follow.

### Deliverables

- Initialize Git and add an appropriate `.gitignore`.
- Create the .NET solution and App, Core, Data, and test projects.
- Add a minimal WPF window that builds and launches.
- Configure dependency injection or a similarly simple composition root.
- Add basic logging with sensitive paths and values handled deliberately.
- Add continuous build and test commands.
- Document local development and release commands in `README.md`.

### Completion criteria

- A clean checkout can be built using the documented command.
- The test projects run successfully.
- The empty application opens on Windows.
- Game database files and generated backups are ignored by Git.

## Milestone 1 — Database discovery and read-only validation

**Status: Complete. The solution builds cleanly, automated tests pass, and the known local database has passed an opt-in read-only smoke test.**

### Objective

Reliably find and identify a compatible Let It Die database without modifying it.

### Deliverables

- Check the requested `D:` path first.
- Discover additional Steam libraries from Steam's configuration.
- Check the standard Steam installation location.
- Add a manual file picker as a fallback.
- Remember the last manually selected database path.
- Verify the SQLite file header.
- Verify that required tables and columns exist.
- Run SQLite `quick_check` in read-only mode.
- Read useful file metadata, schema information, and a database fingerprint.
- Report missing, inaccessible, corrupt, locked, and unsupported databases clearly.

### Completion criteria

- The known local database is detected automatically.
- Selecting an unrelated or invalid file produces a useful error and never creates or changes a file.
- Validation uses a read-only connection.
- The application can distinguish a compatible database from an unknown schema revision.
- Tests cover the default path, fallback selection, invalid headers, missing tables, and corrupt fixtures.

## Milestone 2 — Read-only settings browser

**Status: Complete. The settings and schema browsers use short-lived read-only connections, automated fixture tests pass, and the local game database has passed the opt-in browsing smoke test.**

### Objective

Make the database useful to explore before enabling any write capability.

### Deliverables

- Load entries from `master_const_int`, `master_const_float`, and `master_const_str`.
- Display setting key, raw value, type, and source table.
- Add search, sorting, and type/category filters.
- Keep database access asynchronous so the interface remains responsive.
- Add paging or virtualization where necessary.
- Add a details panel for descriptions, units, known behavior, and risk level.
- Add a read-only advanced schema/table inspector.
- Display database path, validation result, last modified time, and fingerprint.

### Completion criteria

- All constant entries can be searched and inspected without editing the database.
- Large result sets do not freeze the interface.
- Closing the browser releases all database connections.
- The browser behaves predictably when values or tables are missing.

## Milestone 3 — Curated settings catalog

**Status: Complete. The versioned catalog is validated before use, curated and raw values are visibly separated, local favorites/history are persisted, and undocumented constants remain available in read-only mode.**

### Objective

Turn raw database identifiers into understandable, validated controls.

### Deliverables

- Define a versioned `settings.catalog.json` format.
- Support label, description, category, value type, units, minimum, maximum, step, default display format, and risk level.
- Support display conversions while retaining the exact raw database value.
- Mark unknown entries as undocumented or experimental.
- Add favorites and recently viewed settings.
- Add catalog validation with actionable startup errors.
- Document how contributors can add or correct setting definitions.

### Completion criteria

- Catalog entries map unambiguously to a table and primary key.
- Invalid ranges, duplicate mappings, and incompatible types are rejected by tests.
- The interface always distinguishes displayed units from raw values.
- An undocumented constant remains accessible in advanced read-only mode.

## Milestone 4 — Change staging and comparison

**Status: Complete. The app stages validated changes only in memory, provides exact diffs and reset controls, and discards staging if a fresh read-only fingerprint check detects a source database change.**

### Objective

Let users prepare changes and understand their impact before any data is written.

### Deliverables

- Add type-appropriate editors for integers, floating-point values, and strings.
- Validate edits against catalog rules.
- Track original, current, and proposed values.
- Add reset for one setting and reset all pending changes.
- Add a pending-changes panel with an exact before/after diff.
- Add warnings for experimental and unusually large changes.
- Detect when the source database changes after it was loaded.
- Keep all edits in memory until the user explicitly chooses Apply.

### Completion criteria

- Editing the UI alone never writes to disk.
- Invalid values cannot enter the apply workflow.
- Reverting a value removes it from the pending diff.
- External database changes invalidate or safely refresh the pending change set.

## Milestone 5 — Backup, transactional writes, and restore

### Objective

Apply changes safely and provide a dependable recovery path.

### Deliverables

- Detect whether the game is running and block write/restore operations while it is active.
- Create timestamped backups outside the game directory.
- Record backup metadata, including source path, timestamp, size, and fingerprint.
- Revalidate the database immediately before writing.
- Confirm that each source value still matches the value originally loaded.
- Apply parameterized updates by exact primary key inside one transaction.
- Require every update to affect exactly one row.
- Run integrity and foreign-key checks before completing the operation.
- Roll back the full transaction on any failed update or validation.
- Add a backup browser and explicit restore workflow.
- Maintain a local audit log of table, key, old value, and new value.

### Completion criteria

- A backup is created and verified before every write.
- Successful apply operations change only the intended rows.
- Injected failures leave the target database unchanged.
- A backup can be restored and passes integrity validation afterward.
- Locked files, permission failures, schema changes, and insufficient disk space result in safe failures.
- The application never partially applies a pending change set.

## Milestone 6 — Profiles and repeatable modifications

### Objective

Allow users to save, share, inspect, and reapply sets of changes without distributing game data.

### Deliverables

- Define a versioned profile format containing setting identifiers and proposed values only.
- Export pending or applied changes as a profile.
- Import profiles into the staging area rather than applying them automatically.
- Preview conflicts, missing keys, range violations, and schema incompatibilities.
- Add profile name, description, author, and optional notes.
- Allow profile values to be selectively enabled or disabled.
- Add a clean-database comparison that produces a profile-sized diff.

### Completion criteria

- Profiles never contain a copy of the game database.
- Importing a profile cannot bypass catalog validation or the normal backup workflow.
- Unknown or incompatible settings are reported without blocking compatible entries.
- Export/import round trips preserve supported values exactly.

## Milestone 7 — Specialized editors

### Objective

Add safer domain-specific tools for data that is more complex than single constants.

### Candidate editors

- Enemy and boss parameters
- Item and equipment statistics
- Drop generation and probability tables
- Floor and stage generation
- Shop and reward values that are clearly local gameplay data

### Requirements for each editor

- Document the tables and relationships it uses.
- Define allowed operations and validation rules.
- Present meaningful names rather than raw foreign keys where possible.
- Include relationship and probability-total checks where applicable.
- Use the same staging, backup, transaction, and audit mechanisms as constant edits.
- Add focused tests using a minimal synthetic database fixture.

### Completion criteria

- Each editor has documented behavior and risk boundaries.
- Relationship integrity is preserved after edits.
- No editor exposes online services, purchases, or DLC entitlement manipulation.

## Milestone 8 — Packaging and first stable release

### Objective

Produce a dependable application that can be used without a development environment.

### Deliverables

- Publish a Windows x64 self-contained build.
- Add application icon, version information, and About screen.
- Add first-run safety guidance.
- Add accessible keyboard navigation and readable validation messages.
- Test on a clean supported Windows machine.
- Add a release checklist and changelog.
- Document recovery steps if the game or Steam replaces `masters.db`.
- Produce checksums for release artifacts.

### Completion criteria

- The packaged application launches without requiring the .NET SDK.
- Discovery, browsing, backup, apply, and restore pass an end-to-end release test.
- The release contains no game files, local database copies, backups, or machine-specific paths.
- Known limitations and supported database fingerprints are documented.

## Test strategy

### Unit tests

- Catalog parsing and validation
- Raw/display value conversion
- Range and type validation
- Change tracking and diff generation
- Profile import/export
- Path candidate ordering

### Integration tests

- Build minimal temporary SQLite fixtures from known schemas.
- Open compatible and incompatible databases read-only.
- Apply updates to temporary database copies only.
- Verify rollback after injected failures.
- Verify backups and restores byte-for-byte or logically, as appropriate.
- Verify integrity checks and exact affected-row counts.

### Manual safety tests

- Missing game installation
- Database on a non-default Steam library
- Game running during apply or restore
- Read-only file and permission errors
- Locked database
- Insufficient backup destination space
- Steam replacing the database between load and apply
- Application termination during staging and during a transaction

The real game database must never be used as an automated write-test target. Tests that use its schema should operate on a temporary copy or a purpose-built minimal fixture.

## Initial release boundaries

The first stable release will not include:

- Arbitrary SQL execution
- Unrestricted editing of all 222 tables
- Editing while the game is running
- Automatic profile application on startup
- Cloud synchronization
- Multiplayer, online-service, purchase, DLC, or entitlement modifications
- Redistribution of the original database or other game assets

## Suggested implementation order

1. Complete Milestones 0 and 1 before creating setting controls.
2. Complete the read-only browser in Milestone 2 and use it to research settings safely.
3. Build the catalog and staging system in Milestones 3 and 4.
4. Review the full write and recovery design before beginning Milestone 5.
5. Treat Milestone 5 as the minimum safety gate for any public build with editing enabled.
6. Add profiles and specialized editors only after the core write path has comprehensive tests.

## Definition of a successful MVP

The MVP is successful when a user can launch the application, have a compatible Let It Die database detected, browse and search known constants, stage validated changes, review an exact diff, create a verified backup, apply all changes atomically, and restore the backup if needed—without requiring SQLite knowledge or manually editing game files.
