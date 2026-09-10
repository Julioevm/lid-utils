using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidUtils.Core;

public sealed record CharacterStat(string Label, string Value, string? Bonus = null, string? Pointer = null);

public sealed record CharacterBagSlot(
    int Slot,
    int Type,
    string? EntityId,
    string? DefinitionId,
    string? Owner,
    string? Site,
    int? ArmSlot,
    IReadOnlyList<string> Warnings)
{
    public bool IsOccupied => !string.IsNullOrWhiteSpace(EntityId);
}

public sealed record CharacterRecord(
    string CharacterId,
    string Name,
    string NamePointer,
    string RawState,
    string Status,
    int? RosterSlot,
    string FighterType,
    string Body,
    string Grade,
    string LimitBreak,
    string CurrentHp,
    string TotalExperience,
    string CarriedMoney,
    string CarriedSplithium,
    string CarriedBloodnium,
    IReadOnlyList<CharacterStat> Stats,
    IReadOnlyList<CharacterBagSlot> DeathBag,
    IReadOnlyList<string> EquippedDecals,
    IReadOnlyList<string> Warnings,
    string? BodyStatsPointer = null);

public sealed record CharacterInventory(
    int PlayerUid,
    IReadOnlyList<CharacterRecord> Characters,
    IReadOnlyList<string> Warnings)
{
    public static CharacterInventory Read(string json) => CharacterInventoryReader.Read(json);
}

internal static class CharacterInventoryReader
{
    private static readonly (string Property, string Label, string? Bonus)[] StatFields =
    [
        ("lvl", "Level", null),
        ("hp", "HP", "hp_bonus"),
        ("str", "STR", "str_bonus"),
        ("dex", "DEX", "dex_bonus"),
        ("vit", "VIT", "vit_bonus"),
        ("stm", "STM", "stm_bonus"),
        ("luk", "LUK", "luk_bonus"),
        ("skill", "Skill", null),
        ("bag", "Bag", null),
        ("rage", "Rage", null)
    ];

