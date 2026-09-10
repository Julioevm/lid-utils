using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

/// <summary>Reads fighter body caps from masters.db using a read-only SQLite connection.</summary>
public sealed class BodyStatCatalogService : IBodyStatCatalogService
{
    public async Task<BodyStatCatalogLoadResult> LoadBodyStatsAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var definitions = new List<BodyStatDefinition>();
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new([], ["The masters database path does not exist; fighter stat caps are unavailable."]);

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var columns = await ColumnsAsync(connection, "master_body_detail", cancellationToken);
            var required = new[] { "type", "grade", "limit_break", "param_lv_max" };
            if (!required.All(columns.Contains))
                return new([], ["Table 'master_body_detail' is missing type, grade, limit_break, or param_lv_max; fighter stat caps are unavailable."]);

            var optional = new[] { "bag_capacity", "skill_slots", "rage_capacity" }.Where(columns.Contains).ToArray();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"type\", \"grade\", \"limit_break\", \"param_lv_max\"" +
                string.Concat(optional.Select(column => $", \"{column}\"")) +
                " FROM \"master_body_detail\" ORDER BY \"type\", \"grade\", \"limit_break\";";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var seen = new HashSet<(string, int, int)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0))?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(type) || !TryInt(reader, 1, out var grade) ||
                    !TryInt(reader, 2, out var limitBreak) || !TryInt(reader, 3, out var cap))
                {
                    warnings.Add("master_body_detail contains an incomplete fighter cap row; that row was skipped.");
                    continue;
                }
                if (grade < 1 || limitBreak < 0 || cap < 1)
                {
                    warnings.Add($"master_body_detail contains invalid cap data for {type} grade {grade} limit break {limitBreak}; that row was skipped.");
                    continue;
                }
                if (!seen.Add((type, grade, limitBreak)))
                {
                    warnings.Add($"master_body_detail has a duplicate definition for {type} grade {grade} limit break {limitBreak}; the first row was used.");
                    continue;
                }
                int? Optional(string name)
                {
                    var offset = Array.IndexOf(optional, name);
                    return offset >= 0 && TryInt(reader, 4 + offset, out var value) ? value : null;
                }
                definitions.Add(new(type, grade, limitBreak, cap, Optional("bag_capacity"), Optional("skill_slots"), Optional("rage_capacity")));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Fighter stat definitions could not be read: {exception.Message}");
        }
        return new(definitions, warnings);
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (!reader.IsDBNull(1)) result.Add(reader.GetString(1));
        return result;
    }

    private static bool TryInt(SqliteDataReader reader, int ordinal, out int value)
    {
        value = 0;
        if (reader.IsDBNull(ordinal)) return false;
        try { value = Convert.ToInt32(reader.GetValue(ordinal)); return true; }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return false; }
    }
}
