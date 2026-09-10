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

        // The term calendar starts a term at each boundary: now = 1.5M is inside the term introduced
        // at 1M (template A), which runs until the next boundary at 2M.
        Assert.Equal("A", result.ActiveTemplateId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_000_000), result.ActiveTermStartUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000), result.ActiveTermExpiresUtc);
        Assert.Equal(new[] { "B", "C" }, result.UpcomingTerms.Select(term => term.TemplateId));
        Assert.Equal(new[] { "A" }, result.RecentTerms.Select(term => term.TemplateId));
    }

    [Theory]
    [InlineData(0, "A")]              // before the first boundary: fall back to the first term
    [InlineData(999_999, "A")]
    [InlineData(1_000_000, "A")]      // exactly on a boundary: the new term wins
    [InlineData(1_999_999, "A")]
    [InlineData(2_000_000, "B")]
    [InlineData(2_999_999, "B")]
    [InlineData(3_000_000, "C")]
    [InlineData(3_500_000, "C")]      // after the last boundary: keep the last term
    public async Task Load_ResolvesTheTermThatMostRecentlyStarted(long nowUnixSeconds, string expectedTemplate)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateMapFixtureAsync(path);

        var result = await new MapDataService().LoadAsync(path, nowUtc: DateTimeOffset.FromUnixTimeSeconds(nowUnixSeconds));

        Assert.Equal(expectedTemplate, result.ActiveTemplateId);
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

        // Boss overlay facts confirmed on the installed build (see docs/map_boss_plan.md).
        var current = result.Templates.Single(candidate => candidate.Id == "4HMA");
        var forceMan = current.Nodes.Single(node => node.AreaId == "MET_AREA_V440");
        Assert.True(forceMan.IsForceManRoom);
        Assert.Equal(440, forceMan.ForceManGate!.Fee);
        Assert.Equal("NORMAL", forceMan.ForceManGate.DifficultyLabel);
        Assert.Contains(current.Nodes, node => node.StageId == "S_MET" && node.HasMainBoss);
        var bandBoss = current.Nodes.Single(node => node.AreaId == "MET_AREA_101");
        Assert.True(bandBoss.IsBossArena);
        Assert.Equal("STAGE_BOSS1", bandBoss.ArenaBoss!.Id);
        Assert.Equal("Max Sharp", bandBoss.ArenaBoss.DisplayName);
        Assert.All(current.Nodes.Where(node => node.HasMainBoss),
            node => Assert.All(node.MainBossTypes, type => Assert.Contains(type.DisplayName, TowerBosses.Names)));

        // The paid rooms are mounted only by the 4HMA rotation, so other rotations must not expose them.
        var other = result.Templates.Single(candidate => candidate.Id == "A");
        Assert.DoesNotContain(other.Nodes, node => node.IsForceManRoom && node.StageId == "S_MET");
    }

    [Fact]
    public async Task Load_EnrichesNodesWithBossInfo()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "masters.db");
        await CreateMapFixtureAsync(path);

        var result = await new MapDataService().LoadAsync(path);
        var template = result.Templates.Single(candidate => candidate.Id == "4HMA");

        // Mini-boss presence comes from master_floor.mbs*; the variant is inferred from the unit token.
        var inferred = template.Nodes.Single(node => node.AreaId == "MET_AREA_010");
        Assert.True(inferred.HasMainBoss);
        Assert.Equal(1, inferred.MainBossMin);
        Assert.Equal(1, inferred.MainBossMax);
        Assert.Equal(new[] { "MBOSS1" }, inferred.MainBossTypes.Select(type => type.Id).ToArray());
        Assert.Equal("Max Sharp", inferred.MainBossTypes[0].DisplayName);

        // The drop generator's explicit variant wins over the unit-token inference (MBOSS2 -> MBOSS1).
        var bossRoom = template.Nodes.Single(node => node.AreaId == "MET_AREA_020");
        Assert.True(bossRoom.IsBossArena);
        Assert.Equal("STAGE_BOSS1", bossRoom.ArenaBoss!.Id);
        Assert.Equal(new[] { "MBOSS1" }, bossRoom.MainBossTypes.Select(type => type.Id).ToArray());

        var forceMan = template.Nodes.Single(node => node.AreaId == "MET_AREA_022");
        Assert.False(forceMan.HasMainBoss);
        Assert.True(forceMan.IsForceManRoom);
        Assert.NotNull(forceMan.ForceManGate);
        Assert.Equal("GATE_FFM_WS_01", forceMan.ForceManGate!.GateId);
        Assert.Equal(440, forceMan.ForceManGate.Fee);
        Assert.Equal("NORMAL", forceMan.ForceManGate.DifficultyLabel);

        // A node without any boss data keeps the defaults.
        var head = template.Nodes.Single(node => node.IsHead);
        Assert.False(head.HasBoss);
    }

    private static async Task CreateMapFixtureAsync(string path)
    {
        await using var connection = await OpenWritableAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE master_area_connect_node (id TEXT, stgid TEXT, flrid TEXT, areaid TEXT, elvflrid TEXT, isdef INTEGER, ofsx REAL);
            CREATE TABLE master_area_connect_escalator (id TEXT, dir INTEGER, flrid TEXT, areaid TEXT, toflr TEXT, toarea TEXT, ci INTEGER, key TEXT, gate TEXT);
            CREATE TABLE master_area_template_term (expires INTEGER, tmplid TEXT);
            CREATE TABLE master_floor (id TEXT, areaid TEXT, no INTEGER, name TEXT, stgid TEXT, mbsmin INTEGER, mbsmax INTEGER);
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
                ('MET_FLR_01', 'MET_AREA_010', 1, 'AREA_NAME.TXT_MET_0001', 'S_MET', 1, 1),
                ('MET_FLR_02', 'MET_AREA_020', 2, 'AREA_NAME.TXT_MET_0002', 'S_MET', 1, 1),
                ('MET_FLR_02', 'MET_AREA_022', 2, 'AREA_NAME.TXT_MET_0003', 'S_MET', 0, 0);

            INSERT INTO master_elevator VALUES ('ELV_MAIN', 'MAIN ELEVATOR');
            INSERT INTO master_elevator_stop_floor VALUES ('ELV_MAIN_MET_FLR_01', 'ELV_MAIN');

            INSERT INTO master_text VALUES
                ('AREA_NAME', 'TXT_MET_0001', 'int', 'IMA OKA'),
                ('AREA_NAME', 'TXT_MET_0002', 'int', 'WANOKI'),
                ('AREA_NAME', 'TXT_MET_0003', 'int', 'KITA'),
                ('4FORCEMEN', 'TXT_NORMAL', 'int', 'NORMAL');

            -- Boss overlay tables (see docs/map_boss_plan.md).
            CREATE TABLE master_area_setting_unit (stgid TEXT, areaid TEXT, unit TEXT, kis TEXT);
            CREATE TABLE master_stage_mboss (pntid TEXT, stgid TEXT, unit TEXT, attp TEXT, type TEXT, freq INTEGER, path TEXT, itemid TEXT, itemgenid TEXT);
            CREATE TABLE master_mboss (id TEXT, name TEXT);
            CREATE TABLE master_stage_gate (pntid TEXT, flrid TEXT, stgid TEXT, unit TEXT, gateid TEXT, freq INTEGER);
            CREATE TABLE master_gate (id TEXT, type TEXT, val0 TEXT, val1 TEXT);
            CREATE TABLE master_floor_drop_gen (flrid TEXT, areaid TEXT, refareaid TEXT, type TEXT, grp INTEGER, freq INTEGER);

            INSERT INTO master_area_setting_unit VALUES
                ('S_MET', 'MET_AREA_010', 'METRO_A14_BR', ''),
                ('S_MET', 'MET_AREA_020', 'METRO_A17_BR', ''),
                ('S_MET', 'MET_AREA_020', 'METRO_BOSS', ''),
                ('S_MET', 'MET_AREA_022', 'METRO_SMALLBOSS', '');

            INSERT INTO master_stage_mboss VALUES
                ('MET_MBS_TGT_00', 'S_MET', 'A14_BR', 'MBSATTP_NORMAL', 'MBOSS1', 0,  '', '', ''),
                ('MET_MBS_TGT_00', 'S_MET', 'A17_BR', 'MBSATTP_NORMAL', 'MBOSS2', -1, '', '', ''),
                ('MET_MBS_TGT_00', 'S_MET', 'BOSS',   'MBSATTP_NORMAL', 'STAGE_BOSS1', -1, '', '', '');

            INSERT INTO master_mboss VALUES
                ('MBOSS1', 'hearing'),
                ('MBOSS2', 'sight'),
                ('STAGE_BOSS1', 'boss-hearing');

            INSERT INTO master_stage_gate VALUES ('MET_RC_TGT_00', 'MET_FLR_02', 'S_MET', 'SMALLBOSS', 'GATE_FFM_WS_01', 100);
            INSERT INTO master_gate VALUES ('GATE_FFM_WS_01', 'PAYMONEY', '440', '4FORCEMEN.TXT_NORMAL');
            INSERT INTO master_floor_drop_gen VALUES ('MET_FLR_02', 'MET_AREA_020', '-', 'PTGENTP_MBOSS1', 1, 6);
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