    public static CharacterInventory Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("The save JSON root must be an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The decoded save JSON is invalid.", exception);
        }

        var uid = ResolvePlayerUid(root);
        var key = uid.ToString(CultureInfo.InvariantCulture);
        var warnings = new List<string>();
        var fighters = ReadArray(root["soul"]?["chr"]?["chrs"]?[key], "/soul/chr/chrs/" + key, warnings);
        var roster = BuildRoster(root["soul"]?["chr"]?["slots"]?[key], warnings);
        var bodies = IndexByCharacterId(root["bodyuser"]?[key], "/bodyuser/" + key, warnings);
        var bags = root["soul"]?["deathbag"]?[key] as JsonObject;
        var equipped = BuildEquipped(root["soul"]?["skl"]?["eqskl"]?[key], warnings);
        var deadIds = FindCharacterIds(root["diedchara"]?["dchrs"]?[key]);
        var entities = BuildEntityIndex(root, warnings);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CharacterRecord>();

        for (var index = 0; index < fighters.Count; index++)
        {
            if (fighters[index] is not JsonObject fighter)
            {
                warnings.Add($"Fighter row {index} is not an object.");
                continue;
            }

            var cid = Text(fighter["cid"]);
            if (string.IsNullOrWhiteSpace(cid))
            {
                warnings.Add($"Fighter row {index} has no character ID.");
                continue;
            }
            if (!seen.Add(cid))
            {
                warnings.Add($"Character ID '{cid}' is duplicated; only the first row is shown.");
                continue;
            }

            var characterWarnings = new List<string>();
            roster.TryGetValue(cid, out var rosterSlot);
            bodies.TryGetValue(cid, out var bodyRow);
            var bodyStats = bodyRow?.Value;
            if (bodyStats is null) characterWarnings.Add("Allocated body stats are missing.");
            var bag = bags?[cid] as JsonArray;
            if (bag is null) characterWarnings.Add("Death Bag data is missing.");
            var slots = ReadBag(bag, cid, entities, characterWarnings);
            var rawState = Text(fighter["state"]) ?? string.Empty;
            var status = deadIds.Contains(cid) ? "Dead" : rawState switch
            {
                "USE" => "In use",
                "FREE" => "Freezer",
                "GUARD" => "Defender",
                _ => string.IsNullOrWhiteSpace(rawState) ? "Unknown" : $"Unknown ({rawState})"
            };
            if (status.StartsWith("Unknown", StringComparison.Ordinal))
                characterWarnings.Add("The fighter has an unrecognized state and is shown read-only.");

            result.Add(new CharacterRecord(
                cid,
                Text(fighter["name"]) ?? string.Empty,
                $"/soul/chr/chrs/{key}/{index}/name",
                rawState,
                status,
                rosterSlot,
                Text(fighter["type"]) ?? "—",
                Text(fighter["body"]) ?? "—",
                Scalar(fighter["grade"]),
                Scalar(fighter["limit_break"]),
                Scalar(fighter["hp"]),
                Scalar(fighter["total_exp"]),
                Scalar(fighter["money"]),
                Scalar(fighter["spirit"]),
                Scalar(fighter["bloodnium"]),
                ReadStats(bodyStats, bodyRow is null ? null : $"/bodyuser/{key}/{bodyRow.Index}"),
                slots,
                equipped.TryGetValue(cid, out var skills) ? skills : [],
                characterWarnings,
                bodyRow is null ? null : $"/bodyuser/{key}/{bodyRow.Index}"));
        }

        return new CharacterInventory(uid,
            result.OrderBy(character => character.RosterSlot ?? int.MaxValue)
                .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.CharacterId, StringComparer.Ordinal)
                .ToArray(),
            warnings);
    }

    private static int ResolvePlayerUid(JsonObject root)
    {
        var hasUser = TryInt(root["user"]?["uid"], out var userUid);
        var hasSoul = TryInt(root["soul"]?["uid"], out var soulUid);
        if (hasUser && hasSoul && userUid != soulUid)
            throw new InvalidOperationException("The player UID differs between /user/uid and /soul/uid.");
        if (hasUser) return userUid;
        if (hasSoul) return soulUid;
        throw new InvalidOperationException("The save does not contain a usable player UID at /user/uid or /soul/uid.");
    }

    private static JsonArray ReadArray(JsonNode? node, string path, ICollection<string> warnings)
    {
        if (node is JsonArray array) return array;
        if (node is null || node is JsonObject { Count: 0 })
        {
            warnings.Add($"{path} is empty or missing.");
            return [];
        }
        warnings.Add($"{path} is not an array.");
        return [];
    }

    private static Dictionary<string, int?> BuildRoster(JsonNode? node, ICollection<string> warnings)
    {
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        if (node is null || node is JsonObject { Count: 0 }) return result;
        if (node is not JsonArray rows)
        {
            warnings.Add("The player roster slot table is not an array.");
            return result;
        }
        foreach (var row in rows.OfType<JsonObject>())
        {
            var cid = Text(row["cid"]);
            if (string.IsNullOrWhiteSpace(cid)) continue;
            if (!TryInt(row["slot"], out var slot))
            {
                warnings.Add($"Roster entry for '{cid}' has no valid slot.");
                continue;
            }
            if (!result.TryAdd(cid, slot)) warnings.Add($"Character '{cid}' appears in more than one roster slot.");
        }
        return result;
    }

    private sealed record BodyRow(int Index, JsonObject Value);

    private static Dictionary<string, BodyRow> IndexByCharacterId(JsonNode? node, string path, ICollection<string> warnings)
    {
        var result = new Dictionary<string, BodyRow>(StringComparer.Ordinal);
        if (node is null || node is JsonObject { Count: 0 }) return result;
        if (node is not JsonArray rows)
        {
            warnings.Add($"{path} is not an array.");
            return result;
        }
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row) continue;
            var cid = Text(row["cid"]);
            if (!string.IsNullOrWhiteSpace(cid) && !result.TryAdd(cid, new BodyRow(index, row)))
                warnings.Add($"Character '{cid}' has duplicate body-stat rows.");
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildEquipped(JsonNode? node, ICollection<string> warnings)
    {
        var result = new Dictionary<string, List<(int Slot, string Skill)>>(StringComparer.Ordinal);
        if (node is null || node is JsonObject { Count: 0 }) return [];
        if (node is not JsonArray rows)
        {
            warnings.Add("The equipped decal table is not an array.");
            return [];
        }
        foreach (var row in rows.OfType<JsonObject>())
        {
            var cid = Text(row["cid"]);
            var skill = Text(row["sklid"]);
            if (string.IsNullOrWhiteSpace(cid) || string.IsNullOrWhiteSpace(skill)) continue;
            if (!result.TryGetValue(cid, out var list)) result[cid] = list = [];
            list.Add((TryInt(row["slot"], out var slot) ? slot : int.MaxValue, skill));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(item => item.Slot).Select(item => item.Skill).ToArray(),
            StringComparer.Ordinal);
    }

    private static HashSet<string> FindCharacterIds(JsonNode? node)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Visit(node);
        return result;

        void Visit(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                var cid = Text(obj["cid"]);
                if (!string.IsNullOrWhiteSpace(cid)) result.Add(cid);
                foreach (var child in obj) Visit(child.Value);
            }
            else if (current is JsonArray array)
            {
                foreach (var child in array) Visit(child);
            }
        }
    }

    private static IReadOnlyList<CharacterBagSlot> ReadBag(
        JsonArray? bag,
        string cid,
        IReadOnlyDictionary<string, EntityInfo> entities,
        ICollection<string> characterWarnings)
    {
        if (bag is null) return [];
        var result = new List<CharacterBagSlot>();
        var seenSlots = new HashSet<int>();
        var seenEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in bag)
        {
            if (node is not JsonObject row)
            {
                characterWarnings.Add("A Death Bag row is not an object.");
                continue;
            }
            if (!TryInt(row["slot"], out var slot))
            {
                characterWarnings.Add("A Death Bag row has no valid slot number.");
                continue;
            }
            var rowWarnings = new List<string>();
            if (!seenSlots.Add(slot)) rowWarnings.Add("Duplicate slot number.");
            var type = TryInt(row["type"], out var readType) ? readType : -1;
            var eid = Text(row["eid"]);
            EntityInfo? entity = null;
            if (string.IsNullOrWhiteSpace(eid))
            {
                eid = null;
                if (type != -1) rowWarnings.Add("Empty slot does not use type -1.");
            }
            else
            {
                if (!seenEntities.Add(eid)) rowWarnings.Add("Entity is referenced more than once in this bag.");
                if (!entities.TryGetValue(eid, out entity)) rowWarnings.Add("Referenced entity is missing.");
                else
                {
                    if (entity.Type != type) rowWarnings.Add("Slot type does not match the entity registry.");
                    if (!string.Equals(entity.Owner, "USER", StringComparison.Ordinal))
                        rowWarnings.Add($"Entity owner is '{entity.Owner ?? "unknown"}', not USER.");
                }
            }
            if (!string.Equals(Text(row["cid"]), cid, StringComparison.Ordinal)) rowWarnings.Add("Slot character ID does not match its bag.");
            result.Add(new CharacterBagSlot(slot, type, eid, entity?.DefinitionId, entity?.Owner,
                Text(row["site"]), TryInt(row["arm_slot"], out var armSlot) ? armSlot : null, rowWarnings));
        }
        return result.OrderBy(slot => slot.Slot).ToArray();
    }

    private static IReadOnlyList<CharacterStat> ReadStats(JsonObject? body, string? bodyPointer)
    {
        if (body is null) return [];
        return StatFields.Select(field => new CharacterStat(
            field.Label,
            Scalar(body[field.Property]),
            field.Bonus is null ? null : Scalar(body[field.Bonus]),
            bodyPointer is null ? null : $"{bodyPointer}/{field.Property}")).ToArray();
    }

    private static Dictionary<string, EntityInfo> BuildEntityIndex(JsonObject root, ICollection<string> warnings)
    {
        var result = new Dictionary<string, EntityInfo>(StringComparer.Ordinal);
        if (root["part"]?["pts"] is JsonObject partRegistries)
            foreach (var registry in partRegistries)
                Index(registry.Value, 0, "/part/pts/" + registry.Key);
        Index(root["mushroom"]?["msrs"], 1, "/mushroom/msrs");
        Index(root["beast"]?["bsts"], 2, "/beast/bsts");
        Index(root["item"]?["items"], 3, "/item/items");
        return result;

        void Index(JsonNode? node, int type, string path)
        {
            if (node is null || node is JsonObject { Count: 0 }) return;
            if (node is not JsonArray rows)
            {
                warnings.Add($"{path} is not an array.");
                return;
            }
            foreach (var entity in rows.OfType<JsonObject>())
            {
                var eid = Text(entity["eid"]);
                if (string.IsNullOrWhiteSpace(eid)) continue;
                var definition = Text(entity["ptid"]) ?? Text(entity["itemid"]) ?? Text(entity["itid"])
                    ?? Text(entity["msrid"]) ?? Text(entity["bstid"]);
                if (!result.TryAdd(eid, new EntityInfo(type, definition, Text(entity["owner"]))))
                    warnings.Add($"Entity ID '{eid}' is duplicated across inventory registries.");
            }
        }
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "—",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString()
    };

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryInt(JsonNode? node, out int result)
    {
        if (node is JsonValue value && (value.TryGetValue<int>(out result) ||
            value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)))
            return true;
        result = default;
        return false;
    }

    private sealed record EntityInfo(int Type, string? DefinitionId, string? Owner);
}
