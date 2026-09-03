using System.Security.Cryptography;
using System.Security;
using System.Text;
using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

public sealed class DatabaseValidator : IDatabaseValidator
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private static readonly IReadOnlyDictionary<string, string[]> RequiredSchema =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["master_const_int"] = ["id", "value"],
            ["master_const_float"] = ["id", "value"],
            ["master_const_str"] = ["id", "value"]
        };

    public async Task<DatabaseValidationResult> ValidateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.NotFound,
                "The selected masters.db file does not exist.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var initialFile = new FileInfo(fullPath);
            var initialLength = initialFile.Length;
            var initialWriteTime = initialFile.LastWriteTimeUtc;

            if (!await HasSqliteHeaderAsync(fullPath, cancellationToken))
            {
                return DatabaseValidationResult.Failure(
                    DatabaseValidationError.InvalidHeader,
                    "The selected file is not a SQLite 3 database.");
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var quickCheck = await ExecuteScalarStringAsync(connection, "PRAGMA quick_check;", cancellationToken);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return DatabaseValidationResult.Failure(
                    DatabaseValidationError.Corrupt,
                    $"SQLite integrity validation failed: {quickCheck}");
            }

            var schemaError = await FindSchemaErrorAsync(connection, cancellationToken);
            if (schemaError is not null)
            {
                return DatabaseValidationResult.Failure(DatabaseValidationError.UnsupportedSchema, schemaError);
            }

            var tableCount = await ExecuteScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table';",
                cancellationToken);
            var userVersion = await ExecuteScalarIntAsync(connection, "PRAGMA user_version;", cancellationToken);
            var applicationId = await ExecuteScalarIntAsync(connection, "PRAGMA application_id;", cancellationToken);
            var schemaSha256 = await ComputeSchemaFingerprintAsync(connection, cancellationToken);

            await connection.CloseAsync();

            var databaseSha256 = await ComputeFileSha256Async(fullPath, cancellationToken);

            initialFile.Refresh();
            if (initialFile.Length != initialLength || initialFile.LastWriteTimeUtc != initialWriteTime)
            {
                return DatabaseValidationResult.Failure(
                    DatabaseValidationError.ChangedDuringValidation,
                    "The database changed while it was being validated. Close the game or Steam updater and try again.");
            }

            return DatabaseValidationResult.Success(new DatabaseFileMetadata(
                fullPath,
                initialLength,
                initialWriteTime,
                databaseSha256,
                schemaSha256,
                tableCount,
                userVersion,
                applicationId));
        }
        catch (UnauthorizedAccessException)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.AccessDenied,
                "The application does not have permission to read the selected database.");
        }
        catch (IOException exception)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.Locked,
                $"The selected database could not be read: {exception.Message}");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.Locked,
                "The database is locked by another process. Close the game or Steam updater and try again.");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.Corrupt,
                $"SQLite rejected the database: {exception.Message}");
        }
        catch (SqliteException exception)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.Unexpected,
                $"SQLite could not validate the database: {exception.Message}");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or SecurityException)
        {
            return DatabaseValidationResult.Failure(
                DatabaseValidationError.Unexpected,
                $"The database path could not be validated: {exception.Message}");
        }
    }

    private static async Task<bool> HasSqliteHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[SqliteHeader.Length];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        return bytesRead == SqliteHeader.Length && buffer.AsSpan().SequenceEqual(SqliteHeader);
    }

    private static async Task<string?> FindSchemaErrorAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var (table, requiredColumns) in RequiredSchema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table);";
            command.Parameters.AddWithValue("$table", table);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }

            if (columns.Count == 0)
            {
                return $"Required table '{table}' is missing. This database version is not supported.";
            }

            var missingColumns = requiredColumns.Where(column => !columns.Contains(column)).ToArray();
            if (missingColumns.Length > 0)
            {
                return $"Table '{table}' is missing required column(s): {string.Join(", ", missingColumns)}.";
            }
        }

        return null;
    }

    private static async Task<string> ComputeSchemaFingerprintAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_schema
            ORDER BY type, name, tbl_name;
            """;

        var schema = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schema
                .Append(reader.GetString(0)).Append('|')
                .Append(reader.GetString(1)).Append('|')
                .Append(reader.GetString(2)).Append('|')
                .Append(reader.GetString(3)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToString())));
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }

    private static async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
