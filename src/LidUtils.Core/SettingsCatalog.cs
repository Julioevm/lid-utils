using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LidUtils.Core;

public readonly record struct SettingId(string SourceTable, string Key)
{
    public override string ToString() => $"{SourceTable}:{Key}";

    public static bool TryParse(string value, out SettingId id)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            id = default;
            return false;
        }

        id = new SettingId(value[..separator], value[(separator + 1)..]);
        return true;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel { Low, Moderate, High, Experimental }

public sealed record DisplayConversion(string Kind, double Scale = 1, double Offset = 0);

public sealed record SettingDefinition(
    [property: JsonRequired] string SourceTable,
    [property: JsonRequired] string Key,
    [property: JsonRequired] string Label,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string Category,
    [property: JsonRequired] SettingValueType ValueType,
    [property: JsonRequired] string RawUnits,
    string? DisplayUnits,
    double? Minimum,
    double? Maximum,
    double? Step,
    string? DefaultDisplayFormat,
    DisplayConversion? Conversion,
    [property: JsonRequired] RiskLevel Risk)
{
    public SettingId Id => new(SourceTable, Key);

    public string FormatDisplayValue(string rawValue, bool isNull)
    {
        if (isNull) return "(NULL)";
        if (ValueType == SettingValueType.String) return rawValue;
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)) return rawValue;
        if (Conversion is not null) numeric = (numeric * Conversion.Scale) + Conversion.Offset;
        return numeric.ToString(DefaultDisplayFormat ?? "G", CultureInfo.InvariantCulture);
    }
}

public sealed class SettingsCatalog
{
    private readonly IReadOnlyDictionary<SettingId, SettingDefinition> _definitions;

    public SettingsCatalog(int schemaVersion, string catalogVersion, IEnumerable<SettingDefinition> definitions)
    {
        SchemaVersion = schemaVersion;
        CatalogVersion = catalogVersion;
        _definitions = definitions.ToDictionary(item => item.Id);
    }

    public int SchemaVersion { get; }
    public string CatalogVersion { get; }
    public IReadOnlyCollection<SettingDefinition> Definitions => _definitions.Values.ToArray();
    public bool TryGet(SettingId id, out SettingDefinition? definition) => _definitions.TryGetValue(id, out definition);
    public static SettingsCatalog Empty { get; } = new(1, "empty", []);

