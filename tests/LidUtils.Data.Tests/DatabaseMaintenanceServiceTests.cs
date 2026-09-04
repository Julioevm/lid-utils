using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class DatabaseMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyWithTableChanges_UpdatesAnAdvancedTableRowByPrimaryKey()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateDatabaseAsync(databasePath);
        await ExecuteAsync(databasePath, "CREATE TABLE advanced_values (id INTEGER PRIMARY KEY, label TEXT, amount REAL); INSERT INTO advanced_values VALUES (1, 'before', 2.5);");
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(databasePath);
        Assert.True(loaded.IsValid, loaded.Message);
        var service = new DatabaseMaintenanceService(validator, Path.Combine(temporaryDirectory.Path, "backups"), Path.Combine(temporaryDirectory.Path, "audit"), () => false);

        await service.ApplyWithTableChangesAsync(loaded.Metadata!, [],
        [
            new StagedTableRowChange("advanced_values", [new TableKeyValue("id", 1L)],
            [
                new StagedTableCellChange("label", "before", "after"),
                new StagedTableCellChange("amount", 2.5d, 4d)
            ])
        ], 5);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT label, amount FROM advanced_values WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("after", reader.GetString(0));
        Assert.Equal(4d, reader.GetDouble(1));
    }

    [Fact]
    public async Task ApplyAndRestore_CreateVerifiedBackupsAuditAndExactValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        var backupRoot = Path.Combine(temporaryDirectory.Path, "backups");
        var auditRoot = Path.Combine(temporaryDirectory.Path, "audit");
        await CreateDatabaseAsync(databasePath);
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(databasePath);
        Assert.True(loaded.IsValid, loaded.Message);
        var service = new DatabaseMaintenanceService(validator, backupRoot, auditRoot, () => false);

        var result = await service.ApplyAsync(loaded.Metadata!,
        [
            Change("master_const_int", "COUNT", "10", "42", SettingValueType.Integer),
            Change("master_const_float", "RATE", "1.25", "2.5", SettingValueType.Float),
            Change("master_const_str", "TITLE", "Tower", "Garden", SettingValueType.String)
        ], 5);

        Assert.True(File.Exists(result.Backup.BackupPath));
        Assert.True(File.Exists(result.Backup.BackupPath + ".json"));
        Assert.Equal(64, result.Backup.SourceSha256.Length);
        Assert.Equal(64, result.Backup.BackupSha256.Length);
        Assert.Equal("42", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
        Assert.Equal("2.5", await ReadValueAsync(databasePath, "master_const_float", "RATE"));
        Assert.Equal("Garden", await ReadValueAsync(databasePath, "master_const_str", "TITLE"));
        Assert.Single(Directory.EnumerateFiles(auditRoot, "*.json"));
        Assert.Contains("\"Status\": \"Succeeded\"", await File.ReadAllTextAsync(Directory.EnumerateFiles(auditRoot).Single()));

        var listed = Assert.Single(await service.ListBackupsAsync(databasePath));
        Assert.Equal(result.Backup.Id, listed.Id);
        var restored = await service.RestoreAsync(databasePath, listed.Id, 5);

        Assert.Equal("10", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
        Assert.Equal("1.25", await ReadValueAsync(databasePath, "master_const_float", "RATE"));
        Assert.Equal("Tower", await ReadValueAsync(databasePath, "master_const_str", "TITLE"));
        Assert.True(File.Exists(restored.SafetyBackup.BackupPath));
        Assert.Equal(2, Directory.EnumerateFiles(auditRoot, "*.json").Count());
    }

    [Fact]
    public async Task Apply_WhenLaterUpdateFails_RollsBackEveryRow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        var auditRoot = Path.Combine(temporaryDirectory.Path, "audit");
        await CreateDatabaseAsync(databasePath, addFailingTrigger: true);
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(databasePath);
        var service = new DatabaseMaintenanceService(
            validator,
            Path.Combine(temporaryDirectory.Path, "backups"),
            auditRoot,
            () => false);

        await Assert.ThrowsAsync<DatabaseOperationException>(() => service.ApplyAsync(loaded.Metadata!,
        [
            Change("master_const_int", "COUNT", "10", "42", SettingValueType.Integer),
            Change("master_const_float", "RATE", "1.25", "2.5", SettingValueType.Float)
        ], 5));

        Assert.Equal("10", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
        Assert.Equal("1.25", await ReadValueAsync(databasePath, "master_const_float", "RATE"));
        Assert.Contains("\"Status\": \"Failed\"", await File.ReadAllTextAsync(Directory.EnumerateFiles(auditRoot).Single()));
    }

    [Fact]
    public async Task Apply_BlocksGameAndChangedSourceBeforeWriting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        var backupRoot = Path.Combine(temporaryDirectory.Path, "backups");
        await CreateDatabaseAsync(databasePath);
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(databasePath);
        var change = Change("master_const_int", "COUNT", "10", "11", SettingValueType.Integer);
        var running = new DatabaseMaintenanceService(validator, backupRoot, Path.Combine(temporaryDirectory.Path, "audit"), () => true);

        var runningError = await Assert.ThrowsAsync<DatabaseOperationException>(() =>
            running.ApplyAsync(loaded.Metadata!, [change], 5));
        Assert.Equal(DatabaseOperationError.GameRunning, runningError.Error);
        Assert.False(Directory.Exists(backupRoot));

        await ExecuteAsync(databasePath, "UPDATE master_const_int SET value = 12 WHERE id = 'COUNT';");
        var stopped = new DatabaseMaintenanceService(validator, backupRoot, Path.Combine(temporaryDirectory.Path, "audit"), () => false);
        var changedError = await Assert.ThrowsAsync<DatabaseOperationException>(() =>
            stopped.ApplyAsync(loaded.Metadata!, [change], 5));
        Assert.Equal(DatabaseOperationError.SourceChanged, changedError.Error);
        Assert.False(Directory.Exists(backupRoot));
        Assert.Equal("12", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
    }

    [Fact]
    public async Task Apply_RequiresOriginalValueAndExactlyOneAffectedRow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateDatabaseAsync(databasePath);
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(databasePath);
        var service = new DatabaseMaintenanceService(
            validator,
            Path.Combine(temporaryDirectory.Path, "backups"),
            Path.Combine(temporaryDirectory.Path, "audit"),
            () => false);

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() =>
            service.ApplyAsync(loaded.Metadata!,
                [Change("master_const_int", "COUNT", "9", "11", SettingValueType.Integer)], 5));

        Assert.Equal(DatabaseOperationError.SourceChanged, exception.Error);
        Assert.Equal("10", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
    }

    [Fact]
    public async Task Restore_BlocksBackupAfterSchemaChange()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(temporaryDirectory.Path, "masters.db");
        var validator = new DatabaseValidator();
        await CreateDatabaseAsync(databasePath);
        var loaded = await validator.ValidateAsync(databasePath);
        var service = new DatabaseMaintenanceService(
            validator,
            Path.Combine(temporaryDirectory.Path, "backups"),
            Path.Combine(temporaryDirectory.Path, "audit"),
            () => false);
        var applied = await service.ApplyAsync(loaded.Metadata!,
            [Change("master_const_int", "COUNT", "10", "11", SettingValueType.Integer)], 5);
        await ExecuteAsync(databasePath, "CREATE TABLE steam_update (id INTEGER PRIMARY KEY);");

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() =>
            service.RestoreAsync(databasePath, applied.Backup.Id, 5));

        Assert.Equal(DatabaseOperationError.IncompatibleBackup, exception.Error);
        Assert.Equal("11", await ReadValueAsync(databasePath, "master_const_int", "COUNT"));
    }

    [Fact]
    public async Task Retention_IsGlobalAndRunsOnlyAfterSuccessfulOperations()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var validator = new DatabaseValidator();
        var backupRoot = Path.Combine(temporaryDirectory.Path, "backups");
        var service = new DatabaseMaintenanceService(
            validator,
            backupRoot,
            Path.Combine(temporaryDirectory.Path, "audit"),
            () => false);
        var first = Path.Combine(temporaryDirectory.Path, "one", "masters.db");
        var second = Path.Combine(temporaryDirectory.Path, "two", "masters.db");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        await CreateDatabaseAsync(first);
        await CreateDatabaseAsync(second);

        foreach (var path in new[] { first, second, first })
        {
            var loaded = await validator.ValidateAsync(path);
            var oldValue = await ReadValueAsync(path, "master_const_int", "COUNT");
            var newValue = (long.Parse(oldValue) + 1).ToString();
            await service.ApplyAsync(loaded.Metadata!,
                [Change("master_const_int", "COUNT", oldValue, newValue, SettingValueType.Integer)], 2);
            await Task.Delay(5);
        }

        Assert.Equal(2, Directory.EnumerateFiles(backupRoot, "*.db.bak").Count());
        Assert.Equal(2, Directory.EnumerateFiles(backupRoot, "*.db.bak.json").Count());
    }

    private static StagedSettingChange Change(
        string table,
        string key,
        string oldValue,
        string newValue,
        SettingValueType type) =>
        new(new SettingEntry(key, oldValue, type, table, false), newValue, true, null, []);

    private static async Task CreateDatabaseAsync(string path, bool addFailingTrigger = false)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE master_const_int (id CHARACTER(64) NOT NULL PRIMARY KEY, value INTEGER NOT NULL);
            CREATE TABLE master_const_float (id CHARACTER(64) NOT NULL PRIMARY KEY, value FLOAT NOT NULL);
            CREATE TABLE master_const_str (id CHARACTER(64) NOT NULL PRIMARY KEY, value CHARACTER(64) NOT NULL);
            INSERT INTO master_const_int VALUES ('COUNT', 10);
            INSERT INTO master_const_float VALUES ('RATE', 1.25);
            INSERT INTO master_const_str VALUES ('TITLE', 'Tower');
            """ + (addFailingTrigger
                ? "CREATE TRIGGER reject_rate BEFORE UPDATE ON master_const_float BEGIN SELECT RAISE(ABORT, 'injected failure'); END;"
                : string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadValueAsync(string path, string table, string key)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CAST(value AS TEXT) FROM \"{table}\" WHERE id = $id;";
        command.Parameters.AddWithValue("$id", key);
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
