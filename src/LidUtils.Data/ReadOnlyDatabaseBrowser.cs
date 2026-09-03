using System.Globalization;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

public sealed class ReadOnlyDatabaseBrowser : IReadOnlyDatabaseBrowser
{
    private static readonly (string Table, SettingValueType Type)[] SettingTables =
    [
        ("master_const_int", SettingValueType.Integer),
        ("master_const_float", SettingValueType.Float),
        ("master_const_str", SettingValueType.String)
    ];

    public async Task<SettingsLoadResult> LoadSettingsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        var tables = await GetObjectNamesAsync(connection, "table", cancellationToken);
        var entries = new List<SettingEntry>();
        var warnings = new List<string>();

        foreach (var (table, type) in SettingTables)
        {
            if (!tables.Contains(table))
            {
                warnings.Add($"Table '{table}' is missing; its settings were skipped.");
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, CAST(value AS TEXT), value IS NULL FROM {QuoteIdentifier(table)} ORDER BY id COLLATE NOCASE;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.IsDBNull(0)
                    ? "(NULL key)"
                    : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                var isNull = reader.GetInt64(2) != 0;
                var value = isNull ? "(NULL)" : reader.GetString(1);
                entries.Add(new SettingEntry(key, value, type, table, isNull));
            }
        }

        return new SettingsLoadResult(entries, warnings);
    }

    public async Task<IReadOnlyList<SchemaTable>> LoadSchemaAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        var objects = new List<(string Type, string Name, string Sql)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, COALESCE(sql, '') FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name COLLATE NOCASE;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var result = new List<SchemaTable>(objects.Count);
        foreach (var item in objects)
        {
            var columns = await LoadColumnsAsync(connection, item.Name, cancellationToken);
            long? rowCount = null;
            if (item.Type == "table")
            {
                await using var countCommand = connection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(item.Name)};";
                rowCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            result.Add(new SchemaTable(item.Name, item.Type, rowCount, columns, item.Sql));
        }

        return result;
    }

    public async Task<TablePreview> LoadTablePreviewAsync(
        string path,
        string tableName,
        int maximumRows = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumRows is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows), "Preview size must be between 1 and 1,000 rows.");
        }

        await using var connection = await OpenReadOnlyAsync(path, cancellationToken);
        var tables = await GetObjectNamesAsync(connection, "table", cancellationToken);
        var views = await GetObjectNamesAsync(connection, "view", cancellationToken);
        if (!tables.Contains(tableName) && !views.Contains(tableName))
        {
            throw new ArgumentException("The selected table or view does not exist.", nameof(tableName));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", maximumRows + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<TablePreviewRow>();
        while (rows.Count <= maximumRows && await reader.ReadAsync(cancellationToken))
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = FormatCell(reader, index);
            }

            rows.Add(new TablePreviewRow(string.Join("  |  ", values)));
        }

        var isTruncated = rows.Count > maximumRows;
        if (isTruncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new TablePreview(tableName, columnNames, rows, isTruncated);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<HashSet<string>> GetObjectNamesAsync(
        SqliteConnection connection,
        string type,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<SchemaColumn>> LoadColumnsAsync(
        SqliteConnection connection,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cid, name, type, \"notnull\", pk FROM pragma_table_info($name) ORDER BY cid;";
        command.Parameters.AddWithValue("$name", objectName);
        var columns = new List<SchemaColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new SchemaColumn(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 0,
                reader.GetInt32(4) > 0));
        }

        return columns;
    }

    private static string FormatCell(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return "NULL";
        }

        var value = reader.GetValue(index);
        if (value is byte[] bytes)
        {
            return $"<BLOB {bytes.Length:N0} bytes>";
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Length <= 160 ? text : text[..157] + "…";
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