    public CatalogApplicationResult Apply(IEnumerable<SettingEntry> entries)
    {
        var sourceEntries = entries.ToArray();
        var result = new List<SettingEntry>();
        var seen = new HashSet<SettingId>();
        var errors = new List<string>();
        foreach (var duplicate in sourceEntries.Where(item => _definitions.ContainsKey(item.Id)).GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add($"Database contains {duplicate.Count()} rows for catalog setting '{duplicate.Key}'; the table + primary-key mapping is ambiguous.");
        foreach (var entry in sourceEntries)
        {
            if (!TryGet(entry.Id, out var definition) || definition is null)
            {
                result.Add(entry);
                continue;
            }

            seen.Add(entry.Id);
            ValidateDatabaseValue(entry, definition, errors);
            result.Add(entry with { Definition = definition });
        }

        if (errors.Count > 0) throw new CatalogValidationException(errors);
        var warnings = Definitions.Where(item => !seen.Contains(item.Id))
            .Select(item => $"Catalog setting '{item.Id}' is not present in this database revision.")
            .ToArray();
        return new CatalogApplicationResult(result, warnings);
    }

    private static void ValidateDatabaseValue(SettingEntry entry, SettingDefinition definition, List<string> errors)
    {
        if (entry.ValueType != definition.ValueType)
        {
            errors.Add($"Catalog setting '{entry.Id}' declares {definition.ValueType}, but the database table supplies {entry.ValueType}.");
            return;
        }

        if (entry.IsNull && entry.ValueType != SettingValueType.String)
        {
            errors.Add($"Catalog setting '{entry.Id}' has a NULL database value, which is incompatible with {entry.ValueType}.");
            return;
        }

        if (entry.IsNull || entry.ValueType == SettingValueType.String) return;
        if (!double.TryParse(entry.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            errors.Add($"Database value '{entry.RawValue}' for catalog setting '{entry.Id}' is not a valid finite {entry.ValueType} value.");
            return;
        }

        if (definition.Minimum is not null && value < definition.Minimum)
            errors.Add($"Database value {entry.RawValue} for catalog setting '{entry.Id}' is below catalog minimum {definition.Minimum.Value.ToString(CultureInfo.InvariantCulture)}.");
        if (definition.Maximum is not null && value > definition.Maximum)
            errors.Add($"Database value {entry.RawValue} for catalog setting '{entry.Id}' is above catalog maximum {definition.Maximum.Value.ToString(CultureInfo.InvariantCulture)}.");
    }
}

public sealed record CatalogApplicationResult(IReadOnlyList<SettingEntry> Entries, IReadOnlyList<string> Warnings);

public sealed class CatalogValidationException : Exception
{
    public CatalogValidationException(IEnumerable<string> errors)
        : base("Settings catalog is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error => "• " + error)))
    {
        Errors = errors.ToArray();
    }
    public IReadOnlyList<string> Errors { get; }
}

public static class SettingsCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SettingsCatalog Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (CatalogValidationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CatalogValidationException([$"Could not load '{path}': {exception.Message}"]);
        }
    }

    public static SettingsCatalog Parse(string json)
    {
        CatalogDocument? document;
        try { document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions); }
        catch (JsonException exception) { throw new CatalogValidationException([$"JSON error: {exception.Message}"]); }
        if (document is null) throw new CatalogValidationException(["The catalog document is empty."]);

        var errors = new List<string>();
        if (document.SchemaVersion != 1) errors.Add($"Unsupported schemaVersion '{document.SchemaVersion}'. Expected 1.");
        if (string.IsNullOrWhiteSpace(document.CatalogVersion)) errors.Add("catalogVersion is required.");
        var definitions = document.Settings ?? [];
        foreach (var duplicate in definitions.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add($"Duplicate source mapping '{duplicate.Key}'. Each table + key pair must be unique.");
        foreach (var item in definitions) ValidateDefinition(item, errors);
        if (errors.Count > 0) throw new CatalogValidationException(errors);
        return new SettingsCatalog(document.SchemaVersion, document.CatalogVersion!, definitions);
    }

    private static void ValidateDefinition(SettingDefinition item, List<string> errors)
    {
        var id = item.Id.ToString();
        if (item.SourceTable is not ("master_const_int" or "master_const_float" or "master_const_str"))
            errors.Add($"Setting '{id}' uses unsupported sourceTable '{item.SourceTable}'.");
        if (string.IsNullOrWhiteSpace(item.Key)) errors.Add($"Setting '{id}' requires a key.");
        if (string.IsNullOrWhiteSpace(item.Label)) errors.Add($"Setting '{id}' requires a label.");
        if (string.IsNullOrWhiteSpace(item.Description)) errors.Add($"Setting '{id}' requires a description.");
        if (string.IsNullOrWhiteSpace(item.Category)) errors.Add($"Setting '{id}' requires a category.");
        if (string.IsNullOrWhiteSpace(item.RawUnits)) errors.Add($"Setting '{id}' requires rawUnits.");
        var expectedType = item.SourceTable switch
        {
            "master_const_int" => SettingValueType.Integer,
            "master_const_float" => SettingValueType.Float,
            "master_const_str" => SettingValueType.String,
            _ => item.ValueType
        };
        if (item.ValueType != expectedType) errors.Add($"Setting '{id}' declares {item.ValueType}, but {item.SourceTable} requires {expectedType}.");
        if (item.ValueType == SettingValueType.String && (item.Minimum is not null || item.Maximum is not null || item.Step is not null || item.Conversion is not null))
            errors.Add($"String setting '{id}' cannot define numeric ranges, step, or conversion.");
        if (item.Minimum is not null && !double.IsFinite(item.Minimum.Value)) errors.Add($"Setting '{id}' minimum must be finite.");
        if (item.Maximum is not null && !double.IsFinite(item.Maximum.Value)) errors.Add($"Setting '{id}' maximum must be finite.");
        if (item.Minimum > item.Maximum) errors.Add($"Setting '{id}' minimum cannot exceed maximum.");
        if (item.Step is not null && (!double.IsFinite(item.Step.Value) || item.Step <= 0)) errors.Add($"Setting '{id}' step must be a positive finite number.");
        if (item.ValueType == SettingValueType.Integer && new[] { item.Minimum, item.Maximum, item.Step }.Where(value => value is not null).Any(value => value!.Value != Math.Truncate(value.Value)))
            errors.Add($"Integer setting '{id}' must use whole-number minimum, maximum, and step values.");
        if (item.Conversion is not null)
        {
            if (!item.Conversion.Kind.Equals("scaleOffset", StringComparison.Ordinal)) errors.Add($"Setting '{id}' conversion kind must be 'scaleOffset'.");
            if (!double.IsFinite(item.Conversion.Scale) || item.Conversion.Scale == 0 || !double.IsFinite(item.Conversion.Offset)) errors.Add($"Setting '{id}' conversion scale must be non-zero and all conversion values must be finite.");
            if (string.IsNullOrWhiteSpace(item.DisplayUnits)) errors.Add($"Setting '{id}' with a conversion requires displayUnits.");
        }
        if (!string.IsNullOrEmpty(item.DefaultDisplayFormat) && item.ValueType != SettingValueType.String)
        {
            try { _ = 1.0.ToString(item.DefaultDisplayFormat, CultureInfo.InvariantCulture); }
            catch (FormatException) { errors.Add($"Setting '{id}' has invalid defaultDisplayFormat '{item.DefaultDisplayFormat}'."); }
        }
    }

    private sealed record CatalogDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string? CatalogVersion,
        [property: JsonRequired] List<SettingDefinition>? Settings);
}
