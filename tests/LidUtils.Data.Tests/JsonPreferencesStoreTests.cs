using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class JsonPreferencesStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSelectedPath()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings", "settings.json");
        var store = new JsonPreferencesStore(settingsPath);

        await store.SaveAsync(new AppPreferences(
            @"D:\Games\masters.db",
            ["master_const_int:PLAYER_HEALTH"],
            ["master_const_float:WORLD_SPEED", "master_const_int:PLAYER_HEALTH"]));
        var loaded = await store.LoadAsync();

        Assert.Equal(@"D:\Games\masters.db", loaded.LastDatabasePath);
        Assert.Equal(["master_const_int:PLAYER_HEALTH"], loaded.FavoriteSettingIds);
        Assert.Equal(["master_const_float:WORLD_SPEED", "master_const_int:PLAYER_HEALTH"], loaded.RecentlyViewedSettingIds);
    }

    [Fact]
    public async Task Load_ReturnsDefaultsForMalformedJson()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{not-json");
        var store = new JsonPreferencesStore(settingsPath);

        var loaded = await store.LoadAsync();

        Assert.Null(loaded.LastDatabasePath);
    }
}
