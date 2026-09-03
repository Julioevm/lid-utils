using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class VdfLibraryParserTests
{
    [Fact]
    public void ParseLibraryPaths_ReadsModernAndLegacyEntriesWithoutAppIds()
    {
        const string contents = """
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Program Files (x86)\\Steam"
                    "apps"
                    {
                        "794600" "123456"
                    }
                }
                "1" "D:\\SteamLibrary"
            }
            """;

        var paths = VdfLibraryParser.ParseLibraryPaths(contents);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], paths);
    }
}

