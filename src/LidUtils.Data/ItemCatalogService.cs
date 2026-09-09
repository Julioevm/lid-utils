using System.Text.Json;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

/// <summary>Reads master definitions using read-only SQLite connections.</summary>
public sealed class ItemCatalogService : IItemCatalogService, IDecalCatalogService
{
    private readonly object _snapshotGate = new();
    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;

    public async Task<ItemCatalogLoadResult> LoadAsync(string databasePath, string? language = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var entries = new List<ItemCatalogEntry>();
        var data = new Dictionary<(ItemCatalogCategory, string), TemplateData>();
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new ItemCatalogLoadResult(entries, ["The masters database path does not exist; the item catalog is unavailable."]);

        try
        {
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
            var hasText = Has(await ColumnsAsync(connection, "master_text", cancellationToken), "sct", "id", "lang", "txt");
            if (!hasText) warnings.Add("Table 'master_text' is missing or incomplete; definition keys are shown instead of localized names.");
            var requestedLanguage = string.IsNullOrWhiteSpace(language) ? "int" : language.Trim();
            await LoadPartsAsync(connection, requestedLanguage, hasText, entries, data, warnings, cancellationToken);
            await LoadItemsAsync(connection, requestedLanguage, hasText, entries, data, warnings, cancellationToken);
            await LoadMushroomsAsync(connection, requestedLanguage, hasText, entries, data, warnings, cancellationToken);
            await LoadBeastsAsync(connection, requestedLanguage, hasText, entries, data, warnings, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The item catalog could not be read: {exception.Message}");
        }

        var result = new ItemCatalogLoadResult(entries.OrderBy(x => x.Category).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(), warnings.ToArray());
        // Keep a known-good snapshot if an interrupted/broken refresh returned no usable definitions.
        if (entries.Count > 0)
            lock (_snapshotGate) _snapshot = new CatalogSnapshot(result.Entries, data);
        return result;
    }

    public ItemCatalogTemplateResult CreateTemplate(ItemCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CatalogSnapshot snapshot;
        lock (_snapshotGate) snapshot = _snapshot;
        if (!snapshot.Contains(entry))
            return new ItemCatalogTemplateResult(null, "This definition is stale. Reload the item catalog before creating it.");
        if (!entry.IsTemplateSupported)
            return new ItemCatalogTemplateResult(null, entry.TemplateUnsupportedReason ?? "This definition does not have a validated storage template.");
        if (!snapshot.Data.TryGetValue((entry.Category, entry.DefinitionId), out var data))
            return new ItemCatalogTemplateResult(null, "This definition is missing the baseline master data needed to create it.");

        return entry.Category switch
        {
            ItemCatalogCategory.Equipment when data.Durability is not null && data.Capacity is not null && data.Spare is not null =>
                new(new StorageItemTemplate(0, entry.DefinitionId, entry.DisplayName, JsonSerializer.Serialize(new
                {
                    gettime = 0, created = 0, modified = 0, ptid = entry.DefinitionId,
                    rest = data.Capacity.Value, spare = data.Spare.Value,
                    // This is an instance grade, not master_part.rarity.
                    grade = 0, dur = data.Durability.Value, lvl = 1
                })), null),
            ItemCatalogCategory.Item => new(new StorageItemTemplate(3, entry.DefinitionId, entry.DisplayName,
                JsonSerializer.Serialize(new { gettime = 0, itemid = entry.DefinitionId })), null),
            ItemCatalogCategory.Mushroom => new(new StorageItemTemplate(1, entry.DefinitionId, entry.DisplayName, MushroomJson(entry.DefinitionId)), null),
            ItemCatalogCategory.Beast when !string.IsNullOrWhiteSpace(data.RewardMushroomId) => new(new StorageItemTemplate(2, entry.DefinitionId, entry.DisplayName,
                JsonSerializer.Serialize(new { gettime = 0, bstid = entry.DefinitionId, rwdemsrid = "", state = 0, lvl = 1, posonce = 0 }),
                MushroomJson(data.RewardMushroomId)), null),
            _ => new(null, "This definition is missing a validated baseline field required to create it.")
        };
    }

    public async Task<DecalCatalogLoadResult> LoadDecalsAsync(string databasePath, string? language = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var definitions = new List<DecalDefinition>();
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new DecalCatalogLoadResult(definitions, ["The masters database path does not exist; decal definitions are unavailable."]);

        try
        {
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
            var columns = await ColumnsAsync(connection, "master_skill", cancellationToken);
            if (!columns.Contains("id"))
            {
                warnings.Add("Table 'master_skill' is missing the identifier column; decal definitions could not be loaded.");
                return new DecalCatalogLoadResult(definitions, warnings);
            }

            var hasText = Has(await ColumnsAsync(connection, "master_text", cancellationToken), "sct", "id", "lang", "txt");
            if (!hasText) warnings.Add("Table 'master_text' is missing or incomplete; decal definition keys are shown instead of localized names.");
            var selected = new[] { "type", "premium", "rarity", "platform" }.Where(columns.Contains).ToArray();
            var requestedLanguage = string.IsNullOrWhiteSpace(language) ? "int" : language.Trim();
            var hasDesc = hasText && columns.Contains("desc");
            await using var command = Localized(connection, "master_skill", "id", "name", selected, requestedLanguage, hasText, hasDesc ? "desc" : null);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Text(reader, 0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var values = Values(reader, selected, 2);
                if (Int(values, "platform") is { } platform && platform != 0) continue;
                var rarity = Int(values, "rarity") is { } value && value > 0 ? (int?)value : null;
                definitions.Add(new DecalDefinition(
                    id,
                    Name(reader, 2 + selected.Length, Text(reader, 1), id),
                    Int(values, "premium") is { } premium && premium != 0,
                    rarity,
                    TypeLabel(values),
                    hasDesc ? Text(reader, 3 + selected.Length) : ""));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The decal catalog could not be read: {exception.Message}");
        }

        definitions.Sort((left, right) => string.Compare(left.SkillId, right.SkillId, StringComparison.OrdinalIgnoreCase));
        return new DecalCatalogLoadResult(definitions, warnings);
    }

    private static string TypeLabel(IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("type", out var raw) || raw is null) return "";
        var type = Convert.ToString(raw)?.Trim();
        if (string.IsNullOrWhiteSpace(type)) return "";
        const string prefix = "SKLTP_";
        return type.StartsWith(prefix, StringComparison.Ordinal) ? type[prefix.Length..] : type;
    }

    private static async Task LoadPartsAsync(SqliteConnection c, string language, bool text, List<ItemCatalogEntry> entries,
        Dictionary<(ItemCatalogCategory, string), TemplateData> data, List<string> warnings, CancellationToken token)
    {
        const string table = "master_part";
        var columns = await ColumnsAsync(c, table, token);
        if (!Has(columns, "id", "name")) { warnings.Add("Table 'master_part' is missing identifier or name columns; equipment definitions could not be loaded."); return; }
        var required = new[] { "type", "platform", "dur", "capacity", "spare" };
        var complete = Has(columns, required);
        if (!complete) warnings.Add("Table 'master_part' is incomplete; equipment definitions are visible but cannot be created safely.");
        var selected = required.Where(columns.Contains).ToArray();
        await using var command = Localized(c, table, "id", "name", selected, language, text);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = Text(reader, 0); if (string.IsNullOrWhiteSpace(id)) { warnings.Add("Table 'master_part' contains a definition without an ID; that row was skipped."); continue; }
            var values = Values(reader, selected, 2);
            var type = values.TryGetValue("type", out var rawType) ? Convert.ToString(rawType) : null;
            var platform = Int(values, "platform"); var durability = Int(values, "dur"); var capacity = Int(values, "capacity"); var spare = Int(values, "spare");
            var reason = !complete ? "The master_part schema lacks fields required for a safe equipment template."
                : !SupportedEquipmentType(type) ? $"Equipment type '{type ?? "(missing)"}' is unavailable or internal."
                : platform != 0 ? $"Platform {platform?.ToString() ?? "(missing)"} equipment is unavailable or internal."
                : durability is < 0 || capacity is < 0 || spare is < 0 ? "The master definition has invalid durability, capacity, or spare values." : null;
            entries.Add(new ItemCatalogEntry(id, Name(reader, 2 + selected.Length, Text(reader, 1), id), ItemCatalogCategory.Equipment, table, reason is null, reason));
            data[(ItemCatalogCategory.Equipment, id)] = new(durability, capacity, spare, null);
        }
    }

