using System.Text.Json.Nodes;
using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class StorageEngineTests
{
    private const string LockerPartId = "11111111-1111-1111-1111-111111111111";
    private const string BagPartId = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("76561197974144168.json", 1, 30, 3)]
    [InlineData("76561197974144168_old.json", 117305, 290, 277)]
    public void SuppliedSaveSamples_ReadAndExpandWithoutChangingAnythingOutsideLocker(
        string fileName,
        int expectedUid,
        int expectedCapacity,
        int expectedOccupied)
    {
        var path = FindRepositoryFile("data", fileName);
        var json = File.ReadAllText(path);

        var inventory = StorageEngine.Read(json);
        var edited = StorageEngine.Apply(json, [new ExpandStorageOperation()]);
        var expanded = StorageEngine.Read(edited);

        Assert.Equal(expectedUid, inventory.PlayerUid);
        Assert.Equal(expectedCapacity, inventory.Capacity);
        Assert.Equal(expectedOccupied, inventory.OccupiedCount);
        Assert.Equal(expectedCapacity + 10, expanded.Capacity);
        Assert.Equal(expectedOccupied, expanded.OccupiedCount);

        var originalRoot = JsonNode.Parse(json)!.AsObject();
        var editedRoot = JsonNode.Parse(edited)!.AsObject();
        Assert.True(originalRoot["soul"]!.AsObject().Remove("cl"));
        Assert.True(editedRoot["soul"]!.AsObject().Remove("cl"));
        Assert.True(JsonNode.DeepEquals(originalRoot, editedRoot),
            "Expanding storage changed data outside /soul/cl.");
    }

    [Theory]
    [InlineData("76561197974144168.json")]
    [InlineData("76561197974144168_old.json")]
    public void SuppliedSaveSamples_ClearLockerEntityWithoutChangingFighterBagsOrSkills(string fileName)
    {
        var json = File.ReadAllText(FindRepositoryFile("data", fileName));
        var original = JsonNode.Parse(json)!.AsObject();
        var inventory = StorageEngine.Read(json);
        var target = inventory.Slots.First(slot => slot.IsOccupied);

        var editedJson = StorageEngine.Apply(json, [new ClearStorageSlotOperation(target.Slot)]);
        var edited = JsonNode.Parse(editedJson)!.AsObject();
        var editedInventory = StorageEngine.Read(editedJson);

        Assert.False(editedInventory.Slots.Single(slot => slot.Slot == target.Slot).IsOccupied);
        Assert.Equal(inventory.OccupiedCount - 1, editedInventory.OccupiedCount);
        Assert.True(JsonNode.DeepEquals(original["soul"]!["deathbag"], edited["soul"]!["deathbag"]));
        Assert.True(JsonNode.DeepEquals(original["soul"]!["skl"], edited["soul"]!["skl"]));
    }

    [Fact]
    public void Read_ResolvesDynamicUidAndLockerCapacity()
    {
        var inventory = StorageEngine.Read(SaveJson());

        Assert.Equal(117305, inventory.PlayerUid);
        Assert.Equal(2, inventory.Capacity);
        Assert.Equal(1, inventory.OccupiedCount);
        Assert.Equal("P_START", inventory.Slots.Single(slot => slot.Slot == 0).DefinitionId);
        Assert.Equal(LockerPartId, inventory.Slots.Single(slot => slot.Slot == 0).EntityId);
    }

    [Fact]
    public void Apply_ExpandsByTen_AndNeverChangesFighterBags()
    {
        var edited = StorageEngine.Apply(SaveJson(), [new ExpandStorageOperation()]);
        var root = JsonNode.Parse(edited)!.AsObject();

        var locker = root["soul"]!["cl"]!.AsArray();
        Assert.Equal(12, locker.Count);
        Assert.Equal(2, locker[2]!["slot"]!.GetValue<int>());
        Assert.Equal(-1, locker[2]!["type"]!.GetValue<int>());
        Assert.Equal(string.Empty, locker[2]!["eid"]!.GetValue<string>());
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", root["soul"]!["deathbag"]!["117305"]!["77"]!.ToJsonString());
    }

    [Fact]
    public void Apply_ReplacesAndClearsExclusiveLockerEntity_PreservingFighterBag()
    {
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\",\"gettime\":0}");
        var replaced = StorageEngine.Apply(SaveJson(), [new SetStorageSlotOperation(0, template)]);
        var replacedRoot = JsonNode.Parse(replaced)!.AsObject();
        var replacementId = replacedRoot["soul"]!["cl"]![0]!["eid"]!.GetValue<string>();
        var replacement = replacedRoot["item"]!["items"]!.AsArray().Single()!.AsObject();

        Assert.True(Guid.TryParse(replacementId, out _));
        Assert.NotEqual(LockerPartId, replacementId);
        Assert.Equal(replacementId, replacement["eid"]!.GetValue<string>());
        Assert.Equal("COIN_LOCKER", replacement["owner"]!.GetValue<string>());
        Assert.True(replacement["gettime"]!.GetValue<long>() > 0);
        Assert.False(replacement.ContainsKey("uid"));
        Assert.DoesNotContain(replacedRoot["part"]!["pts"]!["117305"]!.AsArray(), value => value!["eid"]!.GetValue<string>() == LockerPartId);
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", replacedRoot["soul"]!["deathbag"]!["117305"]!["77"]!.ToJsonString());

        var cleared = StorageEngine.Apply(replaced, [new ClearStorageSlotOperation(0)]);
        var clearedRoot = JsonNode.Parse(cleared)!.AsObject();
        Assert.Equal(string.Empty, clearedRoot["soul"]!["cl"]![0]!["eid"]!.GetValue<string>());
        Assert.Equal(-1, clearedRoot["soul"]!["cl"]![0]!["type"]!.GetValue<int>());
        Assert.Empty(clearedRoot["item"]!["items"]!.AsArray());
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", clearedRoot["soul"]!["deathbag"]!["117305"]!["77"]!.ToJsonString());
    }

    [Fact]
    public void Apply_AddsBeastReward_AndRemovesItsPrivateRewardWhenBeastIsCleared()
    {
        var template = new StorageItemTemplate(2, "BST_DOG", "Dog",
            "{\"bstid\":\"BST_DOG\",\"rwdemsrid\":\"\",\"gettime\":0}",
            "{\"msrid\":\"MSR_REWARD\",\"gettime\":0}");

        var added = StorageEngine.Apply(SaveJson(), [new SetStorageSlotOperation(1, template)]);
        var addedRoot = JsonNode.Parse(added)!.AsObject();
        var beast = addedRoot["beast"]!["bsts"]!.AsArray().Single()!.AsObject();
        var mushroom = addedRoot["mushroom"]!["msrs"]!.AsArray().Single()!.AsObject();
        Assert.Equal("COIN_LOCKER", beast["owner"]!.GetValue<string>());
        Assert.Equal("BEAST", mushroom["owner"]!.GetValue<string>());
        Assert.Equal(mushroom["eid"]!.GetValue<string>(), beast["rwdemsrid"]!.GetValue<string>());
        Assert.True(beast["gettime"]!.GetValue<long>() > 0);

        var cleared = StorageEngine.Apply(added, [new ClearStorageSlotOperation(1)]);
        var clearedRoot = JsonNode.Parse(cleared)!.AsObject();
        Assert.Empty(clearedRoot["beast"]!["bsts"]!.AsArray());
        Assert.Empty(clearedRoot["mushroom"]!["msrs"]!.AsArray());
    }

    [Fact]
    public void Apply_ClearingBeastPreservesUserOwnedCookedReward()
    {
        const string beastId = "33333333-3333-3333-3333-333333333333";
        const string rewardId = "44444444-4444-4444-4444-444444444444";
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        root["soul"]!["cl"]![1]!["type"] = 2;
        root["soul"]!["cl"]![1]!["eid"] = beastId;
        root["beast"]!["bsts"]!.AsArray().Add(new JsonObject
        {
            ["eid"] = beastId,
            ["bstid"] = "BST_FROG",
            ["owner"] = "COIN_LOCKER",
            ["rwdemsrid"] = rewardId,
            ["state"] = 1,
            ["posonce"] = 1
        });
        root["mushroom"]!["msrs"]!.AsArray().Add(new JsonObject
        {
            ["eid"] = rewardId,
            ["msrid"] = "MSR_FROG",
            ["owner"] = "USER"
        });

        var cleared = JsonNode.Parse(StorageEngine.Apply(
            root.ToJsonString(),
            [new ClearStorageSlotOperation(1)]))!.AsObject();

        Assert.Empty(cleared["beast"]!["bsts"]!.AsArray());
        Assert.Single(cleared["mushroom"]!["msrs"]!.AsArray());
        Assert.Equal(rewardId, cleared["mushroom"]!["msrs"]![0]!["eid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("eid")]
    [InlineData("eptid")]
    [InlineData("rwdemsrid")]
    public void Apply_RejectsKnownEntityReferencesIncludingCsv(string referenceProperty)
    {
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        root["soul"]!["relationship"] = new JsonObject { [referenceProperty] = $"unused,{LockerPartId}" };

        var exception = Assert.Throws<InvalidOperationException>(() => StorageEngine.Apply(root.ToJsonString(), [new ClearStorageSlotOperation(0)]));

        Assert.Contains("also referenced", exception.Message);
    }

    [Fact]
    public void Apply_RejectsNumericEntityIdsAndUidMismatches()
    {
        var invalidId = SaveJson().Replace($"\"{LockerPartId}\"", "100", StringComparison.Ordinal);
        var mismatchRoot = JsonNode.Parse(SaveJson())!.AsObject();
        mismatchRoot["soul"]!["uid"] = 7;

        Assert.Contains("UUID", Assert.Throws<InvalidOperationException>(() => StorageEngine.Read(invalidId)).Message);
        Assert.Contains("differs", Assert.Throws<InvalidOperationException>(() => StorageEngine.Read(mismatchRoot.ToJsonString())).Message);
    }

    [Fact]
    public void Apply_NormalizesOnlyEmptyObjectRegistryPlaceholders()
    {
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\"}");
        var empty = SaveJson().Replace("\"items\": []", "\"items\": {}", StringComparison.Ordinal);
        var changed = StorageEngine.Apply(empty, [new SetStorageSlotOperation(1, template)]);
        Assert.IsType<JsonArray>(JsonNode.Parse(changed)!["item"]!["items"]);

        var malformed = SaveJson().Replace("\"items\": []", "\"items\": {\"doNotDiscard\":true}", StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidOperationException>(() => StorageEngine.Apply(malformed, [new SetStorageSlotOperation(1, template)]));
        Assert.Contains("Expected", exception.Message);
    }

    [Fact]
    public void Apply_RejectsTemplatesWithWrongTypeOrDefinition()
    {
        var wrongType = new StorageItemTemplate(0, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\"}");
        var wrongDefinition = new StorageItemTemplate(3, "IT_OTHER", "Other", "{\"itemid\":\"IT_HEAL\"}");

        Assert.Contains("does not match", Assert.Throws<InvalidOperationException>(() => StorageEngine.Apply(SaveJson(), [new SetStorageSlotOperation(1, wrongType)])).Message);
        Assert.Contains("does not match definition", Assert.Throws<InvalidOperationException>(() => StorageEngine.Apply(SaveJson(), [new SetStorageSlotOperation(1, wrongDefinition)])).Message);
    }

    private static string SaveJson() => $$"""
        {
          "user": { "uid": 117305 },
          "soul": {
            "cl": [
              { "slot": 0, "type": 0, "eid": "{{LockerPartId}}" },
              { "slot": 1, "type": -1, "eid": "" }
            ],
            "deathbag": { "117305": { "77": [ { "eid": "{{BagPartId}}" } ] } }
          },
          "part": { "pts": { "117305": [
            { "eid": "{{LockerPartId}}", "ptid": "P_START", "uid": 117305, "owner": "COIN_LOCKER" },
            { "eid": "{{BagPartId}}", "ptid": "P_BAG", "uid": 117305, "owner": "USER" }
          ] } },
          "item": { "items": [] },
          "mushroom": { "msrs": [] },
          "beast": { "bsts": [] }
        }
        """;

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not find repository fixture '{Path.Combine(segments)}'.");
    }
}
