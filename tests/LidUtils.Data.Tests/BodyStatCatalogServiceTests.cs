using LidUtils.Data;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class BodyStatCatalogServiceTests
{
    [Fact]
    public async Task LoadBodyStats_ReadsExactCapRowsAndOptionalCapacityFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE master_body_detail (type TEXT, grade INTEGER, limit_break INTEGER, param_lv_max INTEGER, bag_capacity INTEGER, skill_slots INTEGER, rage_capacity INTEGER); INSERT INTO master_body_detail VALUES ('BAL', 6, 4, 45, 70, 8, 3);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new BodyStatCatalogService().LoadBodyStatsAsync(path);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(45, definition.ParameterLevelMaximum);
        Assert.Equal(70, definition.BagCapacity);
        Assert.Equal(8, definition.SkillSlots);
        Assert.Same(definition, result.Find("bal", 6, 4));
        Assert.Null(result.Find("BAL", 6, 3));
    }

    [Fact]
    public async Task LoadBodyStats_PartialSchemaReportsUnavailableDefinitions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE master_body_detail (type TEXT, grade INTEGER);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new BodyStatCatalogService().LoadBodyStatsAsync(path);

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Warnings, warning => warning.Contains("param_lv_max", StringComparison.Ordinal));
    }
}
