using System.Text.Json.Nodes;
using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class CharacterInventoryTests
{
    [Fact]
    public void Read_CurrentSaveFixture_JoinsEveryOwnedFighter()
    {
        var path = FindRepositoryFile("data", "save_current.json");

        var inventory = CharacterInventory.Read(File.ReadAllText(path));

        Assert.Equal(1, inventory.PlayerUid);
        Assert.Equal(2, inventory.Characters.Count);
        Assert.All(inventory.Characters, character => Assert.Equal(20, character.DeathBag.Count));
        Assert.Contains(inventory.Characters, character => character.Status == "In use");
        Assert.Contains(inventory.Characters, character => character.Status == "Freezer");
    }

    [Fact]
    public void Read_BivuacFixture_DerivesTheActiveDeadFighter()
    {
        var path = FindRepositoryFile("data", "bivuac.json");

        var inventory = CharacterInventory.Read(File.ReadAllText(path));

        Assert.Contains(inventory.Characters, character => character.Status == "Dead");
    }

    [Fact]
    public void Read_JoinsFightersByCidAndUsesDynamicUid()
    {
        var inventory = CharacterInventory.Read(SaveJson());

        Assert.Equal(424242, inventory.PlayerUid);
        Assert.Equal(2, inventory.Characters.Count);
        var active = Assert.Single(inventory.Characters, character => character.CharacterId == "active");
        Assert.Equal("Alice", active.Name);
        Assert.Equal("/soul/chr/chrs/424242/1/name", active.NamePointer);
        Assert.Equal("In use", active.Status);
        Assert.Equal(0, active.RosterSlot);
        Assert.Equal("7", active.Stats.Single(stat => stat.Label == "Level").Value);
        Assert.Equal("SKL_ONE", Assert.Single(active.EquippedDecals));
        var item = Assert.Single(active.DeathBag);
        Assert.Equal("IT_HEAL", item.DefinitionId);
        Assert.Empty(item.Warnings);
    }

    [Fact]
    public void Read_DerivesDeadFromActiveDeathRecordAndPreservesUnknownState()
    {
        var inventory = CharacterInventory.Read(SaveJson());

        Assert.Equal("Dead", inventory.Characters.Single(character => character.CharacterId == "dead").Status);
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        root["diedchara"]!["dchrs"]!["424242"] = new JsonArray();
        root["soul"]!["chr"]!["chrs"]!["424242"]![0]!["state"] = "MYSTERY";

        var unknown = CharacterInventory.Read(root.ToJsonString()).Characters.Single(character => character.CharacterId == "dead");
        Assert.Equal("Unknown (MYSTERY)", unknown.Status);
        Assert.Contains(unknown.Warnings, warning => warning.Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_SurfacesBrokenBagReferencesWithoutDroppingTheFighter()
    {
        var root = JsonNode.Parse(SaveJson())!.AsObject();
        root["soul"]!["deathbag"]!["424242"]!["active"]![0]!["eid"] = "missing";

        var fighter = CharacterInventory.Read(root.ToJsonString()).Characters.Single(character => character.CharacterId == "active");

        Assert.Single(fighter.DeathBag);
        Assert.Contains(fighter.DeathBag[0].Warnings, warning => warning.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_ExpandsOnlyTheSelectedFighterBag()
    {
        var edited = StorageEngine.Apply(SaveJson(), [new ExpandCharacterDeathBagOperation("active", 5)]);
        var bags = JsonNode.Parse(edited)!["soul"]!["deathbag"]!["424242"]!.AsObject();

        Assert.Single(bags["dead"]!.AsArray());
        Assert.Equal(6, bags["active"]!.AsArray().Count);
        var last = bags["active"]![5]!.AsObject();
        Assert.Equal(5, last["slot"]!.GetValue<int>());
        Assert.Equal(-1, last["type"]!.GetValue<int>());
        Assert.Equal(string.Empty, last["eid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(11)]
    public void Apply_RejectsUnsupportedSelectedBagExpansion(int count)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageEngine.Apply(SaveJson(), [new ExpandCharacterDeathBagOperation("active", count)]));
        Assert.Contains("1, 5, or 10", exception.Message);
    }

    private static string SaveJson() => """
        {
          "user":{"uid":424242},
          "soul":{"uid":424242,"cl":[],"chr":{"chrs":{"424242":[
            {"uid":424242,"cid":"dead","name":"Morgan","state":"ENEMY","type":"COL","body":"BODY_F","grade":2,"limit_break":0,"hp":0,"total_exp":3,"money":0,"spirit":0,"bloodnium":0},
            {"uid":424242,"cid":"active","name":"Alice","state":"USE","type":"BAL","body":"BODY_M","grade":1,"limit_break":0,"hp":290,"total_exp":100,"money":12,"spirit":4,"bloodnium":1}
          ]},"slots":{"424242":[{"uid":424242,"slot":1,"cid":"dead"},{"uid":424242,"slot":0,"cid":"active"}]}},
          "deathbag":{"424242":{"dead":[{"uid":424242,"cid":"dead","slot":0,"type":-1,"eid":"","site":"","arm_slot":-1}],"active":[{"uid":424242,"cid":"active","slot":0,"type":3,"eid":"11111111-1111-1111-1111-111111111111","site":"","arm_slot":-1}]}},
          "skl":{"eqskl":{"424242":[{"cid":"active","sklid":"SKL_ONE","slot":0}]}}},
          "bodyuser":{"424242":[{"uid":424242,"cid":"active","lvl":7,"hp":4,"str":2,"dex":1,"vit":1,"stm":3,"luk":1,"skill":0,"bag":0,"rage":0,"hp_bonus":0,"str_bonus":0,"dex_bonus":0,"vit_bonus":0,"stm_bonus":0,"luk_bonus":0}]},
          "diedchara":{"dchrs":{"424242":[{"cid":"dead"}]}},
          "part":{"pts":{"424242":[]}},
          "item":{"items":[{"eid":"11111111-1111-1111-1111-111111111111","itemid":"IT_HEAL","owner":"USER"}]},
          "mushroom":{"msrs":[]},"beast":{"bsts":[]}
        }
        """;

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