    private static async Task LoadItemsAsync(SqliteConnection c, string language, bool text, List<ItemCatalogEntry> entries,
        Dictionary<(ItemCatalogCategory, string), TemplateData> data, List<string> warnings, CancellationToken token)
    {
        const string table = "master_item"; var columns = await ColumnsAsync(c, table, token);
        if (!Has(columns, "itemid", "name")) { warnings.Add("Table 'master_item' is missing identifier or name columns; item definitions could not be loaded."); return; }
        var platformColumn = columns.Contains("platform");
        if (!platformColumn) warnings.Add("Table 'master_item' has no platform column; item definitions are visible but cannot be created safely.");
        await using var command = Localized(c, table, "itemid", "name", platformColumn ? ["platform"] : [], language, text);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = Text(reader, 0); if (string.IsNullOrWhiteSpace(id)) continue;
            var platform = platformColumn && !reader.IsDBNull(2) ? Convert.ToInt32(reader.GetValue(2)) : (int?)null;
            var supported = platform == 0;
            var reason = supported ? null : platformColumn ? $"Platform {platform?.ToString() ?? "(missing)"} item is unavailable or internal." : "The master_item schema lacks a platform field required for validation.";
            entries.Add(new ItemCatalogEntry(id, Name(reader, 2 + (platformColumn ? 1 : 0), Text(reader, 1), id), ItemCatalogCategory.Item, table, supported, reason));
            data[(ItemCatalogCategory.Item, id)] = new(null, null, null, null);
        }
    }

    private static async Task LoadMushroomsAsync(SqliteConnection c, string language, bool text, List<ItemCatalogEntry> entries,
        Dictionary<(ItemCatalogCategory, string), TemplateData> data, List<string> warnings, CancellationToken token)
    {
        const string table = "master_mushroom";
        if (!Has(await ColumnsAsync(c, table, token), "id", "c_name")) { warnings.Add("Table 'master_mushroom' is missing identifier or name columns; mushroom definitions could not be loaded."); return; }
        await using var command = Localized(c, table, "id", "c_name", [], language, text); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) { var id = Text(reader, 0); if (string.IsNullOrWhiteSpace(id)) continue; entries.Add(new(id, Name(reader, 2, Text(reader, 1), id), ItemCatalogCategory.Mushroom, table, true)); data[(ItemCatalogCategory.Mushroom, id)] = new(null, null, null, null); }
    }

    private static async Task LoadBeastsAsync(SqliteConnection c, string language, bool text, List<ItemCatalogEntry> entries,
        Dictionary<(ItemCatalogCategory, string), TemplateData> data, List<string> warnings, CancellationToken token)
    {
        const string table = "master_beast";
        if (!Has(await ColumnsAsync(c, table, token), "id", "name", "rwdmsrid")) { warnings.Add("Table 'master_beast' is missing identifier, name, or reward columns; beast definitions could not be loaded."); return; }
        var mushrooms = entries.Where(x => x.Category == ItemCatalogCategory.Mushroom).Select(x => x.DefinitionId).ToHashSet(StringComparer.Ordinal);
        await using var command = Localized(c, table, "id", "name", ["rwdmsrid"], language, text); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = Text(reader, 0); if (string.IsNullOrWhiteSpace(id)) continue; var reward = Text(reader, 2);
            var supported = !string.IsNullOrWhiteSpace(reward) && mushrooms.Contains(reward);
            var reason = supported ? null : string.IsNullOrWhiteSpace(reward) ? "The beast definition does not identify its reward mushroom." : $"The reward mushroom '{reward}' is not a supported mushroom definition.";
            entries.Add(new(id, Name(reader, 3, Text(reader, 1), id), ItemCatalogCategory.Beast, table, supported, reason)); data[(ItemCatalogCategory.Beast, id)] = new(null, null, null, reward);
        }
    }

    private static SqliteCommand Localized(SqliteConnection c, string table, string id, string name, IReadOnlyList<string> more, string language, bool hasText, string? localizeMore = null)
    {
        var command = c.CreateCommand(); var quotedName = Quote(name); var columns = string.Concat(more.Select(x => $", {Quote(x)}"));
        var extraDisplay = string.IsNullOrEmpty(localizeMore) ? string.Empty : ", " + LocalDisplay(Quote(localizeMore), hasText);
        command.CommandText = $"SELECT {Quote(id)}, {quotedName}{columns}, {LocalDisplay(quotedName, hasText)}{extraDisplay} FROM {Quote(table)} ORDER BY {Quote(id)} COLLATE NOCASE;";
        if (hasText) command.Parameters.AddWithValue("$language", language); return command;
    }

    private static string LocalDisplay(string quotedColumn, bool hasText) => hasText
        ? $"COALESCE((SELECT txt FROM master_text WHERE sct = substr({quotedColumn}, 1, instr({quotedColumn}, '.') - 1) AND id = substr({quotedColumn}, instr({quotedColumn}, '.') + 1) AND lang = $language LIMIT 1), (SELECT txt FROM master_text WHERE sct = substr({quotedColumn}, 1, instr({quotedColumn}, '.') - 1) AND id = substr({quotedColumn}, instr({quotedColumn}, '.') + 1) AND lang = 'int' LIMIT 1), {quotedColumn})"
        : quotedColumn;

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection c, string table, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.CommandText = "SELECT name FROM pragma_table_info($table);"; command.Parameters.AddWithValue("$table", table);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase); await using var reader = await command.ExecuteReaderAsync(token); while (await reader.ReadAsync(token)) columns.Add(Text(reader, 0)); return columns;
    }

    private static Dictionary<string, object?> Values(SqliteDataReader reader, IReadOnlyList<string> names, int offset) { var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); for (var i = 0; i < names.Count; i++) result[names[i]] = reader.IsDBNull(offset + i) ? null : reader.GetValue(offset + i); return result; }
    private static int? Int(IReadOnlyDictionary<string, object?> values, string key) => !values.TryGetValue(key, out var value) || value is null ? null : Convert.ToInt32(value);
    private static bool Has(IReadOnlySet<string> columns, params string[] names) => names.All(columns.Contains);
    private static bool SupportedEquipmentType(string? type) => type?.Trim().Split('_').LastOrDefault() is "ARM" or "HEAD" or "BODY" or "LEGS";
    private static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? "" : Convert.ToString(reader.GetValue(index)) ?? "";
    private static string Name(SqliteDataReader reader, int index, string key, string fallback) => string.IsNullOrWhiteSpace(Text(reader, index)) ? (string.IsNullOrWhiteSpace(key) ? fallback : key) : Text(reader, index);
    private static string MushroomJson(string id) => JsonSerializer.Serialize(new { gettime = 0, msrid = id, eefcid = "", tefcid = "", posonce = 0, state = 0 });
    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken token) { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5 }.ToString()); await c.OpenAsync(token); return c; }
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private sealed record TemplateData(int? Durability, int? Capacity, int? Spare, string? RewardMushroomId);
    private sealed class CatalogSnapshot(IReadOnlyList<ItemCatalogEntry> entries, Dictionary<(ItemCatalogCategory, string), TemplateData> data)
    {
        public static CatalogSnapshot Empty { get; } = new([], new()); public IReadOnlyDictionary<(ItemCatalogCategory, string), TemplateData> Data { get; } = data;
        private readonly HashSet<ItemCatalogEntry> _entries = new(entries, ReferenceEqualityComparer.Instance); public bool Contains(ItemCatalogEntry entry) => _entries.Contains(entry);
    }
}
