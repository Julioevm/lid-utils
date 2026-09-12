using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidUtils.Core;

/// <summary>
/// A single weapon skill (expertise) entry stored at /soul/expert/&lt;index&gt;. The game raises
/// the passive bonuses for a weapon type as its level increases.
/// </summary>
public sealed record WeaponSkillEntry(
    int Index,
    string WeaponType,
    int Level,
    long Abp,
    bool IsChecked,
    string LevelPointer,
    string AbpPointer);

public sealed record WeaponSkillInventory(
    IReadOnlyList<WeaponSkillEntry> Skills,
    IReadOnlyList<string> Warnings)
{
    public static WeaponSkillInventory Read(string json) => WeaponSkillInventoryReader.Read(json);
}

internal static class WeaponSkillInventoryReader
{
    private const string ExpertPointer = "/soul/expert";

    public static WeaponSkillInventory Read(string json)
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

        var warnings = new List<string>();
        var result = new List<WeaponSkillEntry>();
        var expert = root["soul"]?["expert"];
        if (expert is not JsonArray rows)
        {
            if (expert is null) warnings.Add($"{ExpertPointer} is missing.");
            else if (expert is not JsonObject { Count: 0 }) warnings.Add($"{ExpertPointer} is not an array.");
            return new(result, warnings);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row)
            {
                warnings.Add($"Weapon skill row {index} is not an object.");
                continue;
            }

            var weaponType = Text(row["ptarmtp"]);
            if (string.IsNullOrWhiteSpace(weaponType))
            {
                warnings.Add($"Weapon skill row {index} has no weapon type.");
                continue;
            }

            if (!TryInt(row["lvl"], out var level))
            {
                warnings.Add($"Weapon skill '{weaponType}' has no valid level.");
                continue;
            }

            var abp = TryLong(row["abp"], out var readAbp) ? readAbp : 0L;
            result.Add(new WeaponSkillEntry(
                index,
                weaponType,
                level,
                abp,
                TryInt(row["is_checked"], out var isChecked) && isChecked != 0,
                $"{ExpertPointer}/{index}/lvl",
                $"{ExpertPointer}/{index}/abp"));
        }

        return new(result, warnings);
    }

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryInt(JsonNode? node, out int result)
    {
        if (TryLong(node, out var value) && value is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)value;
            return true;
        }
        result = default;
        return false;
    }

    private static bool TryLong(JsonNode? node, out long result)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out result)) return true;
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
        }
        result = default;
        return false;
    }
}
