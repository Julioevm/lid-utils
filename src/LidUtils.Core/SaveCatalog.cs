using System.Text.Json;
using System.Text.Json.Serialization;

namespace LidUtils.Core;

public sealed record SaveValueDefinition(
    [property: JsonRequired] string Pointer,
    [property: JsonRequired] string Label,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string Category,
    [property: JsonRequired] SaveValueType ValueType);

public sealed class SaveValueCatalog
{
    private readonly IReadOnlyDictionary<string, SaveValueDefinition> _definitions;

    public SaveValueCatalog(int schemaVersion, string catalogVersion, IEnumerable<SaveValueDefinition> definitions)
    {
        SchemaVersion = schemaVersion;
        CatalogVersion = catalogVersion;
        _definitions = definitions.ToDictionary(item => item.Pointer, StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }
    public string CatalogVersion { get; }
    public IReadOnlyCollection<SaveValueDefinition> Definitions => _definitions.Values.ToArray();
    public bool TryGet(string pointer, out SaveValueDefinition? definition) => _definitions.TryGetValue(pointer, out definition);
    public static SaveValueCatalog Empty { get; } = new(1, "empty", []);

    public SaveCatalogApplicationResult Apply(IEnumerable<SaveValueEntry> entries)
    {
        var sourceEntries = entries.ToArray();
        var result = new List<SaveValueEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var duplicate in sourceEntries.Where(item => _definitions.ContainsKey(item.Pointer)).GroupBy(item => item.Pointer, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"The save contains {duplicate.Count()} values for catalog pointer '{duplicate.Key}'; the save cannot be edited safely.");
        foreach (var entry in sourceEntries)
        {
            if (!TryGet(entry.Pointer, out var definition) || definition is null)
            {
                result.Add(entry);
                continue;
            }

            seen.Add(entry.Pointer);
            if (entry.Type != definition.ValueType)
                errors.Add($"Catalog save value '{entry.Pointer}' declares {definition.ValueType}, but the save data supplies {entry.Type}.");
            result.Add(entry with { Definition = definition });
        }

        if (errors.Count > 0) throw new CatalogValidationException(errors);
        var warnings = Definitions.Where(item => !seen.Contains(item.Pointer))
            .Select(item => $"Catalog save value '{item.Pointer}' is not present in this save.")
            .ToArray();
        return new SaveCatalogApplicationResult(result, warnings);
    }
}

public sealed record SaveCatalogApplicationResult(IReadOnlyList<SaveValueEntry> Entries, IReadOnlyList<string> Warnings);

public static class SaveCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SaveValueCatalog Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (CatalogValidationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CatalogValidationException([$"Could not load '{path}': {exception.Message}"]);
        }
    }

    public static SaveValueCatalog Parse(string json)
    {
        SaveCatalogDocument? document;
        try { document = JsonSerializer.Deserialize<SaveCatalogDocument>(json, JsonOptions); }
        catch (JsonException exception) { throw new CatalogValidationException([$"JSON error: {exception.Message}"]); }
        if (document is null) throw new CatalogValidationException(["The catalog document is empty."]);

        var errors = new List<string>();
        if (document.SchemaVersion != 1) errors.Add($"Unsupported schemaVersion '{document.SchemaVersion}'. Expected 1.");
        if (string.IsNullOrWhiteSpace(document.CatalogVersion)) errors.Add("catalogVersion is required.");
        var definitions = document.Values ?? [];
        foreach (var duplicate in definitions.GroupBy(item => item.Pointer, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"Duplicate pointer mapping '{duplicate.Key}'. Each pointer must be unique.");
        foreach (var item in definitions) ValidateDefinition(item, errors);
        if (errors.Count > 0) throw new CatalogValidationException(errors);
        return new SaveValueCatalog(document.SchemaVersion, document.CatalogVersion!, definitions);
    }

    private static void ValidateDefinition(SaveValueDefinition item, List<string> errors)
    {
        if (item.Pointer.Length < 2 || item.Pointer[0] != '/')
            errors.Add($"Save value '{item.Pointer}' requires a JSON pointer that starts with '/' and names a property.");
        if (item.Pointer.Length >= 2 && item.Pointer[1..].Contains("//", StringComparison.Ordinal))
            errors.Add($"Save value '{item.Pointer}' contains an empty path segment.");
        if (string.IsNullOrWhiteSpace(item.Label)) errors.Add($"Save value '{item.Pointer}' requires a label.");
        if (string.IsNullOrWhiteSpace(item.Description)) errors.Add($"Save value '{item.Pointer}' requires a description.");
        if (string.IsNullOrWhiteSpace(item.Category)) errors.Add($"Save value '{item.Pointer}' requires a category.");
    }

    private sealed record SaveCatalogDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string? CatalogVersion,
        [property: JsonRequired] List<SaveValueDefinition>? Values);
}
