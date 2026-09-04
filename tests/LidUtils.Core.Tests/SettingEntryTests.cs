using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class SettingEntryTests
{
    [Fact]
    public void UndocumentedEntry_IsClearlyMarkedAndRetainsRawValue()
    {
        var entry = new SettingEntry("PLAYER_HEALTH", "001", SettingValueType.Integer, "master_const_int", false);

        Assert.Equal("Undocumented", entry.Category);
        Assert.Equal("001", entry.RawValue);
        Assert.False(entry.IsDocumented);
    }
}
