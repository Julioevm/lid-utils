using LidUtils.Core;
using LidUtils.Data;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class ReadOnlyDatabaseBrowserTests
{
    private readonly ReadOnlyDatabaseBrowser _browser = new();

    [Fact]
    public async Task LoadSettings_LoadsAllTypesAndRepresentsNulls()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateFixtureAsync(path);
        var writeTime = File.GetLastWriteTimeUtc(path);

        var result = await _browser.LoadSettingsAsync(path);

        Assert.Empty(result.Warnings);
        Assert.Collection(result.Entries,
            item => Assert.Equal(("PLAYER_HEALTH", "100", SettingValueType.Integer), (item.Key, item.RawValue, item.ValueType)),
            item => Assert.Equal(("WORLD_SPEED", "1.25", SettingValueType.Float), (item.Key, item.RawValue, item.ValueType)),
            item =>
            {
                Assert.Equal("OTHER_EMPTY", item.Key);
                Assert.Equal("(NULL)", item.RawValue);
                Assert.True(item.IsNull);
            },
            item => Assert.Equal(("UI_TITLE", "Tower", SettingValueType.String), (item.Key, item.RawValue, item.ValueType)));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task LoadSettings_SkipsMissingTablesWithWarnings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await using (var connection = await OpenWritableAsync(path))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE master_const_int (id TEXT, value INTEGER); INSERT INTO master_const_int VALUES ('ONE', 1);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await _browser.LoadSettingsAsync(path);

        Assert.Single(result.Entries);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public async Task LoadSettings_CatalogMapsSyntheticDatabaseByTableAndPrimaryKey()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateFixtureAsync(path);
        var loaded = await _browser.LoadSettingsAsync(path);
        var catalog = new SettingsCatalog(1, "fixture", [
            new SettingDefinition("master_const_int", "PLAYER_HEALTH", "Player health", "Base player health.", "Player",
                SettingValueType.Integer, "points", null, 1, 1000, 1, "0", null, RiskLevel.Moderate)]);

        var applied = catalog.Apply(loaded.Entries);

        var curated = Assert.Single(applied.Entries, item => item.IsDocumented);
        Assert.Equal("Player health", curated.Label);
        Assert.Equal("100", curated.RawValue);
        Assert.All(applied.Entries.Where(item => item.Key != "PLAYER_HEALTH"), item => Assert.False(item.IsDocumented));
    }

    [Fact]
    public async Task LoadSchema_ReturnsColumnsAndCountsWithoutHoldingFileOpen()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateFixtureAsync(path);

        var schema = await _browser.LoadSchemaAsync(path);

        var integerTable = Assert.Single(schema, table => table.Name == "master_const_int");
        Assert.Equal(1, integerTable.RowCount);
        Assert.Equal(["id", "value"], integerTable.Columns.Select(column => column.Name));
        Assert.True(integerTable.Columns[0].IsPrimaryKey);

        var movedPath = path + ".moved";
        File.Move(path, movedPath);
        Assert.True(File.Exists(movedPath));
    }

    [Fact]
    public async Task LoadTablePreview_IsCappedAndRejectsUnknownObjectNames()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateFixtureAsync(path);

        var preview = await _browser.LoadTablePreviewAsync(path, "preview data", 2);

        Assert.Equal(["id", "note"], preview.ColumnNames);
        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.IsTruncated);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _browser.LoadTablePreviewAsync(path, "preview data; DROP TABLE master_const_int"));
    }

    private static async Task CreateFixtureAsync(string path)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE master_const_int (id TEXT PRIMARY KEY, value INTEGER);
            CREATE TABLE master_const_float (id TEXT PRIMARY KEY, value REAL);
            CREATE TABLE master_const_str (id TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE "preview data" (id INTEGER PRIMARY KEY, note TEXT);
            INSERT INTO master_const_int VALUES ('PLAYER_HEALTH', 100);
            INSERT INTO master_const_float VALUES ('WORLD_SPEED', 1.25);
            INSERT INTO master_const_str VALUES ('UI_TITLE', 'Tower'), ('OTHER_EMPTY', NULL);
            INSERT INTO "preview data" (note) VALUES ('one'), ('two'), ('three');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenWritableAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
