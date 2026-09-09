using System.Text.Json.Nodes;

namespace LidUtils.Core.Tests;

public sealed class DecalEngineTests
{
    [Fact]
    public void Apply_AppendsGrantToExistingArrayAndPreservesOtherRows()
    {
        const string json = """
            {"soul":{"skl":{"psskl":[
                {"sklid":"SKL_FREE","cnt":1,"updated":1596105334,"is_checked":0}
            ]}}}
            """;

        var updated = DecalEngine.Apply(json, [new GrantDecalOperation("SKL_NEW", 2)]);

        var inventory = DecalInventory.Read(updated);
        Assert.Equal(2, inventory.Owned.Count);
        var grant = inventory.Owned.Single(row => row.SkillId == "SKL_NEW");
        Assert.Equal(2, grant.Count);
        Assert.Equal(0, grant.IsChecked);
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - grant.Updated) < 60);
        var original = inventory.Owned.Single(row => row.SkillId == "SKL_FREE");
        Assert.Equal(1, original.Count);
        Assert.Equal(1596105334, original.Updated);
        Assert.Equal("/soul/skl/psskl/1/cnt", grant.CountPointer);
    }

    [Fact]
    public void Apply_CreatesMissingSklNodes()
    {
        const string json = """{"other":true}""";

        var updated = DecalEngine.Apply(json, [new GrantDecalOperation("SKL_NEW", 1)]);

        var inventory = DecalInventory.Read(updated);
        Assert.Equal("SKL_NEW", Assert.Single(inventory.Owned).SkillId);
        Assert.True(JsonNode.Parse(updated)!["other"]!.GetValue<bool>());
    }

    [Fact]
    public void Apply_ReplacesEmptyObjectPlaceholderWithArray()
    {
        const string json = """{"soul":{"skl":{"psskl":{}}}}""";

        var updated = DecalEngine.Apply(json, [new GrantDecalOperation("SKL_NEW", 3)]);

        Assert.Equal("SKL_NEW", Assert.Single(DecalInventory.Read(updated).Owned).SkillId);
    }

    [Fact]
    public void Apply_WithNoGrants_ReturnsTheOriginalJson()
    {
        const string json = """{"soul":{"skl":{"psskl":[]}}}""";

        Assert.Equal(json, DecalEngine.Apply(json, []));
    }

    [Fact]
    public void Apply_RejectsAGrantForAnOwnedDecal()
    {
        const string json = """
            {"soul":{"skl":{"psskl":[
                {"sklid":"SKL_FREE","cnt":1,"updated":1,"is_checked":0}
            ]}}}
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => DecalEngine.Apply(json, [new GrantDecalOperation("SKL_FREE", 1)]));

        Assert.Contains("already owns decal 'SKL_FREE'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsDuplicateGrantsInsideOneBatch()
    {
        const string json = """{"soul":{"skl":{"psskl":[]}}}""";

        var exception = Assert.Throws<InvalidOperationException>(() => DecalEngine.Apply(
            json,
            [new GrantDecalOperation("SKL_NEW", 1), new GrantDecalOperation("SKL_NEW", 2)]));

        Assert.Contains("already owns decal 'SKL_NEW'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsMalformedJson()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecalEngine.Apply("{not json", [new GrantDecalOperation("SKL_NEW", 1)]));
    }

    [Fact]
    public void Apply_RejectsNonObjectSklNode()
    {
        const string json = """{"soul":{"skl":42}}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => DecalEngine.Apply(json, [new GrantDecalOperation("SKL_NEW", 1)]));

        Assert.Contains("Expected /soul/skl to be an object", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Apply_RejectsQuantitiesOutsideTheCap(long quantity)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DecalEngine.Apply("""{"soul":{"skl":{"psskl":[]}}}""", [new GrantDecalOperation("SKL_NEW", quantity)]));

        Assert.Contains("must be between 1 and 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_ContainsSkill_MatchesOwnedRowsOnly()
    {
        var inventory = DecalInventory.Read("""
            {"soul":{"skl":{
                "psskl":[{"sklid":"SKL_FREE","cnt":1,"updated":1,"is_checked":0}],
                "eqskl":{"1":[]}
            }}}
            """);

        Assert.True(inventory.ContainsSkill("SKL_FREE"));
        Assert.False(inventory.ContainsSkill("SKL_NEW"));
    }
}
