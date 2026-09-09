using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidUtils.Core;

/// <summary>A decal ("skill") definition resolved from the installed game database.</summary>
public sealed record DecalDefinition(
    string SkillId,
    string DisplayName,
    bool Premium,
    int? Rarity,
    string TypeLabel,
    string Description = "");

public sealed record DecalCatalogLoadResult(
    IReadOnlyList<DecalDefinition> Definitions,
    IReadOnlyList<string> Warnings)
{
    public static DecalCatalogLoadResult Empty { get; } = new([], []);
}

public interface IDecalCatalogService
{
    /// <summary>
    /// Reads decal definitions from <paramref name="databasePath"/> without modifying it.
    /// Language is a LET IT DIE master-text language code (for example, <c>int</c>).
    /// </summary>
    Task<DecalCatalogLoadResult> LoadDecalsAsync(
        string databasePath,
        string? language = null,
        CancellationToken cancellationToken = default);
}

/// <summary>One owned decal row from /soul/skl/psskl in the save.</summary>
public sealed record DecalOwnedRow(
    int Index,
    string SkillId,
    long Count,
    long Updated,
    long IsChecked,
    string CountPointer);

/// <summary>One equipped decal record from /soul/skl/eqskl/&lt;loadout&gt; in the save.</summary>
public sealed record EquippedDecalEntry(
    string SkillId,
    string FighterId,
    int Slot,
    string LoadoutKey);

/// <summary>
/// Read-only decal inventory parsed from decoded save JSON. Counts are edited through
/// plain scalar staging on the row pointers; this type only reads and aggregates.
/// </summary>
public sealed record DecalInventory(
    IReadOnlyList<DecalOwnedRow> Owned,
    IReadOnlyList<EquippedDecalEntry> Equipped)
{
    /// <summary>The game stores at most this many copies of one decal type.</summary>
    public const long MaximumQuantity = 4;

    public int EquippedCount(string skillId)
    {
        ArgumentNullException.ThrowIfNull(skillId);
        var count = 0;
        foreach (var entry in Equipped)
            if (string.Equals(entry.SkillId, skillId, StringComparison.Ordinal)) count++;
        return count;
    }

    public bool ContainsSkill(string skillId)
    {
        ArgumentNullException.ThrowIfNull(skillId);
        foreach (var row in Owned)
            if (string.Equals(row.SkillId, skillId, StringComparison.Ordinal)) return true;
        return false;
    }

    public static DecalInventory Read(string json)
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

        return new DecalInventory(ReadOwned(root), ReadEquipped(root));
    }

    private static IReadOnlyList<DecalOwnedRow> ReadOwned(JsonObject root)
    {
        if (root["soul"]?["skl"]?["psskl"] is not { } node) return [];
        if (node is JsonObject { Count: 0 }) return [];
        if (node is not JsonArray rows)
            throw new InvalidOperationException("Expected /soul/skl/psskl to be an array.");

        var owned = new List<DecalOwnedRow>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row)
                throw new InvalidOperationException($"A decal row at /soul/skl/psskl/{index} is not an object.");
            var skillId = RequireText(row["sklid"], $"decal row /soul/skl/psskl/{index}", "sklid");
            var count = RequireInt(row["cnt"], $"decal row /soul/skl/psskl/{index}", "cnt");
            var updated = TryLong(row["updated"], out var updatedValue) ? updatedValue : 0;
            var isChecked = TryLong(row["is_checked"], out var checkedValue) ? checkedValue : 0;
            owned.Add(new DecalOwnedRow(
                index,
                skillId,
                count,
                updated,
                isChecked,
                $"/soul/skl/psskl/{index.ToString(CultureInfo.InvariantCulture)}/cnt"));
        }

        return owned;
    }

    private static IReadOnlyList<EquippedDecalEntry> ReadEquipped(JsonObject root)
    {
        if (root["soul"]?["skl"]?["eqskl"] is not JsonObject loadouts) return [];
        var equipped = new List<EquippedDecalEntry>();
        foreach (var (loadoutKey, value) in loadouts)
        {
            // Some saves use {} as a harmless placeholder for an unused loadout.
            if (value is not JsonArray records)
            {
                if (value is JsonObject { Count: 0 }) continue;
                throw new InvalidOperationException($"Expected /soul/skl/eqskl/{loadoutKey} to be an array.");
            }

            for (var index = 0; index < records.Count; index++)
            {
                if (records[index] is not JsonObject record)
                    throw new InvalidOperationException(
                        $"An equipped decal record at /soul/skl/eqskl/{loadoutKey}/{index} is not an object.");
                var skillId = RequireText(record["sklid"], $"equipped decal /soul/skl/eqskl/{loadoutKey}/{index}", "sklid");
                var fighterId = RequireText(record["cid"], $"equipped decal /soul/skl/eqskl/{loadoutKey}/{index}", "cid");
                var slot = (int)RequireInt(record["slot"], $"equipped decal /soul/skl/eqskl/{loadoutKey}/{index}", "slot");
                equipped.Add(new EquippedDecalEntry(skillId, fighterId, slot, loadoutKey));
            }
        }

        return equipped;
    }

    private static string RequireText(JsonNode? value, string description, string property)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) &&
            !string.IsNullOrWhiteSpace(text))
            return text;
        throw new InvalidOperationException($"The {description} has no valid '{property}' value.");
    }

    private static long RequireInt(JsonNode? value, string description, string property) =>
        TryLong(value, out var result)
            ? result
            : throw new InvalidOperationException($"The {description} has no valid '{property}' value.");

    private static bool TryLong(JsonNode? value, out long result)
    {
        if (value is JsonValue jsonValue && (jsonValue.TryGetValue<long>(out result) ||
            (jsonValue.TryGetValue<string>(out var text) &&
             long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))))
            return true;
        result = default;
        return false;
    }
}

