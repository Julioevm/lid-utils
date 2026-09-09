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
    public void SelectTodayTemplate_JumpsToTheLiveRotation()
    {
        var viewModel = new MapViewModel();
        viewModel.SetResult(Fixture(activeTemplate: "B"));
        Assert.Equal("B", viewModel.SelectedTemplate);

        viewModel.SelectedTemplate = "A";
        viewModel.SelectTodayTemplate();
        Assert.Equal("B", viewModel.SelectedTemplate);
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
}
