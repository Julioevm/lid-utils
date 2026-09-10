using System.Text.RegularExpressions;

namespace LidUtils.Core;

/// <summary>
/// Curated, community-sourced facts for one floor area on one set of map rotations.
/// Loaded from the bundled catalog (settings/map-area-info.json); never read from masters.db.
/// See docs/map_area_info_plan.md for the source sheet and the rotation model.
/// </summary>
public sealed record MapAreaInfo(
    int FloorNumber,
    string Name,
    IReadOnlyList<string> Rotations,
    string Info,
    string Material,
    bool HasStamp,
    string Shop,
    string Boss,
    string Trap,
    int? YotsuyamaBionics,
    string? Tales,
    string Notes)
{
    /// <summary>The sheet's marker for "this area yields no loose crafting materials".</summary>
    public const string NoMaterialMarker = "\u00d7";

    /// <summary>True when the area is mounted by every rotation (base roster or the fixed endgame band).</summary>
    public bool IsAlwaysPresent =>
        Rotations.Contains(MapAreaRotations.Base) || Rotations.Contains(MapAreaRotations.BattleToTheTop);

    /// <summary>True when the area only appears on some rotations.</summary>
    public bool IsRotationOnly => !IsAlwaysPresent;

    public bool HasMaterial => !string.IsNullOrWhiteSpace(Material) && Material != NoMaterialMarker;
    public bool HasShop => !string.IsNullOrWhiteSpace(Shop);
    public bool HasTrap => !string.IsNullOrWhiteSpace(Trap);
    public bool HasCollectible => YotsuyamaBionics is not null || !string.IsNullOrWhiteSpace(Tales);
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Player-facing shop text, e.g. "Gyakufunsha (always appears)".</summary>
    public string ShopLabel => MapAreaRotations.ShopLabel(Shop);

    /// <summary>Player-facing rotation text, e.g. "Wednesday/Sunday only".</summary>
    public string RotationLabel => MapAreaRotations.Describe(Rotations);

    /// <summary>Player-facing collectible text, e.g. "Yotsuyama Bionics p.6 · Tales 1.4".</summary>
    public string CollectibleLabel
    {
        get
        {
            var parts = new List<string>(2);
            if (YotsuyamaBionics is { } page) parts.Add($"Yotsuyama Bionics p.{page}");
            if (!string.IsNullOrWhiteSpace(Tales)) parts.Add($"Tales {Tales}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The rotation model shared by the curated catalog and the map viewer.
/// Rotation ids come from the community sheet: <c>0</c> is the always-present base roster,
/// <c>B</c> the fixed Battle-To-The-Top band, and <c>1</c>..<c>7</c> the weekday groups
/// (<c>1</c>=<c>4</c> Wednesday/Sunday, <c>2</c> Monday, <c>3</c>=<c>7</c> Tuesday/Saturday,
/// <c>5</c> Thursday, <c>6</c> Friday).
/// </summary>
public static class MapAreaRotations
{
    public const string Base = "0";
    public const string BattleToTheTop = "B";

    /// <summary>Which sheet rotation group each of the five DB templates corresponds to.</summary>
    public static IReadOnlyDictionary<string, string> TemplateRotation { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["4HMA"] = "1",
            ["D"] = "2",
            ["C"] = "3",
            ["B"] = "5",
            ["A"] = "6"
        };

    /// <summary>The rotation ids a given template mounts (base + the fixed band + its weekday group).</summary>
    public static IReadOnlySet<string> GroupsFor(
        string templateId,
        IReadOnlyDictionary<string, string>? templateRotation = null)
    {
        var map = templateRotation ?? TemplateRotation;
        var groups = new HashSet<string>(StringComparer.Ordinal) { Base, BattleToTheTop };
        if (map.TryGetValue(templateId, out var rotation) && !string.IsNullOrWhiteSpace(rotation))
            groups.Add(rotation);
        return groups;
    }

    /// <summary>Human-readable day name for a single rotation id; empty when unknown.</summary>
    public static string DayLabel(string rotationId) => rotationId switch
    {
        "1" or "4" => "Wednesday/Sunday",
        "2" => "Monday",
        "3" or "7" => "Tuesday/Saturday",
        "5" => "Thursday",
        "6" => "Friday",
        BattleToTheTop => "Battle To The Top",
        _ => string.Empty
    };

    /// <summary>Player-facing description of a row's rotation set.</summary>
    public static string Describe(IReadOnlyList<string> rotations)
    {
        if (rotations.Count == 0) return string.Empty;
        if (rotations.Contains(Base)) return "every rotation";
        var days = rotations
            .Select(DayLabel)
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return days.Count == 0 ? string.Empty : string.Join("/", days) + " only";
    }

    /// <summary>Player-facing wandering-shop text.</summary>
    public static string ShopLabel(string? shop) => shop switch
    {
        "C" => "Chokufunsha",
        "G" => "Gyakufunsha (may appear)",
        "G\u2605" => "Gyakufunsha (always appears)",
        null or "" => string.Empty,
        _ => shop
    };
}

/// <summary>
/// The curated area catalog, indexed by floor + normalized area name and resolved per template
/// through the rotation groups. Built by <see cref="MapAreaInfoCatalogLoader"/>.
/// </summary>
public sealed class MapAreaInfoCatalog
{
    private static readonly Regex NormalizePattern = new(@"[\s_\-]", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<(int Floor, string Name), IReadOnlyList<MapAreaInfo>> _byPlacement;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MapAreaInfo>> _byName;
    private readonly IReadOnlyDictionary<string, string> _templateRotation;
    private readonly IReadOnlyDictionary<string, string> _nameAliases;

    private MapAreaInfoCatalog(
        IReadOnlyList<MapAreaInfo> areas,
        IReadOnlyDictionary<string, string> templateRotation,
        IReadOnlyDictionary<string, string> nameAliases,
        IReadOnlyList<string> warnings)
    {
        Areas = areas;
        _templateRotation = templateRotation;
        _nameAliases = nameAliases;
        Warnings = warnings;

        var byPlacement = new Dictionary<(int, string), List<MapAreaInfo>>();
        var byName = new Dictionary<string, List<MapAreaInfo>>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            var key = NormalizeName(area.Name);
            Add(byPlacement, (area.FloorNumber, key), area);
            Add(byName, key, area);
        }

        _byPlacement = byPlacement.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<MapAreaInfo>)pair.Value);
        _byName = byName.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<MapAreaInfo>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>An empty catalog; resolving against it always returns null.</summary>
    public static MapAreaInfoCatalog Empty { get; } = new(
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        []);

    /// <summary>Every curated row, in sheet order.</summary>
    public IReadOnlyList<MapAreaInfo> Areas { get; }

    /// <summary>Non-fatal issues found while loading (missing rotations, duplicate rows, ...).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public static MapAreaInfoCatalog Create(
        IReadOnlyList<MapAreaInfo> areas,
        IReadOnlyDictionary<string, string>? templateRotation = null,
        IReadOnlyDictionary<string, string>? nameAliases = null,
        IReadOnlyList<string>? warnings = null)
    {
        var aliases = nameAliases is { Count: > 0 }
            ? nameAliases.ToDictionary(pair => NormalizeName(pair.Key), pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new MapAreaInfoCatalog(
            areas,
            templateRotation is { Count: > 0 } ? templateRotation : MapAreaRotations.TemplateRotation,
            aliases,
            warnings ?? []);
    }

    /// <summary>
    /// Resolves the curated row for a map node: matches the template's rotation group first,
    /// then falls back to the first row when the area has exactly one candidate (e.g. a
    /// floor-number discrepancy in the source sheet).
    /// </summary>
    public MapAreaInfo? Resolve(string templateId, int floorNumber, string areaName)
    {
        if (string.IsNullOrWhiteSpace(areaName)) return null;
        var key = NormalizeName(areaName);
        if (_nameAliases.TryGetValue(key, out var alias) && !string.IsNullOrWhiteSpace(alias))
            key = NormalizeName(alias);

        if (!_byPlacement.TryGetValue((floorNumber, key), out var rows))
        {
            if (!_byName.TryGetValue(key, out rows) || rows.Count == 0) return null;
        }

        var groups = MapAreaRotations.GroupsFor(templateId, _templateRotation);
        foreach (var row in rows)
        {
            if (row.Rotations.Any(groups.Contains)) return row;
        }

        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>Normalizes an area name for matching: case, whitespace, underscores and hyphens.</summary>
    public static string NormalizeName(string name) =>
        NormalizePattern.Replace(name.Trim(), string.Empty).ToUpperInvariant();

    private static void Add<TKey>(Dictionary<TKey, List<MapAreaInfo>> index, TKey key, MapAreaInfo area)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(area);
    }
}
