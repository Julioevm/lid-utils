using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.App.Tests;

/// <summary>
/// End-to-end checks for the community area-information overlay: the shipped catalog parses,
/// and the map view model resolves and surfaces it for the selected rotation.
/// </summary>
public sealed class MapAreaInfoIntegrationTests
{
    private static MapAreaInfoCatalog ShippedCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "map-area-info.json");
        Assert.True(File.Exists(path), $"The shipped catalog was not copied to '{path}'.");
        return MapAreaInfoCatalogLoader.Load(path);
    }

    [Fact]
    public void ShippedCatalog_ParsesWithoutWarningsAndResolvesKnownAreas()
    {
        var catalog = ShippedCatalog();

        Assert.True(catalog.Areas.Count >= 200, $"Expected the full community sheet, got {catalog.Areas.Count}.");
        Assert.Empty(catalog.Warnings);

        // 4HMA is the Wednesday/Sunday rotation; the rotating Four-Foreman floors are 4HMA-only.
        var fourHma = catalog.Resolve("4HMA", 5, "Omeno-Inari");
        Assert.NotNull(fourHma);
        Assert.Equal("44CE", fourHma!.Info);
        Assert.True(fourHma.IsRotationOnly);
        Assert.Equal("Wednesday/Sunday only", fourHma.RotationLabel);
        Assert.Contains("White Steel", fourHma.Notes, StringComparison.Ordinal);

        // The guaranteed Gyakufunsha shop.
        var shop = catalog.Resolve("A", 27, "Yukiyoshi");
        Assert.NotNull(shop);
        Assert.Equal("Gyakufunsha (always appears)", shop!.ShopLabel);

        // Battle-To-The-Top areas are present on every rotation.
        var bttt = catalog.Resolve("A", 50, "Naka-Wara");
        Assert.NotNull(bttt);
        Assert.True(bttt!.IsAlwaysPresent);
        Assert.Equal("Copper", bttt.Material);
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task InstalledDatabase_EveryInScopeNodeResolvesAgainstTheShippedCatalog()
    {
        var databasePath = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_DB");
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath)) return;

        var catalog = ShippedCatalog();
        var result = await new MapDataService().LoadAsync(databasePath);
        var unresolved = new List<string>();
        foreach (var template in result.Templates)
        {
            foreach (var node in template.Nodes)
            {
                if (node.IsHead || node.FloorNumber is < 1 or > 50) continue;
                if (string.IsNullOrWhiteSpace(node.AreaName)) continue; // some Hazama areas ship without a localized name
                if (catalog.Resolve(template.Id, node.FloorNumber, node.AreaName) is null)
                    unresolved.Add($"{template.Id} F{node.FloorNumber} {node.AreaName} ({node.AreaId})");
            }
        }

        Assert.True(unresolved.Count == 0, string.Join(Environment.NewLine, unresolved));
    }

    [Fact]
    public void ViewModel_SurfacesCuratedInfoOnNodesRowsAndDetails()
    {
        var viewModel = new MapViewModel(areaInfoCatalog: ShippedCatalog());
        viewModel.SetResult(Fixture("4HMA"));

        // 1F Ikegara: stamp machine + Yotsuyama Bionics page 1, no loose materials.
        var ikegara = viewModel.Nodes.Single(node => node.Node.AreaId == "MET_AREA_010");
        Assert.True(ikegara.HasLoot);
        Assert.Contains("\u25ce", ikegara.LootBadge, StringComparison.Ordinal);
        Assert.Contains("\u25ae", ikegara.LootBadge, StringComparison.Ordinal);
        Assert.False(ikegara.HasMaterial);
        Assert.Contains("Stamp machine: available", ikegara.ToolTipText, StringComparison.Ordinal);
        Assert.Contains("Collectible: Yotsuyama Bionics p.1", ikegara.ToolTipText, StringComparison.Ordinal);

        // 27F Yukiyoshi: fiber material and the guaranteed Gyakufunsha shop.
        var yukiyoshi = viewModel.AreaRows.Single(row => row.Node.AreaId == "AMS_AREA_V006");
        Assert.Equal("Fiber", yukiyoshi.MaterialLabel);
        Assert.Equal("G\u2605", yukiyoshi.ShopLabel);
        viewModel.SelectArea(yukiyoshi);
        Assert.Contains("Area info (community sheet):", viewModel.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("Materials: Fiber", viewModel.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("Shop: Gyakufunsha (always appears)", viewModel.SelectedDetails, StringComparison.Ordinal);

        // 6F Moka-Magome: 4HMA has the trap room, D (Monday) does not.
        var fourHmaTrap = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_060");
        Assert.True(fourHmaTrap.Info!.HasTrap);
        Assert.Contains("Trap room: Blue metals", fourHmaTrap.RowSummary, StringComparison.Ordinal);

        viewModel.SelectedTemplate = "D";
        var monday = viewModel.AreaRows.Single(row => row.Node.AreaId == "MET_AREA_060");
        Assert.False(monday.Info!.HasTrap);
        Assert.DoesNotContain("Trap room", monday.RowSummary, StringComparison.Ordinal);

        // The overlay toggle only affects drawing; the data stays resolved.
        var version = viewModel.GeometryVersion;
        viewModel.ShowLootInfo = false;
        Assert.True(viewModel.GeometryVersion > version);
        Assert.True(fourHmaTrap.Info!.HasTrap);
    }

    private static TowerMapLoadResult Fixture(string activeTemplate)
    {
        var templates = new List<TowerMapTemplate>();
        foreach (var id in new[] { "4HMA", "D" })
        {
            templates.Add(new TowerMapTemplate(id,
            [
                new MapNode(id, "S_MET", TowerMapCatalog.HeadFloorId, 0, "", "", "", false, TowerMapCatalog.WaitingRoomStopId, TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR", 0d),
                new MapNode(id, "S_MET", "MET_FLR_01", 1, "MET_AREA_010", "AREA_NAME.TXT_MET_0001", "Ikegara", true, "", "", "", 0d),
                new MapNode(id, "S_MET", "MET_FLR_05", 5, "MET_AREA_V450", "AREA_NAME.TXT_MET_0005", "Omeno-Inari", false, "", "", "", 0d),
                new MapNode(id, "S_MET", "MET_FLR_06", 6, "MET_AREA_060", "AREA_NAME.TXT_MET_0006", "Moka-Magome", true, "", "", "", 0d),
                new MapNode(id, "S_AMS", "AMS_FLR_07", 27, "AMS_AREA_V006", "AREA_NAME.TXT_AMS_0007", "Yukiyoshi", false, "", "", "", 0d)
            ], []));
        }

        return new TowerMapLoadResult(
            templates,
            activeTemplate,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.FromUnixTimeSeconds(2_000_000),
            [],
            [],
            []);
    }
}
