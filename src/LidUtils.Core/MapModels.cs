namespace LidUtils.Core;

/// <summary>
/// Describes one themed world ("stage") that owns a contiguous visible floor band
/// in the tower map. The first/last values are the visible tower floor numbers
/// (master_floor.sname/no), not the internal floor-slot indices.
/// </summary>
public sealed record MapStageInfo(string StageId, string DisplayName, int FirstVisibleFloor, int LastVisibleFloor);

/// <summary>
/// A single area mounted on one floor slot for one map rotation ("template").
/// A floor slot can host several nodes side by side (base corridors plus
/// template-only side areas), which is why a node is keyed by both FloorId and AreaId.
/// </summary>
public sealed record MapNode(
    string TemplateId,
    string StageId,
    string FloorId,
    int FloorNumber,
    string AreaId,
    string AreaNameKey,
    string AreaName,
    bool IsBase,
    string ElevatorStopId,
    string ElevatorCarId,
    string ElevatorCarLabel,
    double OfsX)
{
    /// <summary>Node key used to resolve edges (HEAD has an empty AreaId).</summary>
    public string Key => FloorId + "/" + AreaId;

    /// <summary>True for the virtual waiting-room node below F1.</summary>
    public bool IsHead => string.Equals(FloorId, TowerMapCatalog.HeadFloorId, StringComparison.Ordinal);
}

/// <summary>
/// One allowed upward transition between two areas of adjacent or distant floors
/// (dir = 0 in master_area_connect_escalator). The downward mirror is not stored.
/// <paramref name="Ci"/> is the route's difficulty/skip hint from the game data
/// (0–6; -1 marks dead ends, which are excluded while loading).
/// </summary>
public sealed record MapEdge(
    string TemplateId,
    string FloorId,
    string AreaId,
    string ToFloorId,
    string ToAreaId,
    int Ci,
    string Key,
    string Gate)
{
    public string Key2 => FloorId + "/" + AreaId;
    public string TargetKey => ToFloorId + "/" + ToAreaId;
    public bool IsGated => !string.IsNullOrWhiteSpace(Key) || !string.IsNullOrWhiteSpace(Gate);
    public bool SkipsFloors => Ci > 1;
}

/// <summary>The tower layout for a single map rotation.</summary>
public sealed record TowerMapTemplate(
    string Id,
    IReadOnlyList<MapNode> Nodes,
    IReadOnlyList<MapEdge> Edges);

/// <summary>One entry of the pre-generated term calendar (master_area_template_term).</summary>
public sealed record TowerTermEntry(string TemplateId, DateTimeOffset ExpiresUtc);

/// <summary>
/// The complete map payload for the five main rotations ("4HMA", "A"–"D") plus the
/// active-term resolution used for the "today" shortcut. Loaded read-only from masters.db.
/// </summary>
public sealed record TowerMapLoadResult(
    IReadOnlyList<TowerMapTemplate> Templates,
    string ActiveTemplateId,
    DateTimeOffset ActiveTermStartUtc,
    DateTimeOffset ActiveTermExpiresUtc,
    IReadOnlyList<TowerTermEntry> RecentTerms,
    IReadOnlyList<TowerTermEntry> UpcomingTerms,
    IReadOnlyList<string> Warnings)
{
    public TowerMapTemplate? FindTemplate(string id) =>
        Templates.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Static identifiers and band math shared by the map reader and the viewer.
/// The themed bands are consecutive in the visible numbering (confirmed on the installed build):
/// Metro 1–10, Arcade 11–20, Amusement 21–30, Rooftop 31–40, Hazama 41–50; the Last-boss stage
/// also sits at 41 as a special and Heaven starts at 51 (both beyond this first-iteration scope).
/// </summary>
public static class TowerMapCatalog
{
    /// <summary>The virtual node below F1 that escalator rows reference as the source of the first climb.
    /// In game this area is the Waiting Room, the hub that also hosts the main elevator.</summary>
    public const string HeadFloorId = "HEAD";

    /// <summary>Elevator car id of the main tower elevator (its hub is the Waiting Room).</summary>
    public const string MainElevatorCarId = "ELV_MAIN";

    /// <summary>Stop id of the main elevator's Waiting Room hub (not present on floor nodes).</summary>
    public const string WaitingRoomStopId = "ELV_MAIN_HUB";

    /// <summary>Player-facing name of the HEAD node, used by the viewer.</summary>
    public const string WaitingRoomName = "Waiting room";

    /// <summary>The five main rotations carried by the term calendar.</summary>
    public static IReadOnlyList<string> TemplateIds { get; } = ["4HMA", "A", "B", "C", "D"];

    /// <summary>Stages rendered by the first-iteration map viewer (floors 1–50 plus the boss).</summary>
    public static IReadOnlyList<MapStageInfo> Stages { get; } =
    [
        new("S_MET", "Metro", 1, 10),
        new("S_ARC", "Arcade", 11, 20),
        new("S_AMS", "Amusement", 21, 30),
        new("S_RFT", "Rooftop", 31, 40),
        new("S_HZM", "Hazama", 41, 50),
        new("S_LAS", "Last boss", 41, 41)
    ];

    public static MapStageInfo? FindStage(string stageId) =>
        Stages.FirstOrDefault(stage => string.Equals(stage.StageId, stageId, StringComparison.Ordinal));

    /// <summary>
    /// Visible floor number for an internal floor slot id such as MET_FLR_04.
    /// Returns 0 for the virtual HEAD slot and null when the id cannot be mapped.
    /// </summary>
    public static int? FloorNumberFor(string floorId)
    {
        if (string.IsNullOrWhiteSpace(floorId)) return null;
        if (string.Equals(floorId, HeadFloorId, StringComparison.Ordinal)) return 0;
        foreach (var stage in Stages)
        {
            // Floor slot ids follow the stage id minus its S_ prefix (AMS_FLR_01, LAS_FLR_01, ...).
            var prefix = stage.StageId[2..] + "_FLR_";
            if (!floorId.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var tail = floorId[prefix.Length..];
            if (!int.TryParse(tail, out var index) || index < 1) return null;
            var floor = stage.FirstVisibleFloor + (index - 1);
            return floor <= stage.LastVisibleFloor ? floor : null;
        }

        return null;
    }
}
