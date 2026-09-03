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
}

