using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class DatabaseFileMetadataTests
{
    [Fact]
    public void ShortFingerprints_AreLimitedToTwelveCharacters()
    {
        var metadata = new DatabaseFileMetadata(
            "masters.db",
            42,
            DateTime.UnixEpoch,
            "1234567890ABCDEF",
            "ABCDEF1234567890",
            3,
            0,
            0);

        Assert.Equal("1234567890AB", metadata.ShortDatabaseFingerprint);
        Assert.Equal("ABCDEF123456", metadata.ShortSchemaFingerprint);
    }
}

