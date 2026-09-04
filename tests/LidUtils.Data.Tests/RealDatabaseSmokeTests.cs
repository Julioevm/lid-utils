using LidUtils.Data;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

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

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task ApplyAndRestore_OperateOnlyOnTemporaryCopyOfExplicitDatabase()
    {
        var databasePath = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_DB");
        if (string.IsNullOrWhiteSpace(databasePath)) return;

        var validator = new DatabaseValidator();
        var originalBefore = await validator.ValidateAsync(databasePath);
        Assert.True(originalBefore.IsValid, originalBefore.Message);
        using var temporaryDirectory = new TemporaryDirectory();
        var copyPath = Path.Combine(temporaryDirectory.Path, "masters.db");
        await using (var source = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
        await using (var destination = new SqliteConnection($"Data Source={copyPath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await source.OpenAsync();
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }

        var copyMetadata = await validator.ValidateAsync(copyPath);
        var entry = (await new ReadOnlyDatabaseBrowser().LoadSettingsAsync(copyPath)).Entries
            .First(item => item.ValueType == SettingValueType.Integer && !item.IsNull);
        var oldValue = long.Parse(entry.RawValue);
        var newValue = oldValue == long.MaxValue ? oldValue - 1 : oldValue + 1;
        var change = new StagedSettingChange(entry, newValue.ToString(), true, null, []);
        var service = new DatabaseMaintenanceService(
            validator,
            Path.Combine(temporaryDirectory.Path, "backups"),
            Path.Combine(temporaryDirectory.Path, "audit"),
            () => false);

        var applied = await service.ApplyAsync(copyMetadata.Metadata!, [change], 5);
        Assert.NotEqual(copyMetadata.Metadata!.DatabaseSha256, applied.UpdatedMetadata.DatabaseSha256);
        var restored = await service.RestoreAsync(copyPath, applied.Backup.Id, 5);
        Assert.Equal(copyMetadata.Metadata.SchemaSha256, restored.RestoredMetadata.SchemaSha256);
        var restoredEntry = (await new ReadOnlyDatabaseBrowser().LoadSettingsAsync(copyPath)).Entries.Single(item => item.Id == entry.Id);
        Assert.Equal(entry.RawValue, restoredEntry.RawValue);

        var originalAfter = await validator.ValidateAsync(databasePath);
        Assert.Equal(originalBefore.Metadata!.DatabaseSha256, originalAfter.Metadata!.DatabaseSha256);
        Assert.Equal(originalBefore.Metadata.LastWriteTimeUtc, originalAfter.Metadata.LastWriteTimeUtc);
    }
}
