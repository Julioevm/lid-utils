using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data.Tests;

public sealed class MapDataServiceTests
{
    [Fact]
    public async Task Load_ReadsNodesEdgesNamesElevatorsAndActiveTermFromFixture()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateMapFixtureAsync(path);
        var service = new MapDataService();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_500_000);

        var result = await service.LoadAsync(path, nowUtc: now);

        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Templates.Count);

        var current = result.Templates.Single(template => template.Id == "4HMA");
        Assert.Equal(4, current.Nodes.Count); // HEAD + three areas
        Assert.Equal(3, current.Edges.Count); // HEAD climb + two F1 -> F2 routes (duplicate deduped)

        var head = current.Nodes.Single(node => node.IsHead);
        Assert.Equal(0, head.FloorNumber);
        Assert.Empty(head.AreaName);
        // The waiting room is the hub of the main elevator even though it has no floor-stop row.
        Assert.Equal(TowerMapCatalog.MainElevatorCarId, head.ElevatorCarId);
        Assert.Equal(TowerMapCatalog.WaitingRoomStopId, head.ElevatorStopId);
        Assert.Equal("MAIN ELEVATOR", head.ElevatorCarLabel);

        var area = current.Nodes.Single(node => node.AreaId == "MET_AREA_010");
        Assert.Equal(1, area.FloorNumber);
        Assert.True(area.IsBase);
        Assert.Equal("IMA OKA", area.AreaName);
        Assert.Equal("ELV_MAIN_MET_FLR_01", area.ElevatorStopId);
        Assert.Equal(TowerMapCatalog.MainElevatorCarId, area.ElevatorCarId);
        Assert.Equal("MAIN ELEVATOR", area.ElevatorCarLabel);

        var secondFloor = current.Nodes.Where(node => node.FloorNumber == 2).OrderBy(node => node.AreaId).ToArray();
        Assert.Equal(2, secondFloor.Length);
        Assert.All(secondFloor, node => Assert.Equal(string.Empty, node.ElevatorCarId));

        // dir=1 and dangling-target rows are dropped, the duplicated pair is deduped.
        Assert.DoesNotContain(current.Edges, edge => edge.TargetKey == "MET_FLR_09/MET_AREA_090");
        Assert.DoesNotContain(current.Edges, edge => edge.Ci == -1);
        Assert.Contains(current.Edges, edge => edge.Key2 == "MET_FLR_01/MET_AREA_010" && edge.TargetKey == "MET_FLR_02/MET_AREA_020");
        Assert.Contains(current.Edges, edge => edge.TargetKey == "MET_FLR_02/MET_AREA_022" && edge.IsGated);

        var other = result.Templates.Single(template => template.Id == "A");
        Assert.Equal(2, other.Edges.Count); // rotation A does not mount the side route

        // The term calendar: now = 1.5M, term 2M (template B) is live and started at 1M.
        Assert.Equal("B", result.ActiveTemplateId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_000_000), result.ActiveTermStartUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000), result.ActiveTermExpiresUtc);
    }

    [Fact]
    public async Task Load_MissingMapTablesReturnsWarningAndNoTemplates()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await using (var connection = await OpenWritableAsync(path))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE unrelated (id INTEGER);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new MapDataService().LoadAsync(path);

        Assert.Empty(result.Templates);
        Assert.Contains(result.Warnings, warning => warning.Contains("tower map tables", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task ExplicitInstalledDatabase_LoadsFiveCompleteRotationsReadOnly()
    {
        var path = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_DB");
        if (string.IsNullOrWhiteSpace(path)) return;
        var before = File.GetLastWriteTimeUtc(path);
        var service = new MapDataService();

        var result = await service.LoadAsync(path);

        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Templates.Count);
        foreach (var template in result.Templates)
        {
            Assert.True(template.Nodes.Count >= 130, $"{template.Id} node count was {template.Nodes.Count}");
            Assert.True(template.Edges.Count >= 150, $"{template.Id} edge count was {template.Edges.Count}");
            Assert.Contains(template.Nodes, node => node.IsHead && node.FloorNumber == 0);
            Assert.Contains(template.Nodes, node => !string.IsNullOrWhiteSpace(node.AreaName));
            Assert.Contains(template.Nodes, node => node.ElevatorCarId == TowerMapCatalog.MainElevatorCarId && node.ElevatorCarLabel == "MAIN ELEVATOR");
        }

        Assert.Contains(result.ActiveTemplateId, TowerMapCatalog.TemplateIds);
        Assert.NotEmpty(result.UpcomingTerms);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    private static async Task CreateMapFixtureAsync(string path)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE master_area_connect_node (id TEXT, stgid TEXT, flrid TEXT, areaid TEXT, elvflrid TEXT, isdef INTEGER, ofsx REAL);
            CREATE TABLE master_area_connect_escalator (id TEXT, dir INTEGER, flrid TEXT, areaid TEXT, toflr TEXT, toarea TEXT, ci INTEGER, key TEXT, gate TEXT);
            CREATE TABLE master_area_template_term (expires INTEGER, tmplid TEXT);
            CREATE TABLE master_floor (id TEXT, areaid TEXT, no INTEGER, name TEXT, stgid TEXT);
            CREATE TABLE master_elevator (id TEXT, name TEXT);
            CREATE TABLE master_elevator_stop_floor (id TEXT, elvid TEXT);
            CREATE TABLE master_text (sct TEXT, id TEXT, lang TEXT, txt TEXT);

            INSERT INTO master_area_connect_node VALUES
                ('4HMA', 'S_MET', 'HEAD',        '',             '',                0, 0.0),
                ('4HMA', 'S_MET', 'MET_FLR_01', 'MET_AREA_010', 'ELV_MAIN_MET_FLR_01', 1, 0.0),
                ('4HMA', 'S_MET', 'MET_FLR_02', 'MET_AREA_020', '',                1, 0.0),
                ('4HMA', 'S_MET', 'MET_FLR_02', 'MET_AREA_022', '',                1, 480.0),
                ('A',    'S_MET', 'HEAD',        '',             '',                0, 0.0),
                ('A',    'S_MET', 'MET_FLR_01', 'MET_AREA_010', 'ELV_MAIN_MET_FLR_01', 1, 0.0),
                ('A',    'S_MET', 'MET_FLR_02', 'MET_AREA_020', '',                1, 0.0);

            INSERT INTO master_area_connect_escalator VALUES
                -- 4HMA: HEAD -> F1, F1 -> F2 both ways, plus a duplicate pair, a downward row, and a dangling target.
                ('4HMA', 0, 'HEAD', '', 'MET_FLR_01', 'MET_AREA_010', 0, '', ''),
                ('4HMA', 0, 'MET_FLR_01', 'MET_AREA_010', 'MET_FLR_02', 'MET_AREA_020', 0, '', ''),
                ('4HMA', 0, 'MET_FLR_01', 'MET_AREA_010', 'MET_FLR_02', 'MET_AREA_020', 0, '', ''),   -- duplicate
                ('4HMA', 0, 'MET_FLR_01', 'MET_AREA_010', 'MET_FLR_02', 'MET_AREA_022', 1, 'KGF_1', 'KGF_2'),
                ('4HMA', 1, 'MET_FLR_02', 'MET_AREA_020', 'MET_FLR_01', 'MET_AREA_010', 0, '', ''),
                ('4HMA', 0, 'MET_FLR_02', 'MET_AREA_020', 'MET_FLR_09', 'MET_AREA_090', 0, '', ''),   -- dangling target
                ('4HMA', 0, 'MET_FLR_02', 'MET_AREA_020', '', '', -1, '', ''),                          -- dead end
                ('A', 0, 'HEAD', '', 'MET_FLR_01', 'MET_AREA_010', 0, '', ''),
                ('A', 0, 'MET_FLR_01', 'MET_AREA_010', 'MET_FLR_02', 'MET_AREA_020', 0, '', '');

            INSERT INTO master_area_template_term VALUES
                (1000000, 'A'),
                (2000000, 'B'),
                (3000000, 'C');

            INSERT INTO master_floor VALUES
                ('MET_FLR_01', 'MET_AREA_010', 1, 'AREA_NAME.TXT_MET_0001', 'S_MET'),
                ('MET_FLR_02', 'MET_AREA_020', 2, 'AREA_NAME.TXT_MET_0002', 'S_MET'),
                ('MET_FLR_02', 'MET_AREA_022', 2, 'AREA_NAME.TXT_MET_0003', 'S_MET');

            INSERT INTO master_elevator VALUES ('ELV_MAIN', 'MAIN ELEVATOR');
            INSERT INTO master_elevator_stop_floor VALUES ('ELV_MAIN_MET_FLR_01', 'ELV_MAIN');

            INSERT INTO master_text VALUES
                ('AREA_NAME', 'TXT_MET_0001', 'int', 'IMA OKA'),
                ('AREA_NAME', 'TXT_MET_0002', 'int', 'WANOKI'),
                ('AREA_NAME', 'TXT_MET_0003', 'int', 'KITA');
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
