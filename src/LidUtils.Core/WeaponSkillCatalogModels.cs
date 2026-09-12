namespace LidUtils.Core;

/// <summary>
/// Read-only progression limits for a weapon skill (expertise) type, resolved from masters.db.
/// The observed save stores one row per weapon type with a level and accumulated ABP.
/// </summary>
public sealed record WeaponSkillDefinition(
    string WeaponType,
    string DisplayName,
    int MaximumLevel,
    IReadOnlyDictionary<int, long> RequiredAbpByLevel)
{
    /// <summary>
    /// Cumulative ABP required to hold the supplied level, or null when the level is outside the
    /// catalogued range. Level 1 is the starting level and requires no ABP.
    /// </summary>
    public long? RequiredAbpForLevel(int level)
    {
        if (level <= 1) return 0;
        if (level > MaximumLevel) return null;
        return RequiredAbpByLevel.TryGetValue(level, out var required) ? required : null;
    }
}

public sealed record WeaponSkillCatalogLoadResult(
    IReadOnlyList<WeaponSkillDefinition> Definitions,
    IReadOnlyList<string> Warnings)
{
    public WeaponSkillDefinition? Find(string weaponType) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.WeaponType, weaponType, StringComparison.OrdinalIgnoreCase));
}

public interface IWeaponSkillCatalogService
{
    /// <summary>Loads weapon skill level caps and ABP costs from a validated masters database without modifying it.</summary>
    Task<WeaponSkillCatalogLoadResult> LoadWeaponSkillsAsync(
        string databasePath,
        string? language = null,
        CancellationToken cancellationToken = default);
}
