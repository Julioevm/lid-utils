using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class WeaponSkillInventoryTests
{
    [Fact]
    public void Read_ResolvesWeaponSkillRowsAndPointers()
    {
        const string json = """
            {"soul":{"expert":[{"ptarmtp":"PTARMTP_01","abp":602,"lvl":7,"is_checked":1},{"ptarmtp":"PTARMTP_02","abp":-1,"lvl":1,"is_checked":0}]}}
            """;

        var inventory = WeaponSkillInventory.Read(json);

        Assert.Empty(inventory.Warnings);
        Assert.Equal(2, inventory.Skills.Count);
        var first = inventory.Skills[0];
        Assert.Equal("PTARMTP_01", first.WeaponType);
        Assert.Equal(7, first.Level);
        Assert.Equal(602, first.Abp);
        Assert.True(first.IsChecked);
        Assert.Equal("/soul/expert/0/lvl", first.LevelPointer);
        Assert.Equal("/soul/expert/0/abp", first.AbpPointer);
        var second = inventory.Skills[1];
        Assert.Equal(1, second.Level);
        Assert.Equal(-1, second.Abp);
        Assert.False(second.IsChecked);
        Assert.Equal("/soul/expert/1/lvl", second.LevelPointer);
    }

    [Fact]
    public void Read_MissingExpertArray_ReturnsWarningAndNoRows()
    {
        var inventory = WeaponSkillInventory.Read("""{"soul":{"uid":1}}""");

        Assert.Empty(inventory.Skills);
        Assert.Contains(inventory.Warnings, warning => warning.Contains("/soul/expert", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_SkipsRowsWithoutWeaponTypeOrLevel()
    {
        const string json = """
            {"soul":{"expert":[{"abp":5,"lvl":2},{"ptarmtp":"PTARMTP_03","abp":5},{"ptarmtp":"PTARMTP_04","abp":5,"lvl":3}]}}
            """;

        var inventory = WeaponSkillInventory.Read(json);

        var row = Assert.Single(inventory.Skills);
        Assert.Equal("PTARMTP_04", row.WeaponType);
        Assert.Equal(2, inventory.Warnings.Count);
    }
}
