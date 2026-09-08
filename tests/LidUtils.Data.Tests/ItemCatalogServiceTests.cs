using System.Text.Json.Nodes;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class ItemCatalogServiceTests
{
    [Fact]
    public async Task LoadAndCreateTemplate_UsesValidatedMasterBaselinesAndKeepsUnsupportedDefinitionsBrowsable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateCatalogFixtureAsync(path);
        var service = new ItemCatalogService();

        var catalog = await service.LoadAsync(path);

        Assert.Contains(catalog.Warnings, warning => warning.Contains("master_text", StringComparison.Ordinal));
        var gun = Assert.Single(catalog.Entries, entry => entry.DefinitionId == "GUN");
        var armor = Assert.Single(catalog.Entries, entry => entry.DefinitionId == "ARMOR");
        var internalPart = Assert.Single(catalog.Entries, entry => entry.DefinitionId == "MASK");
        var unavailableItem = Assert.Single(catalog.Entries, entry => entry.DefinitionId == "EVENT");
        Assert.True(gun.IsTemplateSupported);
        Assert.True(armor.IsTemplateSupported);
        Assert.False(internalPart.IsTemplateSupported);
        Assert.Contains("type", internalPart.TemplateUnsupportedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(unavailableItem.IsTemplateSupported);

        var gunTemplate = service.CreateTemplate(gun).Template!;
        var gunJson = JsonNode.Parse(gunTemplate.EntityJson)!.AsObject();
        Assert.Equal(12, gunJson["rest"]!.GetValue<int>());
        Assert.Equal(48, gunJson["spare"]!.GetValue<int>());
        Assert.Equal(0, gunJson["grade"]!.GetValue<int>());
        Assert.Equal(400, gunJson["dur"]!.GetValue<int>());
        Assert.False(service.CreateTemplate(internalPart).IsSuccess);
        Assert.False(service.CreateTemplate(unavailableItem).IsSuccess);

        var beast = Assert.Single(catalog.Entries, entry => entry.Category == ItemCatalogCategory.Beast && entry.IsTemplateSupported);
        var beastTemplate = service.CreateTemplate(beast).Template!;
        Assert.NotNull(beastTemplate.LinkedMushroomJson);
        Assert.Equal("MUSH", JsonNode.Parse(beastTemplate.LinkedMushroomJson!)!["msrid"]!.GetValue<string>());
        Assert.Equal(0, JsonNode.Parse(beastTemplate.LinkedMushroomJson!)!["state"]!.GetValue<int>());
    }

    [Fact]
    public async Task CreateTemplate_RejectsEntriesFromPreviousCatalogSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.db");
        var second = Path.Combine(directory.Path, "second.db");
        await CreateCatalogFixtureAsync(first);
        await CreateCatalogFixtureAsync(second);
        var service = new ItemCatalogService();
        var stale = (await service.LoadAsync(first)).Entries.First(entry => entry.DefinitionId == "GUN");
        await service.LoadAsync(second);

        var result = service.CreateTemplate(stale);

        Assert.False(result.IsSuccess);
        Assert.Contains("stale", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_PartialSchemaShowsDefinitionsButDoesNotConstructThem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "partial.db");
        await using (var connection = await OpenWritableAsync(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE master_part (id TEXT, name TEXT); INSERT INTO master_part VALUES ('BROKEN', 'part.broken');";
            await command.ExecuteNonQueryAsync();
        }
        var service = new ItemCatalogService();

        var catalog = await service.LoadAsync(path);
        var entry = Assert.Single(catalog.Entries);

        Assert.False(entry.IsTemplateSupported);
        Assert.Contains("incomplete", catalog.Warnings.Single(warning => warning.Contains("master_part", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
        Assert.False(service.CreateTemplate(entry).IsSuccess);
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task ExplicitInstalledDatabase_CreatesOneSupportedTemplateOfEachTypeAndAppliesThemToMemoryOnly()
    {
        var path = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_DB");
        if (string.IsNullOrWhiteSpace(path)) return;
        var before = File.GetLastWriteTimeUtc(path);
        var service = new ItemCatalogService();
        var catalog = await service.LoadAsync(path);
        var templates = Enum.GetValues<ItemCatalogCategory>().Select(category =>
        {
            var entry = catalog.Entries.First(item => item.Category == category && item.IsTemplateSupported);
            return service.CreateTemplate(entry).Template!;
        }).ToArray();

        var applied = StorageEngine.Apply(EmptySave(), templates.Select((template, slot) =>
            (StorageOperation)new SetStorageSlotOperation(slot, template)).ToArray());

        Assert.Equal(4, StorageEngine.Read(applied).OccupiedCount);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    private static string EmptySave() => """
        {"user":{"uid":7},"soul":{"uid":7,"cl":[{"slot":0,"type":-1,"eid":""},{"slot":1,"type":-1,"eid":""},{"slot":2,"type":-1,"eid":""},{"slot":3,"type":-1,"eid":""}]},"part":{"pts":{"7":[]}},"item":{"items":[]},"mushroom":{"msrs":[]},"beast":{"bsts":[]}}
        """;

    private static async Task CreateCatalogFixtureAsync(string path)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE master_part (id TEXT, name TEXT, type TEXT, platform INTEGER, dur INTEGER, capacity INTEGER, spare INTEGER);
            CREATE TABLE master_item (itemid TEXT, name TEXT, platform INTEGER);
            CREATE TABLE master_mushroom (id TEXT, c_name TEXT);
            CREATE TABLE master_beast (id TEXT, name TEXT, rwdmsrid TEXT);
            INSERT INTO master_part VALUES
                ('GUN', 'part.gun', 'PTTP_ARM', 0, 400, 12, 48),
                ('ARMOR', 'part.armor', 'PTTP_BODY', 0, 500, 0, 0),
                ('MASK', 'part.mask', 'PTTP_MASK', 0, 1, 0, 0),
                ('EVENT_PART', 'part.event', 'PTTP_HEAD', 1, 1, 0, 0);
            INSERT INTO master_item VALUES ('MAT', 'item.mat', 0), ('EVENT', 'item.event', 1);
            INSERT INTO master_mushroom VALUES ('MUSH', 'mush.name');
            INSERT INTO master_beast VALUES ('BEAST', 'beast.name', 'MUSH'), ('BAD_BEAST', 'beast.bad', 'UNKNOWN');
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
