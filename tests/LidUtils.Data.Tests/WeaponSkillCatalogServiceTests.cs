using LidUtils.Data;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class WeaponSkillCatalogServiceTests
{
    [Fact]
    public async Task LoadWeaponSkills_ResolvesNamesCapsAndAbpCosts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateFixtureAsync(path);

        var result = await new ItemCatalogService().LoadWeaponSkillsAsync(path);

        Assert.Empty(result.Warnings);
        var machete = Assert.Single(result.Definitions);
        Assert.Equal("PTARMTP_01", machete.WeaponType);
        Assert.Equal("Machete", machete.DisplayName);
        Assert.Equal(20, machete.MaximumLevel);
        Assert.Equal(0, machete.RequiredAbpForLevel(1));
        Assert.Equal(5, machete.RequiredAbpForLevel(2));
        Assert.Equal(3800, machete.RequiredAbpForLevel(20));
        Assert.Null(machete.RequiredAbpForLevel(21));
        Assert.Same(machete, result.Find("ptarmtp_01"));
    }

    [Fact]
    public async Task LoadWeaponSkills_MissingRewardTable_ReturnsWarning()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await using (var connection = await OpenWritableAsync(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE master_ptarm_type (id TEXT, name TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new ItemCatalogService().LoadWeaponSkillsAsync(path);

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Warnings, warning => warning.Contains("master_expert_lvl_reward", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadWeaponSkills_MissingDatabasePath_ReturnsWarning()
    {
        var result = await new ItemCatalogService().LoadWeaponSkillsAsync("Z:\\does-not-exist\\masters.db");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    private static async Task CreateFixtureAsync(string path)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        var rewards = string.Join(",", Enumerable.Range(2, 19).Select(level => $"('PTARMTP_01', {level}, {(level == 2 ? 5 : level == 20 ? 3800 : level * 100)})"));
        command.CommandText = $"""
            CREATE TABLE master_text (sct TEXT, id TEXT, lang TEXT, txt TEXT);
            INSERT INTO master_text VALUES ('WEAPON_CTGRY', 'TXT_PT_ARM_WP001_001', 'int', 'Machete');
            CREATE TABLE master_ptarm_type (id TEXT, name TEXT);
            INSERT INTO master_ptarm_type VALUES
                ('PTARMTP_01', 'WEAPON_CTGRY.TXT_PT_ARM_WP001_001'),
                ('PTARMTP_09', 'WEAPON_CTGRY.TXT_PT_ARM_WP009_001');
            CREATE TABLE master_expert_lvl_reward (ptarmtp TEXT, lvl INTEGER, abp INTEGER);
            INSERT INTO master_expert_lvl_reward VALUES {rewards};
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenWritableAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
