using LidUtils.Data;
using LidUtils.Core;

namespace LidUtils.Data.Tests;

public sealed class RealDatabaseSmokeTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task Validate_AcceptsExplicitLocalDatabaseInReadOnlyMode()
    {
        var databasePath = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_DB");
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            // Opt-in only: ordinary test runs must not depend on a game installation.
            return;
        }

        var writeTimeBefore = File.GetLastWriteTimeUtc(databasePath);
        var result = await new DatabaseValidator().ValidateAsync(databasePath);

        Assert.True(result.IsValid, result.Message);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata.TableCount >= 3);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(databasePath));

        var browser = new ReadOnlyDatabaseBrowser();
        var settings = await browser.LoadSettingsAsync(databasePath);
        var schema = await browser.LoadSchemaAsync(databasePath);
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "settings", "settings.catalog.json"));
        var catalog = SettingsCatalogLoader.Load(catalogPath);
        var cataloguedSettings = catalog.Apply(settings.Entries);

        Assert.NotEmpty(settings.Entries);
        Assert.Equal(settings.Entries.Count, cataloguedSettings.Entries.Count);
        Assert.Empty(settings.Warnings);
        Assert.Contains(schema, table => table.Name == "master_const_int");
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(databasePath));
    }
}
