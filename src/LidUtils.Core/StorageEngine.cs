using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidUtils.Core;

/// <summary>
/// Structural account-storage reader and mutator. It intentionally works on a copy of decoded save JSON;
/// file fingerprinting, backups, and atomic writes remain the responsibility of the data service.
/// </summary>
public static class StorageEngine
{
    private const string LockerOwner = "COIN_LOCKER";

    public static StorageInventory Read(string json)
    {
        var root = ParseRoot(json);
        var playerUid = ResolvePlayerUid(root);
        var locker = RequireLocker(root);
        var entities = BuildEntityIndex(root);
        var slots = ReadSlots(root, locker, entities, playerUid);
        return new StorageInventory(playerUid, slots);
    }

    public static string Apply(string json, IReadOnlyCollection<StorageOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
            throw new InvalidOperationException("There are no staged storage changes to apply.");

        var root = ParseRoot(json);
        var uid = ResolvePlayerUid(root);
        var locker = RequireLocker(root);
        ValidateLocker(locker);

        // Rebuild after every operation: replacing and clearing can alter the registry graph.
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case ExpandStorageOperation expand:
                    Expand(locker, expand);
                    break;
                case ExpandDeathBagsOperation expandBags:
                    ExpandDeathBags(root, uid, expandBags);
                    break;
                case ExpandCharacterDeathBagOperation expandCharacterBag:
                    ExpandCharacterDeathBag(root, uid, expandCharacterBag);
                    break;
                case SetStorageSlotOperation set:
                    SetSlot(root, locker, uid, set);
                    break;
                case ClearStorageSlotOperation clear:
                    ClearSlot(root, locker, uid, clear.Slot);
                    break;
                default:
                    throw new InvalidOperationException("The staged storage operation is not supported.");
            }
        }

        ValidateLocker(locker);
        _ = Read(root.ToJsonString());
        return root.ToJsonString();
    }

    private static JsonObject ParseRoot(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("The save JSON root must be an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The decoded save JSON is invalid.", exception);
        }
    }

    private static int ResolvePlayerUid(JsonObject root)
    {
        var hasUserUid = TryInt(root["user"]?["uid"], out var userUid);
        var hasSoulUid = TryInt(root["soul"]?["uid"], out var soulUid);
        if (hasUserUid && hasSoulUid && userUid != soulUid)
            throw new InvalidOperationException("The player UID differs between /user/uid and /soul/uid.");
        if (hasUserUid) return userUid;
        if (hasSoulUid) return soulUid;
        throw new InvalidOperationException("The save does not contain a usable player UID at /user/uid or /soul/uid.");
    }

    private static JsonArray RequireLocker(JsonObject root) =>
        root["soul"]?["cl"]?.AsArray()
        ?? throw new InvalidOperationException("The save does not contain an account-storage array at /soul/cl.");

    private static IReadOnlyList<StorageSlot> ReadSlots(
        JsonObject root,
        JsonArray locker,
        IReadOnlyDictionary<string, EntityLocation> entities,
        int playerUid)
    {
        var slots = new List<StorageSlot>(locker.Count);
        var seenSlots = new HashSet<int>();
        var seenEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in locker)
        {
            var slot = entry?.AsObject() ?? throw new InvalidOperationException("A storage slot is not an object.");
            var slotNumber = RequireInt(slot, "slot", "storage slot");
            if (slotNumber < 0)
                throw new InvalidOperationException("A storage slot cannot have a negative ID.");
            if (!seenSlots.Add(slotNumber))
                throw new InvalidOperationException($"Storage slot {slotNumber} is duplicated.");
            var type = RequireInt(slot, "type", $"storage slot {slotNumber}");
            var entityId = ReadEntityId(slot["eid"], $"storage slot {slotNumber}", allowEmpty: true);
            if (entityId is null)
            {
                if (type != -1)
                    throw new InvalidOperationException($"Empty storage slot {slotNumber} must have type -1.");
                slots.Add(new StorageSlot(slotNumber, type, null));
                continue;
            }

            if (!entities.TryGetValue(entityId, out var entity))
                throw new InvalidOperationException($"Storage slot {slotNumber} references missing entity '{entityId}'.");
            if (!seenEntities.Add(entityId))
                throw new InvalidOperationException($"Entity '{entityId}' is referenced by more than one storage slot.");
            if (type != StorageType(entity.Kind))
                throw new InvalidOperationException($"Storage slot {slotNumber} has type {type}, which does not match entity '{entityId}'.");
            if (!string.Equals(ReadText(entity.Node["owner"]), LockerOwner, StringComparison.Ordinal))
                throw new InvalidOperationException($"Storage slot {slotNumber} references entity '{entityId}' that is not owned by the locker.");
            if (entity.Kind == StorageEntityKind.Part &&
                (!ReferenceEquals(entity.Parent, PartRegistry(root, playerUid)) ||
                 !TryInt(entity.Node["uid"], out var entityUid) || entityUid != playerUid))
            {
                throw new InvalidOperationException($"Equipment entity '{entityId}' is not assigned to the active player.");
            }
            slots.Add(new StorageSlot(slotNumber, type, entityId, DefinitionId(entity.Node), DefinitionId(entity.Node)));
        }

        return slots.OrderBy(slot => slot.Slot).ToArray();
    }

    private static void Expand(JsonArray locker, ExpandStorageOperation operation)
    {
        if (!ExpandStorageOperation.AllowedSlotCounts.Contains(operation.SlotCount))
            throw new InvalidOperationException("Account storage can only be expanded by 10, 20, 50, or 100 slots.");
        var slots = locker.Select(value => value?.AsObject() ?? throw new InvalidOperationException("A storage slot is not an object."))
            .Select(value => RequireInt(value, "slot", "storage slot"))
            .ToArray();
        if (slots.Distinct().Count() != slots.Length)
            throw new InvalidOperationException("Storage contains duplicate slot IDs.");
        if (slots.Length + operation.SlotCount > ExpandStorageOperation.MaxTotalSlots)
            throw new InvalidOperationException($"Account storage cannot exceed {ExpandStorageOperation.MaxTotalSlots.ToString("N0", CultureInfo.InvariantCulture)} slots.");
        var nextSlot = slots.Length == 0 ? 0 : checked(slots.Max() + 1);
        for (var index = 0; index < operation.SlotCount; index++)
            locker.Add(new JsonObject { ["slot"] = checked(nextSlot + index), ["type"] = -1, ["eid"] = "" });
    }

    /// <summary>
    /// Appends empty Death Bag slot rows to every owned fighter (the bag keyed by the player
    /// UID under /soul/deathbag). Each new row clones the shape of the bag's last row so older
    /// or variant schemas keep their own field set. Rows are capped per bag to keep an
    /// accidental repeat activation from growing a bag without limit.
    /// </summary>
    private static void ExpandDeathBags(JsonObject root, int uid, ExpandDeathBagsOperation operation)
    {
        if (operation.RowsPerBag is <= 0 or > ExpandDeathBagsOperation.MaximumRowsPerBag)
            throw new InvalidOperationException(
                $"Fighter Death Bags can only be expanded by 1 to {ExpandDeathBagsOperation.MaximumRowsPerBag} slots at a time.");

        if (root["soul"]?["deathbag"] is not JsonObject deathBags) return;
        if (deathBags[uid.ToString(CultureInfo.InvariantCulture)] is not JsonObject ownedBags) return;

        foreach (var (cid, value) in ownedBags)
        {
            if (value is not JsonArray bag) continue;
            var rowsToAdd = Math.Min(operation.RowsPerBag, ExpandDeathBagsOperation.MaximumRowsPerBag - bag.Count);
            if (rowsToAdd <= 0) continue;
            var firstNewSlot = bag.Count;
            for (var index = firstNewSlot; index < firstNewSlot + rowsToAdd; index++)
                bag.Add(EmptyBagRow(bag, uid, cid, index));
        }
    }

    private static void ExpandCharacterDeathBag(JsonObject root, int uid, ExpandCharacterDeathBagOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.CharacterId))
            throw new InvalidOperationException("A fighter must be selected before expanding a Death Bag.");
        if (!ExpandCharacterDeathBagOperation.AllowedSlotCounts.Contains(operation.SlotCount))
            throw new InvalidOperationException("A fighter Death Bag can only be expanded by 1, 5, or 10 slots.");
        var key = uid.ToString(CultureInfo.InvariantCulture);
        var ownedBags = root["soul"]?["deathbag"]?[key] as JsonObject
            ?? throw new InvalidOperationException("The save does not contain Death Bags for the active player.");
        var bag = ownedBags[operation.CharacterId] as JsonArray
            ?? throw new InvalidOperationException($"Fighter '{operation.CharacterId}' does not have a Death Bag.");
        if (bag.Count + operation.SlotCount > ExpandCharacterDeathBagOperation.MaximumRowsPerBag)
            throw new InvalidOperationException($"A fighter Death Bag cannot exceed {ExpandCharacterDeathBagOperation.MaximumRowsPerBag} slots.");

        var existingSlots = bag.OfType<JsonObject>()
            .Select(row => RequireInt(row, "slot", "Death Bag slot"))
            .ToArray();
        if (existingSlots.Distinct().Count() != existingSlots.Length)
            throw new InvalidOperationException("The selected fighter's Death Bag contains duplicate slot IDs.");
        var nextSlot = existingSlots.Length == 0 ? 0 : checked(existingSlots.Max() + 1);
        for (var index = 0; index < operation.SlotCount; index++)
            bag.Add(EmptyBagRow(bag, uid, operation.CharacterId, checked(nextSlot + index)));
    }

    private static JsonObject EmptyBagRow(JsonArray bag, int uid, string cid, int slotIndex)
    {
        JsonObject row;
        if (bag.LastOrDefault() is JsonObject { Count: > 0 } template)
        {
            row = new JsonObject();
            foreach (var property in template)
                row[property.Key] = property.Value?.DeepClone();
        }
        else
        {
            row = new JsonObject
            {
                ["uid"] = uid,
                ["cid"] = cid
            };
        }

        // Normalize the clone to an empty slot regardless of the template's previous state.
        row["slot"] = slotIndex;
        row["type"] = -1;
        row["eid"] = "";
        if (row.ContainsKey("site")) row["site"] = "";
        if (row.ContainsKey("arm_slot")) row["arm_slot"] = -1;
        return row;
    }

    private static void SetSlot(JsonObject root, JsonArray locker, int uid, SetStorageSlotOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation.Template);
        var target = FindSlot(locker, operation.Slot);
        ValidateTemplate(operation.Template);
        var oldEntityId = ReadEntityId(target["eid"], $"storage slot {operation.Slot}", allowEmpty: true);
        if (oldEntityId is not null)
            EnsureExclusivelyOwnedByLocker(root, locker, target, uid, oldEntityId);

        var templateEntity = ParseTemplate(operation.Template.EntityJson, "entity");
        var targetRegistry = ResolveRegistry(root, templateEntity, operation.Template.Type, uid);
        var nextEid = NextEntityId(BuildEntityIndex(root).Keys);
        Materialize(templateEntity, nextEid, uid, LockerOwner, operation.Template.Type == 0);

        if (!string.IsNullOrWhiteSpace(operation.Template.LinkedMushroomJson))
        {
            if (!IsBeast(templateEntity))
                throw new InvalidOperationException("Only beast storage templates may include a linked reward mushroom.");
            var mushroom = ParseTemplate(operation.Template.LinkedMushroomJson!, "linked mushroom");
            if (!IsMushroom(mushroom))
                throw new InvalidOperationException("The linked beast reward must be a mushroom entity.");
            var mushroomId = NextEntityId(BuildEntityIndex(root).Keys.Append(nextEid));
            Materialize(mushroom, mushroomId, uid, "BEAST", isPart: false);
            EnsureMushroomRegistry(root).Add(mushroom);
            templateEntity["rwdemsrid"] = mushroomId;
        }

        targetRegistry.Add(templateEntity);
        target["type"] = operation.Template.Type;
        target["eid"] = nextEid;
        if (oldEntityId is not null)
            RemoveExclusivelyLockerOwnedEntity(root, locker, target, uid, oldEntityId);
    }

    private static void ClearSlot(JsonObject root, JsonArray locker, int uid, int slotNumber)
    {
        var target = FindSlot(locker, slotNumber);
        var oldEntityId = ReadEntityId(target["eid"], $"storage slot {slotNumber}", allowEmpty: true);
        if (oldEntityId is null)
            return;
        EnsureExclusivelyOwnedByLocker(root, locker, target, uid, oldEntityId);
        target["eid"] = "";
        target["type"] = -1;
        RemoveExclusivelyLockerOwnedEntity(root, locker, target, uid, oldEntityId);
    }

    private static JsonObject FindSlot(JsonArray locker, int slotNumber)
    {
        var matches = locker.Select(value => value?.AsObject() ?? throw new InvalidOperationException("A storage slot is not an object."))
            .Where(value => RequireInt(value, "slot", "storage slot") == slotNumber)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Storage slot {slotNumber} does not exist."),
            _ => throw new InvalidOperationException($"Storage slot {slotNumber} is duplicated.")
        };
    }

    private static void EnsureExclusivelyOwnedByLocker(JsonObject root, JsonArray locker, JsonObject target, int uid, string entityId)
    {
        var entities = BuildEntityIndex(root);
        if (!entities.TryGetValue(entityId, out var entity))
            throw new InvalidOperationException($"Storage references missing entity '{entityId}'.");
        if (!string.Equals(ReadText(entity.Node["owner"]), LockerOwner, StringComparison.Ordinal))
            throw new InvalidOperationException($"Entity '{entityId}' is not owned by the locker and cannot be removed.");
        if (entity.Kind == StorageEntityKind.Part && !ReferenceEquals(entity.Parent, PartRegistry(root, uid)))
            throw new InvalidOperationException($"Equipment entity '{entityId}' is not in the active player's inventory registry.");
        var references = new List<string>();
        FindReferences(root, entityId, string.Empty, target, entity.Node, references);
        if (references.Count > 0)
            throw new InvalidOperationException($"Entity '{entityId}' is also referenced by {string.Join(", ", references.Take(3))}; it cannot be removed from storage safely.");
    }

    private static void RemoveExclusivelyLockerOwnedEntity(JsonObject root, JsonArray locker, JsonObject target, int uid, string entityId)
    {
        var entities = BuildEntityIndex(root);
        var entity = entities[entityId];
        var linkedRewardId = entity.Kind == StorageEntityKind.Beast
            ? ReadEntityId(entity.Node["rwdemsrid"], $"beast entity '{entityId}'", allowEmpty: true)
            : null;
        if (!string.Equals(ReadText(entity.Node["owner"]), LockerOwner, StringComparison.Ordinal) ||
            (entity.Kind == StorageEntityKind.Part && !ReferenceEquals(entity.Parent, PartRegistry(root, uid))))
            throw new InvalidOperationException($"Entity '{entityId}' is no longer exclusively locker-owned.");
        FindReferences(root, entityId, string.Empty, target, entity.Node, out var anyReference);
        if (anyReference)
            throw new InvalidOperationException($"Entity '{entityId}' gained another reference and cannot be removed safely.");
        if (!entity.Parent.Remove(entity.Node))
            throw new InvalidOperationException($"Entity '{entityId}' could not be removed from its registry.");

        // Raw beasts own a separate reward-mushroom record. Remove that private
        // child only when ownership and the now-updated reference graph prove it
        // is not a cooked/user-owned reward or shared by another aggregate.
        if (linkedRewardId is not null)
            RemovePrivateBeastRewardIfUnreferenced(root, target, linkedRewardId);
    }

    private static void RemovePrivateBeastRewardIfUnreferenced(
        JsonObject root,
        JsonObject excludedSlot,
        string rewardEntityId)
    {
        var entities = BuildEntityIndex(root);
        if (!entities.TryGetValue(rewardEntityId, out var reward) ||
            reward.Kind != StorageEntityKind.Mushroom ||
            !string.Equals(ReadText(reward.Node["owner"]), "BEAST", StringComparison.Ordinal))
        {
            return;
        }

        FindReferences(root, rewardEntityId, string.Empty, excludedSlot, reward.Node, out var anyReference);
        if (!anyReference) reward.Parent.Remove(reward.Node);
    }

    private static void FindReferences(JsonNode? node, string eid, string pointer, JsonObject excludedSlot, JsonObject excludedEntity, ICollection<string> references)
    {
        if (node is JsonObject obj)
        {
            if (ReferenceEquals(obj, excludedSlot) || ReferenceEquals(obj, excludedEntity)) return;
            foreach (var property in obj)
            {
                var childPointer = pointer + "/" + property.Key;
                if (IsEntityReferenceProperty(property.Key) && MatchesEntityReference(property.Value, eid))
                    references.Add(childPointer);
                FindReferences(property.Value, eid, childPointer, excludedSlot, excludedEntity, references);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                FindReferences(array[index], eid, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), excludedSlot, excludedEntity, references);
        }
    }

    private static void FindReferences(JsonNode? node, string eid, string pointer, JsonObject excludedSlot, JsonObject excludedEntity, out bool found)
    {
        var references = new List<string>();
        FindReferences(node, eid, pointer, excludedSlot, excludedEntity, references);
        found = references.Count > 0;
    }

    private static bool IsEntityReferenceProperty(string property) =>
        property.Equals("eid", StringComparison.Ordinal) ||
        property.Equals("eptid", StringComparison.Ordinal) ||
        property.Equals("rwdemsrid", StringComparison.Ordinal);

    private static bool MatchesEntityReference(JsonNode? value, string eid)
    {
        var valueId = ReadText(value);
        if (valueId == eid) return true;
        return value is JsonValue && valueId is not null && valueId.Split(',', StringSplitOptions.TrimEntries).Contains(eid, StringComparer.Ordinal);
    }

    private static JsonObject ParseTemplate(string json, string description)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"The {description} template is empty.");
        try
        {
            return JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException($"The {description} template must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The {description} template is not valid JSON.", exception);
        }
    }

    private static void ValidateTemplate(StorageItemTemplate template)
    {
        if (template.Type is < 0 or > 3)
            throw new InvalidOperationException($"Storage template type {template.Type} is unsupported.");
        if (string.IsNullOrWhiteSpace(template.DefinitionId))
            throw new InvalidOperationException("The storage template has no definition ID.");

        var entity = ParseTemplate(template.EntityJson, "entity");
        var expectedProperty = template.Type switch
        {
            0 => "ptid",
            1 => "msrid",
            2 => "bstid",
            3 => "itemid",
            _ => throw new ArgumentOutOfRangeException()
        };
        var definition = ReadText(entity[expectedProperty]);
        if (!string.Equals(definition, template.DefinitionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Storage template type {template.Type} does not match definition '{template.DefinitionId}'.");

        var conflictingProperty = new[] { "ptid", "msrid", "bstid", "itemid" }
            .FirstOrDefault(property => !string.Equals(property, expectedProperty, StringComparison.Ordinal) &&
                                        !string.IsNullOrWhiteSpace(ReadText(entity[property])));
        if (conflictingProperty is not null)
            throw new InvalidOperationException($"Storage template type {template.Type} contains conflicting '{conflictingProperty}' data.");

        if (template.Type == 2 && string.IsNullOrWhiteSpace(template.LinkedMushroomJson))
            throw new InvalidOperationException("A beast storage template requires a linked reward mushroom.");
        if (template.Type != 2 && !string.IsNullOrWhiteSpace(template.LinkedMushroomJson))
            throw new InvalidOperationException("Only a beast storage template may include a linked reward mushroom.");
        if (template.Type == 2)
        {
            var mushroom = ParseTemplate(template.LinkedMushroomJson!, "linked mushroom");
            if (string.IsNullOrWhiteSpace(ReadText(mushroom["msrid"])))
                throw new InvalidOperationException("The linked beast reward has no mushroom definition ID.");
        }
    }

    private static JsonArray ResolveRegistry(JsonObject root, JsonObject entity, int type, int uid)
    {
        return type switch
        {
            0 when entity.ContainsKey("ptid") => EnsurePartRegistry(root, uid),
            1 when IsMushroom(entity) => EnsureMushroomRegistry(root),
            2 when IsBeast(entity) => EnsureBeastRegistry(root),
            3 when entity.ContainsKey("itemid") => EnsureItemRegistry(root),
            0 or 1 or 2 or 3 => throw new InvalidOperationException($"Storage template type {type} does not match its entity payload."),
            _ => throw new InvalidOperationException($"Storage template type {type} is unsupported.")
        };
    }

    private static bool IsMushroom(JsonObject entity) => entity.ContainsKey("msrid");
    private static bool IsBeast(JsonObject entity) => entity.ContainsKey("bstid");

    private static JsonArray EnsurePartRegistry(JsonObject root, int uid)
    {
        var part = EnsureObject(root, "part");
        var pts = EnsureObject(part, "pts");
        var key = uid.ToString(CultureInfo.InvariantCulture);
        return EnsureArray(pts, key);
    }

    private static JsonArray EnsureItemRegistry(JsonObject root) => EnsureArray(EnsureObject(root, "item"), "items");
    private static JsonArray EnsureMushroomRegistry(JsonObject root) => EnsureArray(EnsureObject(root, "mushroom"), "msrs");
    private static JsonArray EnsureBeastRegistry(JsonObject root) => EnsureArray(EnsureObject(root, "beast"), "bsts");

    private static JsonObject EnsureObject(JsonObject parent, string property)
    {
        if (parent[property] is JsonObject existing) return existing;
        if (parent[property] is not null) throw new InvalidOperationException($"Expected /{property} to be an object.");
        return (parent[property] = new JsonObject()).AsObject();
    }

    private static JsonArray EnsureArray(JsonObject parent, string property)
    {
        if (parent[property] is JsonArray existing) return existing;
        // Some saves use {} as a harmless placeholder for an empty registry.  It
        // is safe to normalize only that exact shape; a populated object could be
        // a different schema and must never be overwritten by a storage edit.
        if (parent[property] is JsonObject { Count: 0 })
            return (parent[property] = new JsonArray()).AsArray();
        if (parent[property] is not null) throw new InvalidOperationException($"Expected /{property} to be an array.");
        return (parent[property] = new JsonArray()).AsArray();
    }

    private static void Materialize(JsonObject entity, string eid, int uid, string owner, bool isPart)
    {
        entity["eid"] = eid;
        if (isPart) entity["uid"] = uid;
        else entity.Remove("uid");
        entity["owner"] = owner;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SetTimestamp(entity, "gettime", timestamp);
        SetTimestamp(entity, "created", timestamp);
        SetTimestamp(entity, "modified", timestamp);
    }

    private static void SetTimestamp(JsonObject entity, string property, long value)
    {
        if (entity.ContainsKey(property)) entity[property] = value;
    }

    private static string NextEntityId(IEnumerable<string> existingIds)
    {
        var occupied = existingIds.ToHashSet(StringComparer.Ordinal);
        string value;
        do value = Guid.NewGuid().ToString("D"); while (occupied.Contains(value));
        return value;
    }

    private static Dictionary<string, EntityLocation> BuildEntityIndex(JsonObject root)
    {
        var entities = new Dictionary<string, EntityLocation>(StringComparer.Ordinal);
        IndexPartRegistries(root["part"]?["pts"], entities);
        IndexRegistry(root["item"]?["items"], entities, StorageEntityKind.Item, "/item/items");
        IndexRegistry(root["mushroom"]?["msrs"], entities, StorageEntityKind.Mushroom, "/mushroom/msrs");
        IndexRegistry(root["beast"]?["bsts"], entities, StorageEntityKind.Beast, "/beast/bsts");
        return entities;
    }

    private static void IndexPartRegistries(JsonNode? node, IDictionary<string, EntityLocation> entities)
    {
        if (node is null || node is JsonObject { Count: 0 }) return;
        var registries = node as JsonObject
            ?? throw new InvalidOperationException("Expected /part/pts to be an object keyed by player UID.");
        foreach (var (uid, registry) in registries)
        {
            IndexRegistry(registry, entities, StorageEntityKind.Part, $"/part/pts/{uid}");
        }
    }

    private static void IndexRegistry(JsonNode? node, IDictionary<string, EntityLocation> entities, StorageEntityKind kind, string path)
    {
        if (node is null || node is JsonObject { Count: 0 }) return;
        var array = node as JsonArray ?? throw new InvalidOperationException($"Expected {path} to be an array.");
        foreach (var child in array)
        {
            var entity = child?.AsObject() ?? throw new InvalidOperationException($"An entity in {path} is not an object.");
            var id = ReadEntityId(entity["eid"], $"entity in {path}", allowEmpty: false)!;
            if (!entities.TryAdd(id, new EntityLocation(entity, array, kind)))
                throw new InvalidOperationException($"Entity ID '{id}' is duplicated across inventory registries.");
        }
    }

    private static JsonArray FindParentArray(JsonObject entity) => entity.Parent as JsonArray
        ?? throw new InvalidOperationException("An inventory entity is not stored in an array.");

    private static string? DefinitionId(JsonObject entity)
    {
        foreach (var name in new[] { "ptid", "itid", "itemid", "msrid", "bstid" })
        {
            var value = ReadText(entity[name]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static JsonArray PartRegistry(JsonObject root, int uid) =>
        root["part"]?["pts"]?[uid.ToString(CultureInfo.InvariantCulture)] as JsonArray
        ?? throw new InvalidOperationException("The active player's equipment registry is missing.");

    private static int StorageType(StorageEntityKind kind) => kind switch
    {
        StorageEntityKind.Part => 0,
        StorageEntityKind.Mushroom => 1,
        StorageEntityKind.Beast => 2,
        StorageEntityKind.Item => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ValidateLocker(JsonArray locker)
    {
        var seen = new HashSet<int>();
        foreach (var node in locker)
        {
            var slot = node?.AsObject() ?? throw new InvalidOperationException("A storage slot is not an object.");
            if (!seen.Add(RequireInt(slot, "slot", "storage slot")))
                throw new InvalidOperationException("Storage contains duplicate slot IDs.");
            if (RequireInt(slot, "slot", "storage slot") < 0)
                throw new InvalidOperationException("A storage slot cannot have a negative ID.");
            var type = RequireInt(slot, "type", "storage slot");
            var eid = ReadEntityId(slot["eid"], "storage slot", allowEmpty: true);
            if (eid is null && type != -1)
                throw new InvalidOperationException("An empty storage slot must have type -1.");
            if (eid is not null && type is < 0 or > 3)
                throw new InvalidOperationException("An occupied storage slot has an unsupported type.");
        }
    }

    private static int RequireInt(JsonObject value, string property, string description) =>
        TryInt(value[property], out var result) ? result : throw new InvalidOperationException($"The {description} has no valid '{property}' value.");

    private static bool TryInt(JsonNode? value, out int result)
    {
        if (value is JsonValue jsonValue && (jsonValue.TryGetValue<int>(out result) ||
            (jsonValue.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))))
            return true;
        result = default;
        return false;
    }

    private static string? ReadText(JsonNode? value)
    {
        if (value is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<string>(out var text)) return string.IsNullOrWhiteSpace(text) ? null : text;
        if (jsonValue.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static string? ReadEntityId(JsonNode? value, string description, bool allowEmpty)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
            throw new InvalidOperationException($"The {description} has no string UUID 'eid' value.");
        if (string.IsNullOrWhiteSpace(text))
        {
            if (allowEmpty) return null;
            throw new InvalidOperationException($"The {description} has an empty 'eid' value.");
        }
        if (!Guid.TryParse(text, out _))
            throw new InvalidOperationException($"The {description} has a non-UUID 'eid' value.");
        return text;
    }

    private sealed record EntityLocation(JsonObject Node, JsonArray Parent, StorageEntityKind Kind);
    private enum StorageEntityKind { Part, Item, Mushroom, Beast }
}