/// <summary>
/// Grants ownership of a never-owned decal by appending a row to /soul/skl/psskl.
/// Grants only append, so they never reindex existing rows and can be applied in the
/// same operation as scalar edits.
/// </summary>
public sealed record GrantDecalOperation(string SkillId, long Quantity);

/// <summary>Applies decal grant operations to decoded save JSON.</summary>
public static class DecalEngine
{
    /// <summary>Appends one owned decal row per grant. Returns the updated JSON.</summary>
    public static string Apply(string json, IReadOnlyCollection<GrantDecalOperation> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count == 0) return json;

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

        if (root["soul"] is not JsonObject soul)
        {
            if (root["soul"] is not null)
                throw new InvalidOperationException("Expected /soul to be an object.");
            soul = [];
            root["soul"] = soul;
        }

        if (soul["skl"] is not JsonObject skill)
        {
            if (soul["skl"] is not null)
                throw new InvalidOperationException("Expected /soul/skl to be an object.");
            skill = [];
            soul["skl"] = skill;
        }

        JsonArray rows;
        switch (skill["psskl"])
        {
            case JsonArray array:
                rows = array;
                break;
            case null:
            case JsonObject { Count: 0 }:
                // {} is a harmless placeholder some saves use instead of an empty array.
                rows = [];
                skill["psskl"] = rows;
                break;
            default:
                throw new InvalidOperationException("Expected /soul/skl/psskl to be an array.");
        }

        var ownedSkillIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row is JsonObject owned && owned["sklid"] is JsonValue value &&
                value.TryGetValue<string>(out var skillId))
                ownedSkillIds.Add(skillId);

        foreach (var grant in grants)
            if (!ownedSkillIds.Add(grant.SkillId))
                throw new InvalidOperationException(
                    $"The save already owns decal '{grant.SkillId}'. Undo the staged grant instead.");

        var updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant.SkillId))
                throw new InvalidOperationException("A decal grant has no decal ID.");
            if (grant.Quantity < 1 || grant.Quantity > DecalInventory.MaximumQuantity)
                throw new InvalidOperationException(
                    $"Grant quantity for '{grant.SkillId}' must be between 1 and {DecalInventory.MaximumQuantity}.");
            rows.Add(new JsonObject
            {
                ["sklid"] = grant.SkillId,
                ["cnt"] = grant.Quantity,
                ["updated"] = updated,
                ["is_checked"] = 0
            });
        }

        return root.ToJsonString();
    }
}
