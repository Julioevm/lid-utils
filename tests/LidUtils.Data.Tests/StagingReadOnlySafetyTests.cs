using LidUtils.Core;
using LidUtils.Data;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class StagingReadOnlySafetyTests
{
    [Fact]
    public async Task ReadOnlyLoadAndInMemoryStaging_DoNotChangeDatabaseFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateCompatibleDatabaseAsync(path, 10);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var validator = new DatabaseValidator();
        var original = await validator.ValidateAsync(path);
        Assert.True(original.IsValid, original.Message);

        var entries = await new ReadOnlyDatabaseBrowser().LoadSettingsAsync(path);
        var count = Assert.Single(entries.Entries);
        var staged = new ChangeStagingService().Stage(count, "20");

        Assert.True(staged.Change!.IsValid);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var afterStaging = await validator.ValidateAsync(path);
        Assert.Equal(SourceDatabaseState.Unchanged, SourceDatabaseComparer.Compare(original.Metadata!, afterStaging));
    }

    [Fact]
    public async Task FreshValidation_DetectsFixtureChangedAfterItWasLoaded()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateCompatibleDatabaseAsync(path, 10);
        var validator = new DatabaseValidator();
        var loaded = await validator.ValidateAsync(path);
        Assert.True(loaded.IsValid, loaded.Message);

        await CreateCompatibleDatabaseAsync(path, 11);
        var current = await validator.ValidateAsync(path);

        Assert.Equal(SourceDatabaseState.Changed, SourceDatabaseComparer.Compare(loaded.Metadata!, current));
    }

    private static async Task CreateCompatibleDatabaseAsync(string path, int value)
    {
        if (File.Exists(path)) File.Delete(path);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE master_const_int (id TEXT PRIMARY KEY, value INTEGER NOT NULL);
            CREATE TABLE master_const_float (id TEXT PRIMARY KEY, value REAL NOT NULL);
            CREATE TABLE master_const_str (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO master_const_int (id, value) VALUES ('COUNT', {{value}});
            """;
        await command.ExecuteNonQueryAsync();
    }
}
