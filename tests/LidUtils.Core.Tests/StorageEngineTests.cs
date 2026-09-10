using System.Text.Json.Nodes;
using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class StorageEngineTests
{
    private const string LockerPartId = "11111111-1111-1111-1111-111111111111";
    private const string BagPartId = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("save_current.json", 1, 30, 3)]
    [InlineData("save_progress.json", 424242, 290, 277)]
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
    [InlineData("save_current.json")]
    [InlineData("save_progress.json")]
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

        Assert.Equal(424242, inventory.PlayerUid);
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
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", root["soul"]!["deathbag"]!["424242"]!["77"]!.ToJsonString());
    }

    [Theory]
    [InlineData(20, 22)]
    [InlineData(50, 52)]
    [InlineData(100, 102)]
    public void Apply_ExpandsByTheSelectedBlockSizes(int slotCount, int expectedCapacity)
    {
        var edited = StorageEngine.Apply(SaveJson(), [new ExpandStorageOperation(slotCount)]);

        Assert.Equal(expectedCapacity, StorageEngine.Read(edited).Capacity);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(0)]
    [InlineData(-10)]
    public void Apply_RejectsExpansionBlockSizesOutsideTheAllowedChoices(int slotCount)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(SaveJson(), [new ExpandStorageOperation(slotCount)]));

        Assert.Contains("10, 20, 50, or 100", exception.Message);
    }

    [Fact]
    public void Apply_EnforcesTheMaximumAccountStorageCapacity()
    {
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        var locker = root["soul"]!["cl"]!.AsArray();
        for (var slot = 2; locker.Count < ExpandStorageOperation.MaxTotalSlots - 100; slot++)
            locker.Add(new JsonObject { ["slot"] = slot, ["type"] = -1, ["eid"] = "" });

        var expanded = StorageEngine.Apply(root.ToJsonString(), [new ExpandStorageOperation(100)]);
        Assert.Equal(ExpandStorageOperation.MaxTotalSlots, StorageEngine.Read(expanded).Capacity);

        var exception = Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(expanded, [new ExpandStorageOperation(10)]));
        Assert.Contains("2,000", exception.Message);
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
        Assert.DoesNotContain(replacedRoot["part"]!["pts"]!["424242"]!.AsArray(), value => value!["eid"]!.GetValue<string>() == LockerPartId);
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", replacedRoot["soul"]!["deathbag"]!["424242"]!["77"]!.ToJsonString());

        var cleared = StorageEngine.Apply(replaced, [new ClearStorageSlotOperation(0)]);
        var clearedRoot = JsonNode.Parse(cleared)!.AsObject();
        Assert.Equal(string.Empty, clearedRoot["soul"]!["cl"]![0]!["eid"]!.GetValue<string>());
        Assert.Equal(-1, clearedRoot["soul"]!["cl"]![0]!["type"]!.GetValue<int>());
        Assert.Empty(clearedRoot["item"]!["items"]!.AsArray());
        Assert.Equal($"[{{\"eid\":\"{BagPartId}\"}}]", clearedRoot["soul"]!["deathbag"]!["424242"]!["77"]!.ToJsonString());
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

    [Fact]
    public void Apply_ExpandsEachOwnedFighterDeathBagByTenWithSequentialEmptySlots()
    {
        var json = File.ReadAllText(FindRepositoryFile("data", "save_current.json"));
        var edited = StorageEngine.Apply(json, [new ExpandDeathBagsOperation()]);
        var originalRoot = JsonNode.Parse(json)!.AsObject();
        var editedRoot = JsonNode.Parse(edited)!.AsObject();

        var originalBags = originalRoot["soul"]!["deathbag"]!["1"]!.AsObject();
        var editedBags = editedRoot["soul"]!["deathbag"]!["1"]!.AsObject();
        Assert.Equal(2, originalBags.Count);
        foreach (var (cid, value) in editedBags)
        {
            var bag = value!.AsArray();
            Assert.Equal(30, bag.Count);
            Assert.True(originalBags.ContainsKey(cid));
            for (var index = 20; index < 30; index++)
            {
                var row = bag[index]!.AsObject();
                Assert.Equal(index, row["slot"]!.GetValue<int>());
                Assert.Equal(-1, row["type"]!.GetValue<int>());
                Assert.Equal(string.Empty, row["eid"]!.GetValue<string>());
                Assert.Equal(string.Empty, row["site"]!.GetValue<string>());
                Assert.Equal(-1, row["arm_slot"]!.GetValue<int>());
                Assert.Equal(1, row["uid"]!.GetValue<int>());
                Assert.Equal(cid, row["cid"]!.GetValue<string>());
            }
        }

        // Nothing outside the owned fighters' Death Bags may change.
        Assert.True(originalRoot["soul"]!["deathbag"]!.AsObject().Remove("1"));
        Assert.True(editedRoot["soul"]!["deathbag"]!.AsObject().Remove("1"));
        Assert.True(JsonNode.DeepEquals(originalRoot, editedRoot));
    }

    [Fact]
    public void Apply_DeathBagExpansion_LeavesTransientAndOtherOwnerBagsUntouched()
    {
        var json = File.ReadAllText(FindRepositoryFile("data", "save_current.json"));
        var originalRoot = JsonNode.Parse(json)!.AsObject();
        var edited = StorageEngine.Apply(json, [new ExpandDeathBagsOperation()]);
        var editedRoot = JsonNode.Parse(edited)!.AsObject();

        var originalOthers = originalRoot["soul"]!["deathbag"]!.AsObject().Where(pair => pair.Key != "1");
        foreach (var (key, _) in originalOthers)
        {
            Assert.True(JsonNode.DeepEquals(
                originalRoot["soul"]!["deathbag"]![key],
                editedRoot["soul"]!["deathbag"]![key]));
        }
    }

    [Fact]
    public void Apply_DeathBagExpansion_ClonesTheExistingRowShape()
    {
        var edited = StorageEngine.Apply(SaveJson(), [new ExpandDeathBagsOperation()]);
        var bag = JsonNode.Parse(edited)!["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray();

        Assert.Equal(11, bag.Count);
        var firstNew = bag[1]!.AsObject();
        Assert.Equal(1, firstNew["slot"]!.GetValue<int>());
        Assert.Equal(-1, firstNew["type"]!.GetValue<int>());
        Assert.Equal(string.Empty, firstNew["eid"]!.GetValue<string>());
        Assert.Null(firstNew["uid"]);
        Assert.False(firstNew.ContainsKey("site"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(81)]
    public void Apply_RejectsDeathBagExpansionOutsideTheAllowedRowCounts(int rowsPerBag)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(SaveJson(), [new ExpandDeathBagsOperation(rowsPerBag)]));

        Assert.Contains($"1 to {ExpandDeathBagsOperation.MaximumRowsPerBag}", exception.Message);
    }

    [Fact]
    public void Apply_DeathBagExpansion_CapsEachBagAtTheMaximumRowCount()
    {
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        var bag = root["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray();
        for (var slot = bag.Count; bag.Count < ExpandDeathBagsOperation.MaximumRowsPerBag - 5; slot++)
            bag.Add(new JsonObject { ["slot"] = slot, ["type"] = -1, ["eid"] = "" });

        var first = StorageEngine.Apply(root.ToJsonString(), [new ExpandDeathBagsOperation()]);
        Assert.Equal(ExpandDeathBagsOperation.MaximumRowsPerBag,
            JsonNode.Parse(first)!["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray().Count);

        var second = StorageEngine.Apply(first, [new ExpandDeathBagsOperation()]);
        Assert.Equal(ExpandDeathBagsOperation.MaximumRowsPerBag,
            JsonNode.Parse(second)!["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray().Count);
    }

    [Fact]
    public void Apply_VipDeathBagExpansion_CanUseTheTenSlotsReservedAboveTheManualLimit()
    {
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        var bag = root["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray();
        for (var slot = bag.Count; bag.Count < ExpandCharacterDeathBagOperation.MaximumRowsPerBag; slot++)
            bag.Add(new JsonObject { ["slot"] = slot, ["type"] = -1, ["eid"] = "" });

        var edited = StorageEngine.Apply(root.ToJsonString(), [new ExpandDeathBagsOperation()]);

        Assert.Equal(ExpandDeathBagsOperation.MaximumRowsPerBag,
            JsonNode.Parse(edited)!["soul"]!["deathbag"]!["424242"]!["77"]!.AsArray().Count);
    }

    [Fact]
    public void Apply_SetsPlayerOwnedItemInDeathBagSlot_AndJoinsItIntoTheFighterBag()
    {
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\",\"gettime\":0}");

        var edited = StorageEngine.Apply(CharacterSaveJson(), [new SetCharacterDeathBagSlotOperation("fighter", 1, template)]);
        var root = JsonNode.Parse(edited)!.AsObject();
        var row = root["soul"]!["deathbag"]!["424242"]!["fighter"]!.AsArray()[1]!.AsObject();
        var eid = row["eid"]!.GetValue<string>();

        Assert.True(Guid.TryParse(eid, out _));
        Assert.Equal(3, row["type"]!.GetValue<int>());
        Assert.Equal(-1, row["arm_slot"]!.GetValue<int>());

        var item = root["item"]!["items"]!.AsArray().Single()!.AsObject();
        Assert.Equal(eid, item["eid"]!.GetValue<string>());
        Assert.Equal("USER", item["owner"]!.GetValue<string>());
        Assert.True(item["gettime"]!.GetValue<long>() > 0);
        Assert.False(item.ContainsKey("uid"));

        var slot = CharacterInventory.Read(edited).Characters
            .Single(character => character.CharacterId == "fighter").DeathBag.Single(entry => entry.Slot == 1);
        Assert.True(slot.IsOccupied);
        Assert.Equal("IT_HEAL", slot.DefinitionId);
        Assert.DoesNotContain(slot.Warnings, warning => warning.Contains("not USER", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ReplacesOccupiedDeathBagSlot_RemovingTheOldPlayerEntity()
    {
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\"}");

        var edited = StorageEngine.Apply(CharacterSaveJson(), [new SetCharacterDeathBagSlotOperation("fighter", 0, template)]);
        var root = JsonNode.Parse(edited)!.AsObject();

        Assert.DoesNotContain(root["part"]!["pts"]!["424242"]!.AsArray(),
            value => value!["eid"]!.GetValue<string>() == BagPartId);
        Assert.Single(root["item"]!["items"]!.AsArray());
    }

    [Fact]
    public void Apply_AddsPlayerOwnedBeastToDeathBag_WithARawRewardMushroom()
    {
        var template = new StorageItemTemplate(2, "BST_DOG", "Dog",
            "{\"bstid\":\"BST_DOG\",\"rwdemsrid\":\"\",\"gettime\":0}",
            "{\"msrid\":\"MSR_REWARD\",\"gettime\":0}");

        var edited = StorageEngine.Apply(CharacterSaveJson(), [new SetCharacterDeathBagSlotOperation("fighter", 1, template)]);
        var root = JsonNode.Parse(edited)!.AsObject();
        var beast = root["beast"]!["bsts"]!.AsArray().Single()!.AsObject();
        var mushroom = root["mushroom"]!["msrs"]!.AsArray().Single()!.AsObject();

        Assert.Equal("USER", beast["owner"]!.GetValue<string>());
        Assert.Equal("BEAST", mushroom["owner"]!.GetValue<string>());
        Assert.Equal(mushroom["eid"]!.GetValue<string>(), beast["rwdemsrid"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_ClearsDeathBagSlot_AndRemovesThePlayerEntity()
    {
        var edited = StorageEngine.Apply(CharacterSaveJson(), [new ClearCharacterDeathBagSlotOperation("fighter", 0)]);
        var root = JsonNode.Parse(edited)!.AsObject();
        var row = root["soul"]!["deathbag"]!["424242"]!["fighter"]!.AsArray()[0]!.AsObject();

        Assert.Equal(-1, row["type"]!.GetValue<int>());
        Assert.Equal(string.Empty, row["eid"]!.GetValue<string>());
        Assert.Empty(root["part"]!["pts"]!["424242"]!.AsArray());
    }

    [Fact]
    public void Apply_RejectsDeathBagEditsForMissingTargets()
    {
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\"}");

        Assert.Contains("slot 9", Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(CharacterSaveJson(), [new SetCharacterDeathBagSlotOperation("fighter", 9, template)])).Message);
        Assert.Contains("does not have a Death Bag", Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(CharacterSaveJson(), [new SetCharacterDeathBagSlotOperation("ghost", 0, template)])).Message);
    }

    [Fact]
    public void Apply_RejectsReplacingADeathBagEntityOwnedByAnotherOwner()
    {
        var root = JsonNode.Parse(CharacterSaveJson())!.AsObject();
        root["part"]!["pts"]!["424242"]![0]!["owner"] = "COIN_LOCKER";
        var template = new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\"}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => StorageEngine.Apply(root.ToJsonString(), [new SetCharacterDeathBagSlotOperation("fighter", 0, template)]));

        Assert.Contains("not owned by the active player", exception.Message);
    }

    private static string CharacterSaveJson() => $$"""
        {
          "user": { "uid": 424242 },
          "soul": {
            "uid": 424242,
            "cl": [ { "slot": 0, "type": -1, "eid": "" } ],
            "chr": {
              "chrs": { "424242": [
                { "cid": "fighter", "name": "Fighter", "state": "FREE", "type": "BAL", "body": "BODY_M",
                  "grade": 1, "limit_break": 0, "hp": 10, "gain_exp": 0, "money": 0, "spirit": 0, "bloodnium": 0 }
              ] },
              "slots": { "424242": [ { "slot": 0, "cid": "fighter" } ] }
            },
            "deathbag": { "424242": { "fighter": [
              { "uid": 424242, "cid": "fighter", "slot": 0, "type": 0, "eid": "{{BagPartId}}", "site": "", "arm_slot": 0 },
              { "uid": 424242, "cid": "fighter", "slot": 1, "type": -1, "eid": "", "site": "", "arm_slot": -1 }
            ] } }
          },
          "part": { "pts": { "424242": [
            { "eid": "{{BagPartId}}", "ptid": "P_BAG", "uid": 424242, "owner": "USER" }
          ] } },
          "item": { "items": [] },
          "mushroom": { "msrs": [] },
          "beast": { "bsts": [] },
          "diedchara": { "dchrs": { "424242": [] } }
        }
        """;

    private static string SaveJson() => $$"""
        {
          "user": { "uid": 424242 },
          "soul": {
            "cl": [
              { "slot": 0, "type": 0, "eid": "{{LockerPartId}}" },
              { "slot": 1, "type": -1, "eid": "" }
            ],
            "deathbag": { "424242": { "77": [ { "eid": "{{BagPartId}}" } ] } }
          },
          "part": { "pts": { "424242": [
            { "eid": "{{LockerPartId}}", "ptid": "P_START", "uid": 424242, "owner": "COIN_LOCKER" },
            { "eid": "{{BagPartId}}", "ptid": "P_BAG", "uid": 424242, "owner": "USER" }
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
