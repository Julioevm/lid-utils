namespace LidUtils.Core;

/// <summary>Read-only progression limits for a fighter body definition.</summary>
public sealed record BodyStatDefinition(
    string Type,
    int Grade,
    int LimitBreak,
    int ParameterLevelMaximum,
    int? BagCapacity = null,
    int? SkillSlots = null,
    int? RageCapacity = null);

public sealed record BodyStatCatalogLoadResult(
    IReadOnlyList<BodyStatDefinition> Definitions,
    IReadOnlyList<string> Warnings)
{
    public BodyStatDefinition? Find(string type, int grade, int limitBreak) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase) &&
            definition.Grade == grade && definition.LimitBreak == limitBreak);
}

public interface IBodyStatCatalogService
{
    /// <summary>Loads body progression definitions from a validated masters database without modifying it.</summary>
    Task<BodyStatCatalogLoadResult> LoadBodyStatsAsync(string databasePath, CancellationToken cancellationToken = default);
}
