using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

public sealed class DatabaseMaintenanceService : IDatabaseMaintenanceService
{
    public const int DefaultBackupRetentionCount = 5;
    public const int MaximumBackupRetentionCount = 50;

    private static readonly IReadOnlyDictionary<string, SettingValueType> WritableTables =
        new Dictionary<string, SettingValueType>(StringComparer.Ordinal)
        {
            ["master_const_int"] = SettingValueType.Integer,
            ["master_const_float"] = SettingValueType.Float,
            ["master_const_str"] = SettingValueType.String
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDatabaseValidator _validator;
    private readonly string _backupRoot;
    private readonly string _auditRoot;
    private readonly Func<bool> _isGameRunning;

    public DatabaseMaintenanceService(
        IDatabaseValidator validator,
        string? backupRoot = null,
        string? auditRoot = null,
        Func<bool>? isGameRunning = null)
    {
        _validator = validator;
        _backupRoot = Path.GetFullPath(backupRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LidUtils",
            "backups",
            "databases"));
        _auditRoot = Path.GetFullPath(auditRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LidUtils",
            "audit",
            "databases"));
        _isGameRunning = isGameRunning ?? LetItDieProcessDetector.IsRunning;
    }

    public async Task<DatabaseApplyResult> ApplyAsync(
        DatabaseFileMetadata loadedSource,
        IReadOnlyCollection<StagedSettingChange> changes,
        int backupRetentionCount,
        CancellationToken cancellationToken = default)
        => await ApplyWithTableChangesAsync(loadedSource, changes, [], backupRetentionCount, cancellationToken);

    public async Task<DatabaseApplyResult> ApplyWithTableChangesAsync(
        DatabaseFileMetadata loadedSource,
        IReadOnlyCollection<StagedSettingChange> changes,
        IReadOnlyCollection<StagedTableRowChange> tableChanges,
        int backupRetentionCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadedSource);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(tableChanges);
        ValidateRetention(backupRetentionCount);
        ValidateChanges(changes, tableChanges);
        EnsureGameClosed();

        var source = await RequireUnchangedSourceAsync(loadedSource, cancellationToken);
        var backup = await CreateVerifiedBackupAsync(source, DatabaseBackupPurpose.Apply, cancellationToken);
        EnsureGameClosed();
        _ = await RequireUnchangedSourceAsync(loadedSource, cancellationToken);

        var audit = DatabaseAuditRecord.Prepared(
            Guid.NewGuid(),
            "Apply",
            source.Path,
            source.DatabaseSha256,
            source.SchemaSha256,
            backup.Id,
            changes.Select(change => new DatabaseAuditChange(
                change.Entry.SourceTable,
                change.Entry.Key,
                change.OriginalRawValue,
                change.ProposedRawValue))
            .Concat(tableChanges.SelectMany(change => change.Cells.Select(cell => new DatabaseAuditChange(
                change.TableName,
                $"{change.Source}.{cell.ColumnName}",
                FormatAuditValue(cell.OriginalValue),
                FormatAuditValue(cell.ProposedValue))))).ToArray());
        var auditPath = await CreateAuditAsync(audit, cancellationToken);
        var committed = false;

        try
        {
            await using var connection = await OpenReadWriteAsync(source.Path, cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);

            var lockedFingerprint = await ComputeFileSha256Async(source.Path, cancellationToken);
            if (!string.Equals(lockedFingerprint, loadedSource.DatabaseSha256, StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.SourceChanged,
                    "The database changed before the write lock was acquired. Reload it before applying changes.");
            }

            await ValidateWritableSchemaAsync(connection, cancellationToken);
            await ValidateAdvancedTableSchemasAsync(connection, tableChanges, cancellationToken);
            EnsureGameClosed();
            foreach (var change in changes)
            {
                await ApplyChangeAsync(connection, change, cancellationToken);
            }
            foreach (var change in tableChanges)
            {
                await ApplyTableRowChangeAsync(connection, change, cancellationToken);
            }

            await RequireIntegrityAsync(connection, changes.Select(change => change.Entry.SourceTable)
                .Concat(tableChanges.Select(change => change.TableName)), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch (Exception exception)
        {
            if (!committed)
            {
                await TryUpdateAuditAsync(auditPath, audit.Failed(exception.Message), cancellationToken);
            }

            throw NormalizeException(exception, "The database changes could not be applied.");
        }

        DatabaseFileMetadata updated;
        try
        {
            updated = await RequireValidDatabaseAsync(source.Path, cancellationToken);
            if (!string.Equals(updated.SchemaSha256, source.SchemaSha256, StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.VerificationFailed,
                    "The database schema changed while applying settings.");
            }

            await VerifyAppliedValuesAsync(source.Path, changes, cancellationToken);
            await VerifyAppliedTableValuesAsync(source.Path, tableChanges, cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                var restored = await ReplaceFromBackupAsync(backup, source.Path, CancellationToken.None, enforceGameClosed: false);
                await TryUpdateAuditAsync(auditPath, audit.RolledBack(restored.DatabaseSha256, exception.Message), CancellationToken.None);
            }
            catch (Exception restoreException)
            {
                await TryUpdateAuditAsync(auditPath, audit.Failed(
                    $"Post-write verification failed and automatic recovery also failed: {restoreException.Message}"),
                    CancellationToken.None);
                throw Error(DatabaseOperationError.VerificationFailed,
                    $"Post-write verification failed, and automatic recovery failed. Use backup {backup.BackupPath}. {restoreException.Message}",
                    exception);
            }

            throw Error(DatabaseOperationError.VerificationFailed,
                $"Post-write verification failed, so the verified backup was restored. {exception.Message}",
                exception);
        }

        var warnings = new List<string>();
        var auditWarning = await CompleteAuditAsync(auditPath, audit.Succeeded(updated.DatabaseSha256), CancellationToken.None);
        if (auditWarning is not null) warnings.Add(auditWarning);
        warnings.AddRange(await PruneBackupsAsync(backupRetentionCount, CancellationToken.None));
        return new DatabaseApplyResult(backup, updated, warnings);
    }

    public async Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var normalizedSource = Path.GetFullPath(sourcePath);
        var backups = await LoadBackupMetadataAsync(cancellationToken);
        return backups
            .Where(backup => PathsEqual(backup.SourcePath, normalizedSource))
            .OrderByDescending(backup => backup.CreatedUtc)
            .ToArray();
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(
        string sourcePath,
        Guid backupId,
        int backupRetentionCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ValidateRetention(backupRetentionCount);
        EnsureGameClosed();

        var normalizedSource = Path.GetFullPath(sourcePath);
        var matchingBackups = (await LoadBackupMetadataAsync(cancellationToken))
            .Where(candidate => candidate.Id == backupId)
            .ToArray();
        if (matchingBackups.Length != 1)
        {
            throw Error(DatabaseOperationError.BackupNotFound,
                "The selected database backup is missing or has ambiguous metadata.");
        }
        var backup = matchingBackups[0];
        if (!PathsEqual(backup.SourcePath, normalizedSource))
        {
            throw Error(DatabaseOperationError.IncompatibleBackup,
                "This backup belongs to a different masters.db path.");
        }

        var current = await RequireValidDatabaseAsync(normalizedSource, cancellationToken);
        var verifiedBackup = await RequireValidBackupAsync(backup, cancellationToken);
        if (!string.Equals(current.SchemaSha256, verifiedBackup.SchemaSha256, StringComparison.Ordinal))
        {
            throw Error(DatabaseOperationError.IncompatibleBackup,
                "Steam or the game changed the database schema after this backup was created. This backup cannot be restored safely.");
        }

        EnsureNoSidecars(normalizedSource);
        var safetyBackup = await CreateVerifiedBackupAsync(current, DatabaseBackupPurpose.PreRestore, cancellationToken);
        EnsureGameClosed();
        _ = await RequireUnchangedSourceAsync(current, cancellationToken);
        var auditChanges = await BuildRestoreDiffAsync(normalizedSource, verifiedBackup.BackupPath, cancellationToken);
        var audit = DatabaseAuditRecord.Prepared(
            Guid.NewGuid(),
            "Restore",
            normalizedSource,
            current.DatabaseSha256,
            current.SchemaSha256,
            safetyBackup.Id,
            auditChanges,
            verifiedBackup.Id);
        var auditPath = await CreateAuditAsync(audit, cancellationToken);

        DatabaseFileMetadata restored;
        try
        {
            EnsureGameClosed();
            EnsureNoSidecars(normalizedSource);
            restored = await ReplaceFromBackupAsync(
                verifiedBackup,
                normalizedSource,
                cancellationToken,
                expectedTarget: current);
        }
        catch (Exception exception)
        {
            if (exception is DatabaseReplacementException)
            {
                try
                {
                    var recovered = await ReplaceFromBackupAsync(safetyBackup, normalizedSource, CancellationToken.None, enforceGameClosed: false);
                    await TryUpdateAuditAsync(auditPath, audit.RolledBack(recovered.DatabaseSha256, exception.Message), CancellationToken.None);
                }
                catch (Exception recoveryException)
                {
                    await TryUpdateAuditAsync(auditPath, audit.Failed(
                        $"Restore failed and recovery also failed: {recoveryException.Message}"), CancellationToken.None);
                    throw Error(DatabaseOperationError.VerificationFailed,
                        $"Restore failed, and automatic recovery failed. Use safety backup {safetyBackup.BackupPath}. {recoveryException.Message}",
                        exception);
                }

                throw NormalizeException(exception.InnerException ?? exception,
                    "The restore failed; the pre-restore safety backup was put back.");
            }

            await TryUpdateAuditAsync(auditPath, audit.Failed(exception.Message), cancellationToken);
            throw NormalizeException(exception, "The restore failed before masters.db was replaced.");
        }

        var warnings = new List<string>();
        var auditWarning = await CompleteAuditAsync(auditPath, audit.Succeeded(restored.DatabaseSha256), CancellationToken.None);
        if (auditWarning is not null) warnings.Add(auditWarning);
        warnings.AddRange(await PruneBackupsAsync(backupRetentionCount, CancellationToken.None));
        return new DatabaseRestoreResult(safetyBackup, restored, warnings);
    }

    private async Task<DatabaseBackupInfo> CreateVerifiedBackupAsync(
        DatabaseFileMetadata source,
        DatabaseBackupPurpose purpose,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_backupRoot);
        var id = Guid.NewGuid();
        var createdUtc = DateTime.UtcNow;
        var stem = Path.GetFileNameWithoutExtension(source.Path);
        var backupPath = Path.Combine(_backupRoot,
            $"{stem}_{createdUtc:yyyyMMdd_HHmmss_fff}_{source.DatabaseSha256[..8]}_{id.ToString("N")[..8]}.db.bak");
        var metadataPath = MetadataPath(backupPath);

        try
        {
            await using (var sourceConnection = await OpenReadOnlyAsync(source.Path, cancellationToken))
            await using (var destinationConnection = await OpenReadWriteCreateAsync(backupPath, cancellationToken))
            {
                sourceConnection.BackupDatabase(destinationConnection);
            }

            var backupMetadata = await RequireValidDatabaseAsync(backupPath, cancellationToken);
            await RequireIntegrityAsync(backupPath, cancellationToken);
            if (!string.Equals(backupMetadata.SchemaSha256, source.SchemaSha256, StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.SourceChanged,
                    "The database changed while its backup was being created. The original was not modified.");
            }

            _ = await RequireUnchangedSourceAsync(source, cancellationToken);

            var info = new DatabaseBackupInfo(
                id,
                backupPath,
                source.Path,
                createdUtc,
                purpose,
                source.Length,
                source.DatabaseSha256,
                source.SchemaSha256,
                backupMetadata.Length,
                backupMetadata.DatabaseSha256);
            await WriteJsonAtomicallyAsync(metadataPath, info, cancellationToken);
            return info;
        }
        catch (Exception exception)
        {
            TryDelete(metadataPath);
            TryDelete(backupPath);
            throw NormalizeException(exception, "A verified database backup could not be created.", DatabaseOperationError.BackupFailed);
        }
    }

    private async Task<DatabaseBackupInfo> RequireValidBackupAsync(
        DatabaseBackupInfo backup,
        CancellationToken cancellationToken)
    {
        if (!IsUnderRoot(backup.BackupPath, _backupRoot) || !File.Exists(backup.BackupPath))
        {
            throw Error(DatabaseOperationError.BackupNotFound, "The selected database backup file is missing.");
        }

        var metadata = await RequireValidDatabaseAsync(backup.BackupPath, cancellationToken);
        await RequireIntegrityAsync(backup.BackupPath, cancellationToken);
        if (!string.Equals(metadata.DatabaseSha256, backup.BackupSha256, StringComparison.Ordinal) ||
            metadata.Length != backup.BackupLength ||
            !string.Equals(metadata.SchemaSha256, backup.SchemaSha256, StringComparison.Ordinal))
        {
            throw Error(DatabaseOperationError.IntegrityFailed,
                "The selected backup no longer matches its verified metadata.");
        }

        return backup;
    }

    private async Task<DatabaseFileMetadata> ReplaceFromBackupAsync(
        DatabaseBackupInfo backup,
        string targetPath,
        CancellationToken cancellationToken,
        bool enforceGameClosed = true,
        DatabaseFileMetadata? expectedTarget = null)
    {
        _ = await RequireValidBackupAsync(backup, cancellationToken);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw Error(DatabaseOperationError.Unexpected, "The database path has no parent directory.");
        var candidatePath = Path.Combine(targetDirectory,
            $".{Path.GetFileName(targetPath)}.lidutils.restore.{Guid.NewGuid():N}.tmp");

        var replaced = false;
        try
        {
            File.Copy(backup.BackupPath, candidatePath, overwrite: false);
            var candidate = await RequireValidDatabaseAsync(candidatePath, cancellationToken);
            await RequireIntegrityAsync(candidatePath, cancellationToken);
            if (!string.Equals(candidate.DatabaseSha256, backup.BackupSha256, StringComparison.Ordinal) ||
                !string.Equals(candidate.SchemaSha256, backup.SchemaSha256, StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.VerificationFailed,
                    "The prepared restore file did not match the selected backup.");
            }

            if (expectedTarget is not null)
            {
                _ = await RequireUnchangedSourceAsync(expectedTarget, cancellationToken);
            }
            EnsureNoSidecars(targetPath);
            if (enforceGameClosed) EnsureGameClosed();
            File.Replace(candidatePath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            replaced = true;
            var restored = await RequireValidDatabaseAsync(targetPath, cancellationToken);
            await RequireIntegrityAsync(targetPath, cancellationToken);
            if (!string.Equals(restored.DatabaseSha256, backup.BackupSha256, StringComparison.Ordinal) ||
                !string.Equals(restored.SchemaSha256, backup.SchemaSha256, StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.VerificationFailed,
                    "The restored database did not match the selected backup.");
            }

            return restored;
        }
        catch (Exception exception) when (replaced)
        {
            throw new DatabaseReplacementException(
                "The database was replaced but did not pass post-restore verification.", exception);
        }
        finally
        {
            TryDelete(candidatePath);
        }
    }

    private async Task<DatabaseFileMetadata> RequireUnchangedSourceAsync(
        DatabaseFileMetadata loaded,
        CancellationToken cancellationToken)
    {
        var current = await _validator.ValidateAsync(loaded.Path, cancellationToken);
        if (SourceDatabaseComparer.Compare(loaded, current) != SourceDatabaseState.Unchanged || current.Metadata is null)
        {
            throw Error(DatabaseOperationError.SourceChanged,
                "The database changed after it was loaded. Reload it before continuing.");
        }

        return current.Metadata;
    }

    private async Task<DatabaseFileMetadata> RequireValidDatabaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = await _validator.ValidateAsync(path, cancellationToken);
        if (!result.IsValid || result.Metadata is null)
        {
            var error = result.Error == DatabaseValidationError.Locked
                ? DatabaseOperationError.Locked
                : DatabaseOperationError.IntegrityFailed;
            throw Error(error, result.Message);
        }

        return result.Metadata;
    }

    private static async Task ApplyChangeAsync(
        SqliteConnection connection,
        StagedSettingChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {QuoteIdentifier(change.Entry.SourceTable)} SET value = $new WHERE id = $id AND value = $old AND typeof(value) = $storage;";
        command.Parameters.AddWithValue("$new", ParseDatabaseValue(change.Entry.ValueType, change.ProposedRawValue));
        command.Parameters.AddWithValue("$id", change.Entry.Key);
        command.Parameters.AddWithValue("$old", ParseDatabaseValue(change.Entry.ValueType, change.OriginalRawValue));
        command.Parameters.AddWithValue("$storage", StorageClass(change.Entry.ValueType));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw Error(DatabaseOperationError.SourceChanged,
                $"Setting '{change.Source}' no longer has the value that was loaded. No changes were committed.");
        }
    }

    private static async Task VerifyAppliedValuesAsync(
        string path,
        IReadOnlyCollection<StagedSettingChange> changes,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        foreach (var change in changes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT value, typeof(value) FROM {QuoteIdentifier(change.Entry.SourceTable)} WHERE id = $id;";
            command.Parameters.AddWithValue("$id", change.Entry.Key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !DatabaseValuesEqual(change.Entry.ValueType, reader.GetValue(0), change.ProposedRawValue) ||
                !string.Equals(reader.GetString(1), StorageClass(change.Entry.ValueType), StringComparison.Ordinal))
            {
                throw Error(DatabaseOperationError.VerificationFailed,
                    $"Post-write verification failed for setting '{change.Source}'.");
            }
        }
    }

    private static async Task ApplyTableRowChangeAsync(
        SqliteConnection connection,
        StagedTableRowChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var assignments = change.Cells.Select((cell, index) => $"{QuoteIdentifier(cell.ColumnName)} = $new{index}").ToArray();
        var predicates = change.OriginalKeyValues.Select((key, index) => $"{QuoteIdentifier(key.ColumnName)} IS $key{index}").ToArray();
        command.CommandText = $"UPDATE {QuoteIdentifier(change.TableName)} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)};";
        foreach (var (cell, index) in change.Cells.Select((cell, index) => (cell, index)))
            command.Parameters.AddWithValue($"$new{index}", cell.ProposedValue ?? DBNull.Value);
        foreach (var (key, index) in change.OriginalKeyValues.Select((key, index) => (key, index)))
            command.Parameters.AddWithValue($"$key{index}", key.OriginalValue ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Error(DatabaseOperationError.SourceChanged,
                $"Row '{change.Source}' changed after it was loaded. No changes were committed.");
        }
    }

    private static async Task VerifyAppliedTableValuesAsync(
        string path,
        IReadOnlyCollection<StagedTableRowChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        foreach (var change in changes)
        {
            var finalKeys = change.OriginalKeyValues.Select(key => new TableKeyValue(
                key.ColumnName,
                change.Cells.FirstOrDefault(cell => string.Equals(cell.ColumnName, key.ColumnName, StringComparison.Ordinal))?.ProposedValue ?? key.OriginalValue)).ToArray();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", change.Cells.Select(cell => QuoteIdentifier(cell.ColumnName)))} FROM {QuoteIdentifier(change.TableName)} WHERE {string.Join(" AND ", finalKeys.Select((key, index) => $"{QuoteIdentifier(key.ColumnName)} IS $key{index}"))};";
            foreach (var (key, index) in finalKeys.Select((key, index) => (key, index)))
                command.Parameters.AddWithValue($"$key{index}", key.OriginalValue ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw Error(DatabaseOperationError.VerificationFailed, $"Post-write verification could not find row '{change.Source}'.");
            for (var index = 0; index < change.Cells.Count; index++)
            {
                var actual = reader.IsDBNull(index) ? null : reader.GetValue(index);
                if (!DatabaseValuesEqual(actual, change.Cells[index].ProposedValue))
                {
                    throw Error(DatabaseOperationError.VerificationFailed,
                        $"Post-write verification failed for '{change.Source}.{change.Cells[index].ColumnName}'.");
                }
            }
        }
    }

    private static bool DatabaseValuesEqual(object? actual, object? expected) => (actual, expected) switch
    {
        (null, null) => true,
        (byte[] left, byte[] right) => left.SequenceEqual(right),
        (long left, long right) => left == right,
        (double left, double right) => left.Equals(right),
        (long left, string right) => long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && left == value,
        (double left, string right) => double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && left.Equals(value),
        _ => Equals(actual, expected)
    };

    private static async Task ValidateAdvancedTableSchemasAsync(
        SqliteConnection connection,
        IReadOnlyCollection<StagedTableRowChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            await using var objectCommand = connection.CreateCommand();
            objectCommand.CommandText = "SELECT type, COALESCE(sql, '') FROM sqlite_schema WHERE name = $name;";
            objectCommand.Parameters.AddWithValue("$name", change.TableName);
            await using var reader = await objectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !string.Equals(reader.GetString(0), "table", StringComparison.Ordinal) ||
                reader.GetString(1).StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(DatabaseOperationError.UnsupportedWriteSchema,
                    $"'{change.TableName}' is not an ordinary editable SQLite table.");
            }

            var columns = new List<(string Name, int PrimaryKey)>();
            await using var columnCommand = connection.CreateCommand();
            columnCommand.CommandText = "SELECT name, pk FROM pragma_table_info($table) ORDER BY cid;";
            columnCommand.Parameters.AddWithValue("$table", change.TableName);
            await using var columnReader = await columnCommand.ExecuteReaderAsync(cancellationToken);
            while (await columnReader.ReadAsync(cancellationToken)) columns.Add((columnReader.GetString(0), columnReader.GetInt32(1)));

            var primaryKeys = columns.Where(column => column.PrimaryKey > 0).OrderBy(column => column.PrimaryKey).Select(column => column.Name).ToArray();
            if (primaryKeys.Length == 0 || !primaryKeys.SequenceEqual(change.OriginalKeyValues.Select(key => key.ColumnName), StringComparer.Ordinal) ||
                change.Cells.Any(cell => !columns.Any(column => string.Equals(column.Name, cell.ColumnName, StringComparison.Ordinal))))
            {
                throw Error(DatabaseOperationError.UnsupportedWriteSchema,
                    $"'{change.TableName}' no longer has the columns and primary key loaded by Advanced mode.");
            }
        }
    }

    private static async Task ValidateWritableSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var (table, type) in WritableTables)
        {
            await using (var objectCommand = connection.CreateCommand())
            {
                objectCommand.CommandText = "SELECT type FROM sqlite_schema WHERE name = $name;";
                objectCommand.Parameters.AddWithValue("$name", table);
                var objectType = Convert.ToString(await objectCommand.ExecuteScalarAsync(cancellationToken));
                if (!string.Equals(objectType, "table", StringComparison.Ordinal))
                {
                    throw Error(DatabaseOperationError.UnsupportedWriteSchema,
                        $"Required writable table '{table}' is not an ordinary SQLite table.");
                }
            }

            var columns = new List<(string Name, string Type, int PrimaryKey)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name, type, pk FROM pragma_table_info($table);";
                command.Parameters.AddWithValue("$table", table);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    columns.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                }
            }

            var id = columns.SingleOrDefault(column => string.Equals(column.Name, "id", StringComparison.OrdinalIgnoreCase));
            var value = columns.SingleOrDefault(column => string.Equals(column.Name, "value", StringComparison.OrdinalIgnoreCase));
            if (id == default || id.PrimaryKey != 1 || columns.Count(column => column.PrimaryKey > 0) != 1 ||
                value == default || Affinity(value.Type) != ExpectedAffinity(type))
            {
                throw Error(DatabaseOperationError.UnsupportedWriteSchema,
                    $"Table '{table}' does not have the primary key or value type required for safe writes.");
            }
        }
    }

    private static async Task RequireIntegrityAsync(
        SqliteConnection connection,
        IEnumerable<string> touchedTables,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) rows.Add(reader.GetString(0));
            if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(DatabaseOperationError.IntegrityFailed,
                    $"SQLite integrity validation failed: {string.Join("; ", rows.Take(5))}");
            }
        }

        foreach (var table in touchedTables.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_check({QuoteIdentifier(table)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw Error(DatabaseOperationError.IntegrityFailed,
                    $"Foreign-key validation failed for '{table}'.");
            }
        }
    }

    private static async Task RequireIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        await RequireIntegrityAsync(connection, WritableTables.Keys, cancellationToken);
    }

    private async Task<IReadOnlyList<DatabaseAuditChange>> BuildRestoreDiffAsync(
        string currentPath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        var current = await ReadSettingValuesAsync(currentPath, cancellationToken);
        var restored = await ReadSettingValuesAsync(backupPath, cancellationToken);
        return current.Keys.Concat(restored.Keys).Distinct().OrderBy(id => id.Table, StringComparer.Ordinal).ThenBy(id => id.Key, StringComparer.Ordinal)
            .Where(id => !string.Equals(current.GetValueOrDefault(id), restored.GetValueOrDefault(id), StringComparison.Ordinal))
            .Select(id => new DatabaseAuditChange(id.Table, id.Key, current.GetValueOrDefault(id), restored.GetValueOrDefault(id)))
            .ToArray();
    }

    private static async Task<Dictionary<(string Table, string Key), string?>> ReadSettingValuesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Table, string Key), string?>();
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        foreach (var (table, type) in WritableTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, value FROM {QuoteIdentifier(table)};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var value = reader.IsDBNull(1) ? null : FormatDatabaseValue(type, reader.GetValue(1));
                result[(table, reader.GetString(0))] = value;
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        try
        {
            var valid = new List<DatabaseBackupInfo>();
            foreach (var backup in await LoadBackupMetadataAsync(cancellationToken))
            {
                try
                {
                    valid.Add(await RequireValidBackupAsync(backup, cancellationToken));
                }
                catch
                {
                    // Never automatically delete malformed or corrupted backup artifacts.
                }
            }

            foreach (var backup in valid.OrderByDescending(item => item.CreatedUtc).Skip(retentionCount))
            {
                try
                {
                    File.Delete(backup.BackupPath);
                    File.Delete(MetadataPath(backup.BackupPath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not remove old backup '{Path.GetFileName(backup.BackupPath)}': {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"Backup retention could not be enforced: {exception.Message}");
        }

        return warnings;
    }

    private async Task<IReadOnlyList<DatabaseBackupInfo>> LoadBackupMetadataAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_backupRoot)) return [];
        var result = new List<DatabaseBackupInfo>();
        foreach (var metadataPath in Directory.EnumerateFiles(_backupRoot, "*.db.bak.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var info = await JsonSerializer.DeserializeAsync<DatabaseBackupInfo>(stream, JsonOptions, cancellationToken);
                if (info is not null && IsPlausibleMetadata(info) && IsUnderRoot(info.BackupPath, _backupRoot) && File.Exists(info.BackupPath) &&
                    PathsEqual(metadataPath, MetadataPath(info.BackupPath)))
                {
                    result.Add(info);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Malformed metadata remains untouched and is omitted from the browser.
            }
        }

        return result;
    }

    private async Task<string> CreateAuditAsync(DatabaseAuditRecord audit, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_auditRoot);
        var path = Path.Combine(_auditRoot, $"{audit.StartedUtc:yyyyMMdd_HHmmss_fff}_{audit.Id:N}.json");
        await WriteJsonAtomicallyAsync(path, audit, cancellationToken);
        return path;
    }

    private static Task WriteAuditAsync(string path, DatabaseAuditRecord audit, CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(path, audit, cancellationToken);

    private static async Task<string?> CompleteAuditAsync(
        string path,
        DatabaseAuditRecord audit,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteAuditAsync(path, audit, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The database operation succeeded, but its prepared audit record could not be finalized: {exception.Message}";
        }
    }

    private static async Task TryUpdateAuditAsync(string path, DatabaseAuditRecord audit, CancellationToken cancellationToken)
    {
        try { await WriteAuditAsync(path, audit, cancellationToken); }
        catch { }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The metadata path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ValidateChanges(
        IReadOnlyCollection<StagedSettingChange> changes,
        IReadOnlyCollection<StagedTableRowChange> tableChanges)
    {
        if ((changes.Count == 0 && tableChanges.Count == 0) || changes.Any(change => !change.IsValid))
        {
            throw Error(DatabaseOperationError.InvalidChangeSet,
                "There are no fully validated database changes to apply.");
        }

        if (changes.Select(change => change.Entry.Id).Distinct().Count() != changes.Count)
        {
            throw Error(DatabaseOperationError.InvalidChangeSet, "The change set contains duplicate settings.");
        }

        foreach (var change in changes)
        {
            if (!WritableTables.TryGetValue(change.Entry.SourceTable, out var expectedType) || expectedType != change.Entry.ValueType)
            {
                throw Error(DatabaseOperationError.InvalidChangeSet,
                    $"Setting '{change.Source}' is not in a supported writable table.");
            }
        }

        if (tableChanges.Any(change => string.IsNullOrWhiteSpace(change.TableName) ||
            change.OriginalKeyValues.Count == 0 || change.Cells.Count == 0 ||
            change.OriginalKeyValues.Any(key => string.IsNullOrWhiteSpace(key.ColumnName)) ||
            change.Cells.Any(cell => string.IsNullOrWhiteSpace(cell.ColumnName))) ||
            tableChanges.Select(change => change.Source).Distinct(StringComparer.Ordinal).Count() != tableChanges.Count)
        {
            throw Error(DatabaseOperationError.InvalidChangeSet, "The Advanced table change set is incomplete or contains duplicate rows.");
        }
    }

    private static string? FormatAuditValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private void EnsureGameClosed()
    {
        if (_isGameRunning())
        {
            throw Error(DatabaseOperationError.GameRunning,
                "LET IT DIE is running. Close the game before applying or restoring database changes.");
        }
    }

    private static void EnsureNoSidecars(string path)
    {
        var sidecars = new[] { path + "-wal", path + "-shm", path + "-journal" }
            .Where(File.Exists)
            .Select(Path.GetFileName)
            .ToArray();
        if (sidecars.Length > 0)
        {
            throw Error(DatabaseOperationError.Locked,
                $"SQLite sidecar files are active ({string.Join(", ", sidecars)}). Close the game or updater and try again.");
        }
    }

    private static void ValidateRetention(int count)
    {
        if (count is < 1 or > MaximumBackupRetentionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"Database backup retention must be between 1 and {MaximumBackupRetentionCount}.");
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadWriteAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadWriteCreateAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static object ParseDatabaseValue(SettingValueType type, string value) => type switch
    {
        SettingValueType.Integer => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
        SettingValueType.Float => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
        SettingValueType.String => value,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static bool DatabaseValuesEqual(SettingValueType type, object databaseValue, string expected) => type switch
    {
        SettingValueType.Integer => Convert.ToInt64(databaseValue, CultureInfo.InvariantCulture) ==
                                    long.Parse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture),
        SettingValueType.Float => Convert.ToDouble(databaseValue, CultureInfo.InvariantCulture).Equals(
                                  double.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture)),
        SettingValueType.String => string.Equals(Convert.ToString(databaseValue, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal),
        _ => false
    };

    private static string FormatDatabaseValue(SettingValueType type, object value) => type switch
    {
        SettingValueType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        SettingValueType.Float => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        SettingValueType.String => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => string.Empty
    };

    private static string StorageClass(SettingValueType type) => type switch
    {
        SettingValueType.Integer => "integer",
        SettingValueType.Float => "real",
        SettingValueType.String => "text",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string ExpectedAffinity(SettingValueType type) => type switch
    {
        SettingValueType.Integer => "INTEGER",
        SettingValueType.Float => "REAL",
        SettingValueType.String => "TEXT",
        _ => string.Empty
    };

    private static string Affinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0) return "BLOB";
        return "NUMERIC";
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string MetadataPath(string backupPath) => backupPath + ".json";
    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlausibleMetadata(DatabaseBackupInfo info)
    {
        _ = Path.GetFullPath(info.SourcePath);
        _ = Path.GetFullPath(info.BackupPath);
        return info.Id != Guid.Empty &&
               !string.IsNullOrWhiteSpace(info.SourcePath) &&
               !string.IsNullOrWhiteSpace(info.BackupPath) &&
               info.SourceLength >= 0 &&
               info.BackupLength >= 0 &&
               info.SourceSha256.Length == 64 &&
               info.BackupSha256.Length == 64 &&
               info.SchemaSha256.Length == 64;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static DatabaseOperationException Error(DatabaseOperationError error, string message, Exception? inner = null) =>
        inner is null ? new DatabaseOperationException(error, message) : new DatabaseOperationException(error, message, inner);

    private static Exception NormalizeException(
        Exception exception,
        string prefix,
        DatabaseOperationError fallback = DatabaseOperationError.Unexpected)
    {
        if (exception is OperationCanceledException) return exception;
        if (exception is DatabaseOperationException databaseException) return databaseException;
        var error = exception switch
        {
            UnauthorizedAccessException => DatabaseOperationError.Locked,
            IOException => DatabaseOperationError.Locked,
            SqliteException sqlite when sqlite.SqliteErrorCode is 5 or 6 => DatabaseOperationError.Locked,
            _ => fallback
        };
        return Error(error, $"{prefix} {exception.Message}", exception);
    }

    private sealed record DatabaseAuditChange(string Table, string Key, string? OldValue, string? NewValue);

    private sealed class DatabaseReplacementException(string message, Exception innerException)
        : Exception(message, innerException);

    private sealed record DatabaseAuditRecord(
        Guid Id,
        string Operation,
        string Status,
        DateTime StartedUtc,
        DateTime? CompletedUtc,
        string SourcePath,
        string SourceSha256,
        string SchemaSha256,
        string? ResultSha256,
        Guid BackupId,
        Guid? RestoredBackupId,
        IReadOnlyList<DatabaseAuditChange> Changes,
        string? Error)
    {
        public static DatabaseAuditRecord Prepared(
            Guid id,
            string operation,
            string sourcePath,
            string sourceSha256,
            string schemaSha256,
            Guid backupId,
            IReadOnlyList<DatabaseAuditChange> changes,
            Guid? restoredBackupId = null) =>
            new(id, operation, "Prepared", DateTime.UtcNow, null, sourcePath, sourceSha256, schemaSha256, null, backupId, restoredBackupId, changes, null);

        public DatabaseAuditRecord Succeeded(string resultSha256) =>
            this with { Status = "Succeeded", CompletedUtc = DateTime.UtcNow, ResultSha256 = resultSha256 };

        public DatabaseAuditRecord RolledBack(string resultSha256, string error) =>
            this with { Status = "RolledBack", CompletedUtc = DateTime.UtcNow, ResultSha256 = resultSha256, Error = error };

        public DatabaseAuditRecord Failed(string error) =>
            this with { Status = "Failed", CompletedUtc = DateTime.UtcNow, Error = error };
    }
}
