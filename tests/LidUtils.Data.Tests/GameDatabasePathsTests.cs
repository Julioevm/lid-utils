using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class GameDatabasePathsTests
{
    [Fact]
    public void GetDatabasePath_CombinesContentSubPath()
    {
        var path = GameDatabasePaths.GetDatabasePath(@"C:\Games\LET IT DIE");

        Assert.Equal(Path.Combine(@"C:\Games\LET IT DIE", "BrgGame", "Content", "masters.db"), path);
    }

    [Fact]
    public void GetSaveDataDirectory_CombinesSavedataSubPath()
    {
        var path = GameDatabasePaths.GetSaveDataDirectory(@"C:\Games\LET IT DIE");

        Assert.Equal(Path.Combine(@"C:\Games\LET IT DIE", "Savedata"), path);
    }

    [Fact]
    public void TryGetInstallRoot_ExtractsRootFromStandardDatabaseLayout()
    {
        var databasePath = Path.Combine(@"C:", "SteamLibrary", "steamapps", "common", "LET IT DIE", "BrgGame", "Content", "masters.db");

        var installRoot = GameDatabasePaths.TryGetInstallRoot(databasePath);

        Assert.Equal(Path.Combine(@"C:", "SteamLibrary", "steamapps", "common", "LET IT DIE"), installRoot);
    }

    [Fact]
    public void TryGetInstallRoot_IsCaseInsensitive()
    {
        var databasePath = Path.Combine(@"C:", "Games", "LID", "brggame", "content", "masters.db");

        var installRoot = GameDatabasePaths.TryGetInstallRoot(databasePath);

        Assert.Equal(Path.Combine(@"C:", "Games", "LID"), installRoot);
    }

    [Fact]
    public void TryGetInstallRoot_RejectsNonStandardLayouts()
    {
        Assert.Null(GameDatabasePaths.TryGetInstallRoot(@"C:\Some\Masters\masters.db"));
        Assert.Null(GameDatabasePaths.TryGetInstallRoot(Path.Combine(@"C:\Games", "BrgGame", "masters.db")));
    }
}
