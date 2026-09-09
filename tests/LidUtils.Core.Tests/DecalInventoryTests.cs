using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class DecalInventoryTests
{
    [Fact]
    public void Read_ParsesOwnedRowsAndEquippedRecordsAcrossLoadouts()
    {
        var json = """
        {
          "soul": {
            "skl": {
              "psskl": [
                { "sklid": "SKL_ATKUP_01_P", "cnt": 2, "updated": 100, "is_checked": 1 },
                { "sklid": "SKL_HPUP_01", "cnt": 0, "updated": 200, "is_checked": 0 }
              ],
              "eqskl": {
                "1": [
                  { "cid": "fighter-a", "sklid": "SKL_HPUP_01", "slot": 0 },
                  { "cid": "fighter-b", "sklid": "SKL_HPUP_01", "slot": 1 }
                ],
                "-2": {},
                "424242": [
                  { "cid": "fighter-c", "sklid": "SKL_ATKUP_01_P", "slot": 3 }
                ]
              }
            }
          }
        }
        """;

        var inventory = DecalInventory.Read(json);

        Assert.Equal(2, inventory.Owned.Count);
        var first = inventory.Owned[0];
        Assert.Equal(0, first.Index);
        Assert.Equal("SKL_ATKUP_01_P", first.SkillId);
        Assert.Equal(2, first.Count);
        Assert.Equal(100, first.Updated);
        Assert.Equal(1, first.IsChecked);
        Assert.Equal("/soul/skl/psskl/0/cnt", first.CountPointer);
        Assert.Equal("/soul/skl/psskl/1/cnt", inventory.Owned[1].CountPointer);

        Assert.Equal(3, inventory.Equipped.Count);
        Assert.Equal(2, inventory.EquippedCount("SKL_HPUP_01"));
        Assert.Equal(1, inventory.EquippedCount("SKL_ATKUP_01_P"));
        Assert.Equal(0, inventory.EquippedCount("SKL_UNKNOWN"));
        var equipped = Assert.Single(inventory.Equipped, entry => entry.LoadoutKey == "424242");
        Assert.Equal("fighter-c", equipped.FighterId);
        Assert.Equal(3, equipped.Slot);
    }

    [Fact]
    public void Read_MissingSkillNodes_ReturnsEmptyInventory()
    {
        var inventory = DecalInventory.Read("""{ "soul": {} }""");

        Assert.Empty(inventory.Owned);
        Assert.Empty(inventory.Equipped);
    }

    [Fact]
    public void Read_EmptyPssklAndEmptyObjectEquskl_ReturnsEmptyInventory()
    {
        var inventory = DecalInventory.Read("""
        { "soul": { "skl": { "psskl": [], "eqskl": { "-1": {}, "-2": {} } } } }
        """);

        Assert.Empty(inventory.Owned);
        Assert.Empty(inventory.Equipped);
    }

    [Fact]
    public void Read_MissingOptionalRowFields_DefaultsToZero()
    {
        var inventory = DecalInventory.Read("""
        { "soul": { "skl": { "psskl": [ { "sklid": "SKL_X", "cnt": 1 } ] } } }
        """);

        var row = Assert.Single(inventory.Owned);
        Assert.Equal(0, row.Updated);
        Assert.Equal(0, row.IsChecked);
    }

    [Fact]
    public void Read_MalformedPssklRow_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DecalInventory.Read(
            """{ "soul": { "skl": { "psskl": [ { "cnt": 1 } ] } } }"""));
        Assert.Throws<InvalidOperationException>(() => DecalInventory.Read(
            """{ "soul": { "skl": { "psskl": [ "not-an-object" ] } } }"""));
    }

    [Fact]
    public void Read_NonArrayPsskl_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DecalInventory.Read(
            """{ "soul": { "skl": { "psskl": { "0": {} } } } }"""));
    }

    [Fact]
    public void Read_EquippedRecordMissingFighter_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DecalInventory.Read(
            """{ "soul": { "skl": { "eqskl": { "1": [ { "sklid": "SKL_X", "slot": 0 } ] } } } }"""));
    }

    [Fact]
    public void Read_EmptyOrInvalidJson_Throws()
    {
        Assert.Throws<ArgumentException>(() => DecalInventory.Read("   "));
        Assert.Throws<InvalidOperationException>(() => DecalInventory.Read("not json"));
    }

    [Fact]
    public void MaximumQuantity_IsFour()
    {
        Assert.Equal(4, DecalInventory.MaximumQuantity);
    }
}
