namespace LidUtils.Data;

public static class GameDatabasePaths
{
    public const string RequestedDefault =
        @"D:\SteamLibrary\steamapps\common\LET IT DIE\BrgGame\Content\masters.db";

    public static readonly string RelativeToLibrary = Path.Combine(
        "steamapps",
        "common",
        "LET IT DIE",
        "BrgGame",
        "Content",
        "masters.db");

    public static string GetDatabasePath(string installRoot) => Path.Combine(
        installRoot,
        "BrgGame",
        "Content",
        "masters.db");

    public static string GetSaveDataDirectory(string installRoot) => Path.Combine(installRoot, "Savedata");

    /// <summary>
    /// Extracts the game installation root from a masters.db path that follows the
    /// standard "&lt;install root&gt;\BrgGame\Content\masters.db" layout. Returns null for other layouts.
    /// </summary>
    public static string? TryGetInstallRoot(string databasePath)
    {
        var contentDirectory = Path.GetDirectoryName(databasePath);
        if (contentDirectory is null ||
            !string.Equals(Path.GetFileName(contentDirectory), "Content", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gameDirectory = Path.GetDirectoryName(contentDirectory);
        if (gameDirectory is null ||
            !string.Equals(Path.GetFileName(gameDirectory), "BrgGame", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var installRoot = Path.GetDirectoryName(gameDirectory);
        return string.IsNullOrWhiteSpace(installRoot) ? null : installRoot;
    }
}
