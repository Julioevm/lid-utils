using System.Text.Json.Nodes;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class DecalCatalogServiceTests
{
    [Fact]
    public async Task LoadDecalsAsync_ResolvesNamesAndMetadataAndSkipsOtherPlatforms()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateDecalFixtureAsync(path, withText: true);
        var service = new ItemCatalogService();

        var result = await service.LoadDecalsAsync(path);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Definitions.Count);
        var premium = result.Definitions.Single(definition => definition.SkillId == "SKL_PREM");
        Assert.Equal("Heal Up", premium.DisplayName);
        Assert.True(premium.Premium);
        Assert.Equal(5, premium.Rarity);
        Assert.Equal("HPUP", premium.TypeLabel);
        var plain = result.Definitions.Single(definition => definition.SkillId == "SKL_PLAIN");
        Assert.False(plain.Premium);
        Assert.Equal(3, plain.Rarity);
        Assert.Null(result.Definitions.Single(definition => definition.SkillId == "SKL_NORARITY").Rarity);
        Assert.DoesNotContain(result.Definitions, definition => definition.SkillId == "SKL_OTHER_PLATFORM");
    }

    [Fact]
    public async Task LoadDecalsAsync_MissingTextTable_FallsBackToKeysWithWarning()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateDecalFixtureAsync(path, withText: false);
        var service = new ItemCatalogService();

        var result = await service.LoadDecalsAsync(path);

        Assert.Contains(result.Warnings, warning => warning.Contains("master_text", StringComparison.Ordinal));
        var definition = Assert.Single(result.Definitions, item => item.SkillId == "SKL_PREM");
        Assert.Equal("skl.heal_name", definition.DisplayName);
    }

    [Fact]
    public async Task LoadDecalsAsync_MissingSkillTable_ReturnsWarningAndNoDefinitions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await using (var connection = await OpenWritableAsync(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE master_skill_type (id TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var service = new ItemCatalogService();

        var result = await service.LoadDecalsAsync(path);

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Warnings, warning => warning.Contains("master_skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadDecalsAsync_MissingDatabasePath_ReturnsWarning()
    {
        var result = await new ItemCatalogService().LoadDecalsAsync("Z:\\does-not-exist\\masters.db");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    private static async Task CreateDecalFixtureAsync(string path, bool withText)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        var textTable = withText
            ? """
              CREATE TABLE master_text (sct TEXT, id TEXT, lang TEXT, txt TEXT);
              INSERT INTO master_text VALUES ('skl', 'heal_name', 'int', 'Heal Up');
              """
            : "";
        command.CommandText = $"""
            {textTable}
            CREATE TABLE master_skill (id TEXT, name TEXT, type TEXT, premium INTEGER, rarity INTEGER, platform INTEGER);
            INSERT INTO master_skill VALUES
                ('SKL_PREM', 'skl.heal_name', 'SKLTP_HPUP', 1, 5, 0),
                ('SKL_PLAIN', 'skl.plain', 'SKLTP_MONEYUP', 0, 3, 0),
                ('SKL_NORARITY', 'skl.norarity', 'SKLTP_X', 0, NULL, 0),
                ('SKL_OTHER_PLATFORM', 'skl.other', 'SKLTP_Y', 1, 4, 1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenWritableAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
