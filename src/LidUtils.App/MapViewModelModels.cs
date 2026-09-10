using LidUtils.Core;

namespace LidUtils.App;

/// <summary>A band/floor filter choice shown in the map toolbar.</summary>
public sealed record MapBandOption(string Key, string Label, string[] StageIds, bool IncludeHead);

/// <summary>Layout item describing one row (floor) of the tower map.</summary>
public sealed class MapRowItem
{
    public required int FloorNumber { get; init; }
    public required string Label { get; init; }
    public required double Y { get; init; }
}

/// <summary>Layout item describing one area node chip on the tower map.</summary>
public sealed class MapNodeItem
{
    public required MapNode Node { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Radius { get; init; }
    public required string StageId { get; init; }
    public required string ToolTipText { get; init; }
    public required string LabelText { get; init; }
    public bool ShowLabel { get; init; }
    public bool IsHead => string.Equals(Node.FloorId, TowerMapCatalog.HeadFloorId, StringComparison.Ordinal);
    public bool HasElevator => !string.IsNullOrWhiteSpace(Node.ElevatorStopId);
    public string Key => Node.Key;

    /// <summary>Short boss marker (BOSS / FFM) drawn above the node; empty when none.</summary>
    public string BossBadge { get; init; } = string.Empty;

    public bool HasForceManRoom => Node.IsForceManRoom;
    public bool HasBossArena => Node.IsBossArena;
    public bool HasBoss => Node.HasBoss;

    /// <summary>Elevator car serving this node (empty when the area has no elevator stop).</summary>
    public string ElevatorCarId => Node.ElevatorCarId;

    /// <summary>Slot into the viewer's elevator palette; -1 when the area has no elevator service.</summary>
    public int ElevatorColorIndex { get; init; } = -1;
}

/// <summary>
/// One straight leg of an elevator service route: it joins two consecutive stops (floors the
/// same car serves) and is drawn in the service colour. Elevators let the player travel between
/// stops even when there is no direct escalator connection between them.
/// </summary>
public sealed class MapElevatorSegmentItem
{
    public required string CarId { get; init; }
    public required string CarLabel { get; init; }
    public required int ColorIndex { get; init; }
    public required double X1 { get; init; }
    public required double Y1 { get; init; }
    public required double X2 { get; init; }
    public required double Y2 { get; init; }
    public required string ToolTipText { get; init; }
}

/// <summary>Layout item describing one upward connection between two area chips.</summary>
public sealed class MapEdgeItem
{
    public required MapEdge Edge { get; init; }
    public required double X1 { get; init; }
    public required double Y1 { get; init; }
    public required double X2 { get; init; }
    public required double Y2 { get; init; }
    public required bool IsGated { get; init; }
    public required bool IsHeadEdge { get; init; }
    public required MapBossRoute BossRoute { get; init; }
    public required string ToolTipText { get; init; }

    public bool IsBossRoute => BossRoute.IsBoss;
}

/// <summary>Read-only list row describing one area of the current rotation and band.</summary>
public sealed class MapAreaRow
{
    public required MapNode Node { get; init; }
    public required string FloorLabel { get; init; }
    public required string Name { get; init; }
    public required string KindLabel { get; init; }
    public required string ElevatorLabel { get; init; }
    public required string OffsetLabel { get; init; }
    public required string StageId { get; init; }
    public string BossLabel { get; init; } = string.Empty;
    public string BossSummary { get; init; } = string.Empty;
    public bool HasBoss => Node.HasBoss;
    public string Key => Node.Key;
}
