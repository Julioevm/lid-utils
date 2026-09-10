using System.Text.Json;
using System.Text.Json.Serialization;

namespace LidUtils.Core;

/// <summary>
/// Loads the curated map-area catalog (settings/map-area-info.json) produced by
/// tools/import_floor_data.py. Pure JSON: no database or network access.
/// </summary>
public static class MapAreaInfoCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static MapAreaInfoCatalog Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (CatalogValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CatalogValidationException([$"Could not load '{path}': {exception.Message}"]);
        }
    }

    public static MapAreaInfoCatalog Parse(string json)
    {
        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException([$"JSON error: {exception.Message}"]);
        }

        if (document is null) throw new CatalogValidationException(["The map-area catalog document is empty."]);

        var errors = new List<string>();
        if (document.SchemaVersion != 1)
            errors.Add($"Unsupported schemaVersion '{document.SchemaVersion}'. Expected 1.");

        var templateRotation = document.TemplateRotation ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var template in TowerMapCatalog.TemplateIds)
        {
            if (!templateRotation.TryGetValue(template, out var rotation) || string.IsNullOrWhiteSpace(rotation))
                errors.Add($"templateRotation is missing an entry for template '{template}'.");
        }

        var nameAliases = document.NameAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = document.Areas ?? [];
        if (definitions.Count == 0) errors.Add("The map-area catalog contains no areas.");

        var areas = new List<MapAreaInfo>(definitions.Count);
        var warnings = new List<string>();
        var seen = new HashSet<(int Floor, string Name, string Rotations)>();
        foreach (var definition in definitions)
        {
            var name = definition.Name ?? string.Empty;
            if (definition.Floor <= 0) errors.Add($"Area '{name}' has an invalid floor '{definition.Floor}'.");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("An area row is missing its name.");

            var rotations = definition.Rotations ?? [];
            if (rotations.Count == 0) warnings.Add($"Area '{name}' on floor {definition.Floor} lists no rotations.");
            foreach (var rotation in rotations)
            {
                if (string.IsNullOrWhiteSpace(rotation))
                    warnings.Add($"Area '{name}' on floor {definition.Floor} has a blank rotation id.");
            }

            var key = (definition.Floor, name, string.Join(",", rotations));
            if (!seen.Add(key))
                warnings.Add($"Duplicate area row '{name}' on floor {definition.Floor} for rotations [{key.Item3}].");

            areas.Add(new MapAreaInfo(
                definition.Floor,
                name,
                rotations,
                definition.Info ?? string.Empty,
                definition.Material ?? string.Empty,
                definition.Stamp,
                definition.Shop ?? string.Empty,
                definition.Boss ?? string.Empty,
                definition.Trap ?? string.Empty,
                definition.YotsuyamaBionics,
                definition.Tales,
                definition.Notes ?? string.Empty));
        }

        if (errors.Count > 0) throw new CatalogValidationException(errors);
        return MapAreaInfoCatalog.Create(areas, templateRotation, nameAliases, warnings);
    }

    private sealed record CatalogDocument(
        [property: JsonRequired] int SchemaVersion,
        DocumentSource? Source,
        IReadOnlyDictionary<string, string>? TemplateRotation,
        IReadOnlyDictionary<string, string>? NameAliases,
        [property: JsonRequired] List<AreaDocument>? Areas);

    private sealed record DocumentSource(
        string? Name,
        string? Authors,
        string? Sheet,
        string? Updated,
        string? Note);

    private sealed record AreaDocument(
        [property: JsonRequired] int Floor,
        [property: JsonRequired] string? Name,
        IReadOnlyList<string>? Rotations,
        string? Info,
        string? Material,
        bool Stamp,
        string? Shop,
        string? Boss,
        string? Trap,
        int? YotsuyamaBionics,
        string? Tales,
        string? Notes);
}
