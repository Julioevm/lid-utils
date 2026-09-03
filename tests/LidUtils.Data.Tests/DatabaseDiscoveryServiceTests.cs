using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class DatabaseDiscoveryServiceTests
{
    [Fact]
    public async Task GetCandidates_DefaultPathIsAlwaysFirst()
    {
        var service = new DatabaseDiscoveryService(steamRootsOverride: []);

        var candidates = await service.GetCandidatesAsync(null);

        var first = Assert.Single(candidates);
        Assert.Equal(GameDatabasePaths.RequestedDefault, first.Path);
        Assert.Equal(DatabaseCandidateSource.DefaultPath, first.Source);
    }

    [Fact]
    public async Task FindFirstExisting_DiscoversDatabaseInConfiguredSteamLibrary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var steamRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Steam");
        var libraryRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Games");
        var steamApps = System.IO.Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(steamApps);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(steamApps, "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\"1\"\n{{\n\"path\" \"{libraryRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n}}\n}}");

        var databasePath = System.IO.Path.Combine(libraryRoot, GameDatabasePaths.RelativeToLibrary);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        await File.WriteAllBytesAsync(databasePath, [1]);

        var service = new DatabaseDiscoveryService(
            steamRootsOverride: [steamRoot],
            requestedDefaultPathOverride: System.IO.Path.Combine(temporaryDirectory.Path, "missing", "masters.db"));
        var candidate = await service.FindFirstExistingAsync(null);

        Assert.NotNull(candidate);
        Assert.Equal(databasePath, candidate.Path, ignoreCase: true);
        Assert.Equal(DatabaseCandidateSource.SteamLibrary, candidate.Source);
    }

    [Fact]
    public async Task FindFirstExisting_PrefersAnExistingRememberedSelection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var defaultPath = System.IO.Path.Combine(temporaryDirectory.Path, "default", "masters.db");
        var rememberedPath = System.IO.Path.Combine(temporaryDirectory.Path, "remembered", "masters.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(defaultPath)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(rememberedPath)!);
        await File.WriteAllBytesAsync(defaultPath, [1]);
        await File.WriteAllBytesAsync(rememberedPath, [1]);

        var service = new DatabaseDiscoveryService(
            steamRootsOverride: [],
            requestedDefaultPathOverride: defaultPath);

        var candidate = await service.FindFirstExistingAsync(rememberedPath);

        Assert.NotNull(candidate);
        Assert.Equal(rememberedPath, candidate.Path, ignoreCase: true);
        Assert.Equal(DatabaseCandidateSource.RememberedSelection, candidate.Source);
    }
}
