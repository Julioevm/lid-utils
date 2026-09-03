using LidUtils.Core;
using LidUtils.Data;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class DatabaseValidatorTests
{
    private readonly DatabaseValidator _validator = new();

    [Fact]
    public async Task Validate_ReturnsNotFoundForMissingFile()
    {
        var result = await _validator.ValidateAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".db"));

        Assert.False(result.IsValid);
        Assert.Equal(DatabaseValidationError.NotFound, result.Error);
    }

    [Fact]
    public async Task Validate_RejectsNonSqliteFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await File.WriteAllTextAsync(path, "not a database");

        var result = await _validator.ValidateAsync(path);

        Assert.False(result.IsValid);
        Assert.Equal(DatabaseValidationError.InvalidHeader, result.Error);
    }

    [Fact]
    public async Task Validate_RejectsDatabaseWithoutRequiredTables()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateDatabaseAsync(path, includeAllRequiredTables: false);

        var result = await _validator.ValidateAsync(path);

        Assert.False(result.IsValid);
        Assert.Equal(DatabaseValidationError.UnsupportedSchema, result.Error);
    }

    [Fact]
    public async Task Validate_AcceptsCompatibleDatabaseAndBuildsFingerprints()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporaryDirectory.Path, "masters.db");
        await CreateDatabaseAsync(path, includeAllRequiredTables: true);
        var writeTimeBefore = File.GetLastWriteTimeUtc(path);

        var result = await _validator.ValidateAsync(path);

        Assert.True(result.IsValid, result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal(3, result.Metadata.TableCount);
        Assert.Equal(64, result.Metadata.DatabaseSha256.Length);
        Assert.Equal(64, result.Metadata.SchemaSha256.Length);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(path));
    }

    private static async Task CreateDatabaseAsync(string path, bool includeAllRequiredTables)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = includeAllRequiredTables
            ? """
                CREATE TABLE master_const_int (id TEXT PRIMARY KEY, value INTEGER NOT NULL);
                CREATE TABLE master_const_float (id TEXT PRIMARY KEY, value REAL NOT NULL);
                CREATE TABLE master_const_str (id TEXT PRIMARY KEY, value TEXT NOT NULL);
                """
            : "CREATE TABLE master_const_int (id TEXT PRIMARY KEY, value INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync();
    }
}

