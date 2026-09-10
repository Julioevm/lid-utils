using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class MapViewModelTests
{
    [Fact]
    public void SetResult_LaysOutTheWholeTowerWithHeadBelowFloorOne()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());

        Assert.True(viewModel.HasData);
        Assert.Equal("4HMA", viewModel.SelectedTemplate);
        Assert.Equal(4, viewModel.Nodes.Count); // HEAD + 3 areas
        Assert.Equal(3, viewModel.Edges.Count);

        var floorTwo = viewModel.Nodes.Single(node => node.Node.FloorNumber == 2 && node.Node.AreaId == "MET_AREA_020");
        var floorOne = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_010");
        var head = viewModel.Nodes.Single(node => node.IsHead);
        Assert.True(floorOne.Y > floorTwo.Y, "Higher floors must sit above lower floors on screen.");
        Assert.True(head.Y > floorOne.Y, "The tower head must sit below floor 1.");

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal(["F1", "F2"], viewModel.Rows.Select(row => row.Label).ToArray());
        Assert.Equal(3, viewModel.AreaRows.Count);
        Assert.True(viewModel.ContentWidth >= MapViewModel.GutterWidth + 300);
        Assert.True(viewModel.ContentHeight >= 200);
    }

    [Fact]
    public void BandFilter_RestrictsNodesEdgesAndRows()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());

        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_MET");
        Assert.Equal(4, viewModel.Nodes.Count); // Metro includes the head
        Assert.Equal(3, viewModel.Edges.Count);
        Assert.True(viewModel.AreaRows.All(row => row.Node.FloorNumber is 1 or 2));

        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_AMS");
        Assert.Empty(viewModel.Nodes);
        Assert.Empty(viewModel.AreaRows);
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.Edges);
    }

    [Fact]
    public void SameFloorDots_NeverOverlapAfterSettling()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());
        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_MET");

        var floorTwo = viewModel.Nodes
            .Where(node => node.Node.FloorNumber == 2)
            .OrderBy(node => node.X)
            .ToArray();
        Assert.Equal(2, floorTwo.Length);
        Assert.True(floorTwo[1].X - floorTwo[0].X >= MapViewModel.MinimumSameRowGap - 0.001);
    }

    [Fact]
    public void ElevatorRoutes_JoinEveryConsecutiveStopOfTheSameCarInItsColour()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());

        // The waiting room (car ELV_MAIN, floor 0) and the F1 area share the main elevator.
        var segment = Assert.Single(viewModel.ElevatorSegments);
        Assert.Equal(TowerMapCatalog.MainElevatorCarId, segment.CarId);
        Assert.Equal(0, segment.ColorIndex);
        Assert.Contains("Waiting room → F1 · IMA OKA", segment.ToolTipText, StringComparison.Ordinal);

        var mainStop = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_010");
        var unserviced = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_020");
        Assert.Equal(0, mainStop.ElevatorColorIndex);
        Assert.Equal(-1, unserviced.ElevatorColorIndex);

        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_AMS");
        Assert.Empty(viewModel.ElevatorSegments);
    }

    [Fact]
    public void SwitchingTemplateAndLabels_RebuildsLayoutAndClearsSelection()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());
        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_MET");

        var versionAfterLoad = viewModel.GeometryVersion;
        viewModel.ShowAreaLabels = true;
        Assert.True(viewModel.GeometryVersion > versionAfterLoad);

        // On-map labels carry the localised area name, not the area-id code.
        var mapNode = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_010");
        Assert.True(mapNode.ShowLabel);
        Assert.Equal("IMA OKA", mapNode.LabelText);

        var area = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_010");
        viewModel.SelectArea(area);
        Assert.True(viewModel.HasSelection);
        Assert.Equal("IMA OKA · Floor 1", viewModel.SelectedDetailTitle);
        Assert.Contains("Routes up", viewModel.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("↑", viewModel.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("key KGF_1", viewModel.SelectedDetails, StringComparison.Ordinal);

        viewModel.SelectedTemplate = "A";
        Assert.False(viewModel.HasSelection);
        Assert.Equal(2, viewModel.Edges.Count); // rotation A drops the gated side route
    }

    [Fact]
    public void ShowAreaLabels_DisplaysEveryAreaNameEvenOnCrowdedRows()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture());
        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_MET");
        viewModel.ShowAreaLabels = true;

        // Every visible area carries its name when the toggle is on; none is hidden
        // just because another dot sits close on the same floor.
        Assert.All(viewModel.Nodes.Where(node => !node.IsHead),
            node => Assert.True(node.ShowLabel, $"{node.Node.AreaId} label hidden"));
        Assert.Contains(viewModel.Nodes, node => node.LabelText == "WANOKI");
        Assert.Contains(viewModel.Nodes, node => node.LabelText == "KITA");

        // ... but the pair still gets pushed far enough apart for the left name to clear
        // the right dot (labels never overlap a neighbour chip).
        var floorTwo = viewModel.Nodes
            .Where(node => node.Node.FloorNumber == 2)
            .OrderBy(node => node.X)
            .ToArray();
        Assert.True(floorTwo[1].X - floorTwo[0].X >= 60d);
    }

    [Fact]
    public void SelectTodayTemplate_JumpsToTheLiveRotation()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture(activeTemplate: "B"));
        Assert.Equal("B", viewModel.SelectedTemplate);

        viewModel.SelectedTemplate = "A";
        viewModel.SelectTodayTemplate();
        Assert.Equal("B", viewModel.SelectedTemplate);
    }

    [Fact]
    public void BossInfo_SurfacesOnNodesRoutesListAndDetails()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(BossFixture());
        viewModel.SelectedBand = MapViewModel.BandOptions.Single(option => option.Key == "S_MET");

        // Roaming section-boss spawns still load, but are deliberately not badged or listed.
        var roaming = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_020");
        Assert.True(roaming.Node.HasMainBoss);
        Assert.Equal(string.Empty, roaming.BossBadge);
        Assert.DoesNotContain("Boss spawn", roaming.ToolTipText, StringComparison.Ordinal);
        var roamingRow = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_020");
        Assert.Equal(string.Empty, roamingRow.BossLabel);
        Assert.Equal(string.Empty, roamingRow.BossSummary);

        var arena = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_022");
        Assert.Equal("BOSS", arena.BossBadge);
        Assert.Contains("Boss arena: Max Sharp", arena.ToolTipText, StringComparison.Ordinal);

        var forceMan = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_023");
        Assert.Equal("FFM", forceMan.BossBadge);
        Assert.Contains("Four Force Men room · 440 KC (NORMAL)", forceMan.ToolTipText, StringComparison.Ordinal);

        var plain = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_010");
        Assert.Equal(string.Empty, plain.BossBadge);
        Assert.False(plain.HasBoss);

        var miniRoute = viewModel.Edges.Single(edge => edge.Edge.Key == "KGF_MET_MIDBOSS00_CLEAR");
        Assert.True(miniRoute.IsBossRoute);
        Assert.Equal(MapBossRouteKind.BossClear, miniRoute.BossRoute.Kind);
        Assert.Contains("Requires Max Sharp cleared", miniRoute.ToolTipText, StringComparison.Ordinal);

        var triggerRoute = viewModel.Edges.Single(edge => edge.Edge.Gate == "KGF_MET_MB00_BTN_AREA_010_GOAL");
        Assert.Equal(MapBossRouteKind.BossTrigger, triggerRoute.BossRoute.Kind);
        Assert.Equal("Boss trigger: Max Sharp", triggerRoute.BossRoute.Label);

        var areaRow = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_023");
        Assert.Equal("FFM", areaRow.BossLabel);
        Assert.Contains("Four Force Men room", areaRow.BossSummary, StringComparison.Ordinal);
        viewModel.SelectArea(areaRow);
        Assert.Contains("Bosses:", viewModel.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("★ Four Force Men room", viewModel.SelectedDetails, StringComparison.Ordinal);

        var plainRow = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_010");
        viewModel.SelectArea(plainRow);
        Assert.Contains("Bosses: none for this rotation", viewModel.SelectedDetails, StringComparison.Ordinal);

        // Toggling the overlay only affects drawing; the loaded data stays available.
        viewModel.ShowBossInfo = false;
        Assert.False(viewModel.ShowBossInfo);
        Assert.True(roaming.HasBoss);
    }

    [Fact]
    public void Zoom_StepsClampsAndResetsAroundTheNaturalScale()
    {
        var viewModel = new MapViewModel();
        Assert.Equal(1d, viewModel.Zoom);
        Assert.Equal("100%", viewModel.ZoomPercentText);
        Assert.True(viewModel.CanZoomIn);
        Assert.True(viewModel.CanZoomOut);

        viewModel.ZoomIn();
        Assert.Equal(1d + MapViewModel.ZoomStep, viewModel.Zoom, 6);
        Assert.Equal("125%", viewModel.ZoomPercentText);
        Assert.True(viewModel.CanZoomOut);

        viewModel.ZoomOut();
        viewModel.ZoomOut();
        Assert.Equal(1d - MapViewModel.ZoomStep, viewModel.Zoom, 6);
        Assert.Equal("75%", viewModel.ZoomPercentText);

        // Out-of-range requests clamp and report the stop so the view can disable its button.
        viewModel.Zoom = MapViewModel.MaximumZoom + 5d;
        Assert.Equal(MapViewModel.MaximumZoom, viewModel.Zoom);
        Assert.Equal("300%", viewModel.ZoomPercentText);
        Assert.False(viewModel.CanZoomIn);

        viewModel.Zoom = MapViewModel.MinimumZoom - 5d;
        Assert.Equal(MapViewModel.MinimumZoom, viewModel.Zoom);
        Assert.Equal("50%", viewModel.ZoomPercentText);
        Assert.False(viewModel.CanZoomOut);

        // Reset is available from any zoom level and is idempotent.
        viewModel.ResetZoom();
        Assert.Equal(1d, viewModel.Zoom);
        Assert.Equal("100%", viewModel.ZoomPercentText);
        Assert.True(viewModel.CanZoomIn);
        Assert.True(viewModel.CanZoomOut);
        viewModel.ResetZoom();
        Assert.Equal(1d, viewModel.Zoom);
    }

    [Fact]
    public void ZoomNotifications_FireOnlyWhenTheFactorChanges()
    {
        var viewModel = new MapViewModel();
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        viewModel.ZoomIn();
        Assert.Contains(nameof(MapViewModel.Zoom), changed);
        Assert.Contains(nameof(MapViewModel.ZoomPercentText), changed);
        Assert.Contains(nameof(MapViewModel.CanZoomIn), changed);

        // A no-op write (already at 100 %) stays quiet so the view does not re-anchor needlessly.
        viewModel.ResetZoom();
        changed.Clear();
        viewModel.ResetZoom();
        Assert.Empty(changed);

        // Clamping to the limit that is already applied is also a no-op.
        viewModel.Zoom = MapViewModel.MaximumZoom + 5d;
        changed.Clear();
        viewModel.ZoomIn();
        Assert.Empty(changed);
    }

    private static TowerMapLoadResult Fixture(string activeTemplate = "4HMA") => new(
        Templates:
        [
            new TowerMapTemplate("4HMA", Nodes("4HMA"), Edges("4HMA")),
            new TowerMapTemplate("A", Nodes("A"), Edges("A"))
        ],
        ActiveTemplateId: activeTemplate,
        ActiveTermStartUtc: DateTimeOffset.UnixEpoch,
        ActiveTermExpiresUtc: DateTimeOffset.FromUnixTimeSeconds(2_000_000),
        RecentTerms: [new("4HMA", DateTimeOffset.UnixEpoch)],
        UpcomingTerms: [new("C", DateTimeOffset.FromUnixTimeSeconds(3_000_000))],
        Warnings: []);

    private static IReadOnlyList<MapNode> Nodes(string id) =>
    [
        new MapNode(id, "S_MET", TowerMapCatalog.HeadFloorId, 0, "", "", "", false, TowerMapCatalog.WaitingRoomStopId, TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR", 0d),
        new MapNode(id, "S_MET", "MET_FLR_01", 1, "MET_AREA_010", "AREA_NAME.TXT_MET_0001", "IMA OKA", true, "ELV_MAIN_MET_FLR_01", TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR", 0d),
        new MapNode(id, "S_MET", "MET_FLR_02", 2, "MET_AREA_020", "AREA_NAME.TXT_MET_0002", "WANOKI", true, "", "", "", 0d),
        // A close second dot on floor 2: layout must spread it to the guaranteed minimum gap.
        new MapNode(id, "S_MET", "MET_FLR_02", 2, "MET_AREA_022", "AREA_NAME.TXT_MET_0003", "KITA", false, "", "", "", 4d)
    ];

    private static IReadOnlyList<MapEdge> Edges(string id)
    {
        var common = new[]
        {
            new MapEdge(id, TowerMapCatalog.HeadFloorId, "", "MET_FLR_01", "MET_AREA_010", 0, "", ""),
            new MapEdge(id, "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_020", 0, "", "")
        };
        if (id != "4HMA") return common;
        return
        [
            .. common,
            new MapEdge(id, "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_022", 1, "KGF_1", "KGF_2")
        ];
    }

    private static TowerMapLoadResult BossFixture()
    {
        var miniType = new MapBossType("MBOSS1", "hearing");
        var bossType = new MapBossType("STAGE_BOSS1", "boss-hearing");
        var gate = new MapBossGate("GATE_FFM_WS_01", 440, "4FORCEMEN.TXT_NORMAL", "NORMAL");

        var nodes = new List<MapNode>
        {
            new("4HMA", "S_MET", TowerMapCatalog.HeadFloorId, 0, "", "", "", false, TowerMapCatalog.WaitingRoomStopId, TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR", 0d),
            new("4HMA", "S_MET", "MET_FLR_01", 1, "MET_AREA_010", "AREA_NAME.TXT_MET_0001", "IMA OKA", true, "", "", "", 0d),
            new("4HMA", "S_MET", "MET_FLR_02", 2, "MET_AREA_020", "AREA_NAME.TXT_MET_0002", "WANOKI", true, "", "", "", 0d)
            {
                MainBossMin = 1,
                MainBossMax = 1,
                MainBossTypes = [miniType]
            },
            new("4HMA", "S_MET", "MET_FLR_02", 2, "MET_AREA_022", "AREA_NAME.TXT_MET_0003", "KITA", false, "", "", "", 4d)
            {
                IsBossArena = true,
                ArenaBoss = bossType
            },
            new("4HMA", "S_MET", "MET_FLR_02", 2, "MET_AREA_023", "AREA_NAME.TXT_MET_0004", "SHINJUKU", false, "", "", "", 8d)
            {
                IsForceManRoom = true,
                ForceManGate = gate
            }
        };
        var edges = new List<MapEdge>
        {
            new("4HMA", "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_020", 0, "KGF_MET_MIDBOSS00_CLEAR", ""),
            new("4HMA", "MET_FLR_01", "MET_AREA_010", "MET_FLR_02", "MET_AREA_023", 0, "", "KGF_MET_MB00_BTN_AREA_010_GOAL")
        };

        return new TowerMapLoadResult(
            [new TowerMapTemplate("4HMA", nodes, edges)],
            "4HMA",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.FromUnixTimeSeconds(2_000_000),
            [],
            [],
            []);
    }
}
