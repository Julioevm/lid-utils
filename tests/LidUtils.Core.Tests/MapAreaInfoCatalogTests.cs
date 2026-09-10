using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class MapAreaInfoCatalogTests
{
    [Fact]
    public void Resolve_UsesTheTemplateRotationGroup()
    {
        var catalog = MapAreaInfoCatalog.Create(
        [
            Info(5, "Omeno-Inari", ["1", "4"], notes: "Four Foremen (4HMA only)."),
            Info(5, "Omeno-Inari", ["6"], notes: "Friday variant."),
            Info(1, "Ikegara", ["0"], material: "All"),
            Info(27, "Yukiyoshi", ["1", "6"], shop: "G\u2605")
        ]);

        // 4HMA maps to rotation 1; A maps to rotation 6.
        Assert.Equal("Four Foremen (4HMA only).", catalog.Resolve("4HMA", 5, "Omeno-Inari")!.Notes);
        Assert.Equal("Friday variant.", catalog.Resolve("A", 5, "Omeno-Inari")!.Notes);

        // Base areas resolve on every template.
        foreach (var template in TowerMapCatalog.TemplateIds)
            Assert.NotNull(catalog.Resolve(template, 1, "Ikegara"));
    }

    [Fact]
    public void Resolve_IsCaseAndSeparatorInsensitive()
    {
        var catalog = MapAreaInfoCatalog.Create([Info(38, "Jiyu-Toge", ["0"], material: "Copper")]);

        Assert.NotNull(catalog.Resolve("4HMA", 38, "jiyu toge"));
        Assert.NotNull(catalog.Resolve("4HMA", 38, "JIYU_TOGE"));
        Assert.NotNull(catalog.Resolve("4HMA", 38, "Jiyu-Toge"));
    }

    [Fact]
    public void Resolve_AppliesNameAliases()
    {
        var catalog = MapAreaInfoCatalog.Create(
            [Info(8, "Kohni", ["1", "4"], info: "44CE")],
            nameAliases: new Dictionary<string, string> { ["KOONI"] = "Kohni" });

        var resolved = catalog.Resolve("4HMA", 8, "KO-ONI");

        Assert.NotNull(resolved);
        Assert.Equal("44CE", resolved!.Info);
    }

    [Fact]
    public void Resolve_FallsBackToTheOnlyRowWhenTheSheetFloorDiffers()
    {
        var catalog = MapAreaInfoCatalog.Create([Info(9, "Dasahi-Machi", ["0"], material: "Fiber")]);

        // The sheet lists this area on 9F; the database mounts it on 8F for one template.
        var resolved = catalog.Resolve("C", 8, "Dasahi-Machi");

        Assert.NotNull(resolved);
        Assert.Equal(9, resolved!.FloorNumber);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenTheAreaIsUnknown()
    {
        var catalog = MapAreaInfoCatalog.Create([Info(1, "Ikegara", ["0"])]);

        Assert.Null(catalog.Resolve("4HMA", 99, "Nowhere"));
        Assert.Null(MapAreaInfoCatalog.Empty.Resolve("4HMA", 1, "Ikegara"));
    }

    [Fact]
    public void AreaFlagsAndLabels_ArePlayerFacing()
    {
        var rotationOnly = Info(5, "Omeno-Inari", ["1", "4"], material: "\u00d7", boss: "DOD 44CE", notes: "Four Foremen.");
        rotationOnly = rotationOnly with { HasStamp = true, Shop = "G", Trap = "Blue", YotsuyamaBionics = 6, Tales = "1.4" };

        Assert.True(rotationOnly.IsRotationOnly);
        Assert.False(rotationOnly.IsAlwaysPresent);
        Assert.False(rotationOnly.HasMaterial);
        Assert.True(rotationOnly.HasStamp);
        Assert.True(rotationOnly.HasShop);
        Assert.True(rotationOnly.HasCollectible);
        Assert.True(rotationOnly.HasTrap);
        Assert.True(rotationOnly.HasNotes);
        Assert.Equal("Wednesday/Sunday", MapAreaRotations.Describe(rotationOnly.Rotations).Replace(" only", string.Empty));
        Assert.Equal("Gyakufunsha (may appear)", rotationOnly.ShopLabel);
        Assert.Contains("Yotsuyama Bionics p.6", rotationOnly.CollectibleLabel, StringComparison.Ordinal);
        Assert.Contains("Tales 1.4", rotationOnly.CollectibleLabel, StringComparison.Ordinal);

        var baseArea = Info(1, "Ikegara", ["0"]);
        Assert.True(baseArea.IsAlwaysPresent);
        Assert.Equal("every rotation", baseArea.RotationLabel);

        var bttt = Info(50, "Naka-Wara", ["B"]);
        Assert.True(bttt.IsAlwaysPresent);
        Assert.Equal("Battle To The Top only", bttt.RotationLabel);
    }

    [Fact]
    public void Loader_RejectsBadDocuments()
    {
        Assert.Throws<CatalogValidationException>(() => MapAreaInfoCatalogLoader.Parse("{}"));
        Assert.Throws<CatalogValidationException>(() => MapAreaInfoCatalogLoader.Parse(
            """{"schemaVersion":2,"templateRotation":{},"areas":[]}"""));
        Assert.Throws<CatalogValidationException>(() => MapAreaInfoCatalogLoader.Parse(
            """{"schemaVersion":1,"templateRotation":{"4HMA":"1"},"areas":[{"floor":1,"name":"Ikegara"}]}"""));
        Assert.Throws<CatalogValidationException>(() => MapAreaInfoCatalogLoader.Parse(
            """{"schemaVersion":1,"templateRotation":{},"areas":[{"floor":1,"name":"Ikegara","unexpected":true}]}"""));
    }

    [Fact]
    public void Loader_WarnsButLoadsOnDuplicateRows()
    {
        var catalog = MapAreaInfoCatalogLoader.Parse(
            """
            {
              "schemaVersion": 1,
              "templateRotation": {"4HMA":"1","D":"2","C":"3","B":"5","A":"6"},
              "areas": [
                {"floor": 1, "name": "Ikegara", "rotations": ["0"], "material": "All"},
                {"floor": 1, "name": "Ikegara", "rotations": ["0"], "material": "All"}
              ]
            }
            """);

        Assert.Equal(2, catalog.Areas.Count);
        Assert.Contains(catalog.Warnings, warning => warning.Contains("Duplicate", StringComparison.Ordinal));
    }

    private static MapAreaInfo Info(
        int floor,
        string name,
        IReadOnlyList<string> rotations,
        string info = "",
        string material = "All",
        string shop = "",
        string boss = "",
        string notes = "") =>
        new(floor, name, rotations, info, material, false, shop, boss, "", null, null, notes);
}
