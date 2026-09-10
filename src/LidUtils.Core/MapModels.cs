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

    /// <summary>
    /// Roaming main (section) boss spawn count range for this placement
    /// (master_floor.mbsmin..mbsmax). 0,0 means the placement has no roaming boss on the
    /// selected rotation.
    /// </summary>
    public int MainBossMin { get; init; }

    public int MainBossMax { get; init; }

    /// <summary>
    /// Main (section) bosses this area can host, inferred from master_stage_mboss unit tokens.
    /// Sorted by type id; empty when the area has no known boss units.
    /// </summary>
    public IReadOnlyList<MapBossType> MainBossTypes { get; init; } = [];

    /// <summary>True for a paid Four Force Men (4FORCEMEN) room area, i.e. one holding a *_SMALLBOSS unit.</summary>
    public bool IsForceManRoom { get; init; }

    /// <summary>Paid gate guarding this room (master_stage_gate + master_gate), when present.</summary>
    public MapBossGate? ForceManGate { get; init; }

    /// <summary>True for the section boss arena area, i.e. one holding a *_BOSS unit.</summary>
    public bool IsBossArena { get; init; }

    /// <summary>Section boss fought in this stage's arena (STAGE_BOSS1..4), when known.</summary>
    public MapBossType? ArenaBoss { get; init; }

    /// <summary>True when the placement can spawn a roaming main (section) boss.</summary>
    public bool HasMainBoss => MainBossMax > 0 || MainBossMin > 0;

    /// <summary>True when the area is associated with any boss encounter.</summary>
    public bool HasBoss => HasMainBoss || IsForceManRoom || IsBossArena;
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

    /// <summary>Boss meaning of the route's key/gate strings, used for styling and tooltips.</summary>
    public MapBossRoute BossRoute => TowerBossRoutes.Classify(Key, Gate);
}

/// <summary>
/// The four main tower section bosses. Their names come from the master_text ENEMY entries
/// (TXT_MAXSHARP / TXT_COLONELJACKSON / TXT_MRCROWLEY / TXT_GUNKANYAMA); the numbering matches
/// MBOSS1..4 and STAGE_BOSS1..4 in master_stage_mboss.
/// </summary>
public static class TowerBosses
{
    /// <summary>Section boss names, indexed by boss number (1–4).</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["Max Sharp", "Colonel Jackson", "Mr Crowley", "Gunkanyama"];

    /// <summary>English name for an MBOSS/STAGE_BOSS type id; the id itself when unknown.</summary>
    public static string NameFor(string typeId)
    {
        var number = NumberOf(typeId);
        return number >= 1 && number <= Names.Count ? Names[number - 1] : typeId;
    }

    /// <summary>English name for a 1-based boss number.</summary>
    public static string NameForIndex(int number) =>
        number >= 1 && number <= Names.Count ? Names[number - 1] : $"Boss {number}";

    private static int NumberOf(string typeId)
    {
        const string arenaPrefix = "STAGE_BOSS";
        const string mainPrefix = "MBOSS";
        var digits = typeId.StartsWith(arenaPrefix, StringComparison.Ordinal)
            ? typeId[arenaPrefix.Length..]
            : typeId.StartsWith(mainPrefix, StringComparison.Ordinal)
                ? typeId[mainPrefix.Length..]
                : string.Empty;
        return int.TryParse(digits, out var number) ? number : 0;
    }
}

/// <summary>One main (section) boss type (master_mboss); MBOSS1 is Max Sharp, and so on.</summary>
public sealed record MapBossType(string Id, string Name)
{
    /// <summary>English player-facing name, e.g. "Max Sharp".</summary>
    public string DisplayName
    {
        get
        {
            var name = TowerBosses.NameFor(Id);
            return name != Id ? name : !string.IsNullOrWhiteSpace(Name) ? Name : Id;
        }
    }
}

/// <summary>
/// A paid boss room gate. Built from master_stage_gate (which floor) joined to master_gate
/// (price and difficulty), with the difficulty label resolved through master_text.
/// </summary>
public sealed record MapBossGate(string GateId, int Fee, string DifficultyKey, string DifficultyLabel)
{
    /// <summary>Player-facing fee text, e.g. "440 KC" or "free".</summary>
    public string FeeText => Fee > 0 ? $"{Fee:N0} KC" : "free";
}

/// <summary>How an escalator key/gate string relates to boss progression.</summary>
public enum MapBossRouteKind
{
    None,
    BossClear,
    BossArenaClear,
    BossTrigger,
    KeyOrButton
}

/// <summary>A human-readable description of a route's boss prerequisite.</summary>
public sealed record MapBossRoute(MapBossRouteKind Kind, string Label)
{
    public static readonly MapBossRoute None = new(MapBossRouteKind.None, string.Empty);

    /// <summary>True when the route is specifically tied to a boss encounter.</summary>
    public bool IsBoss => Kind is MapBossRouteKind.BossClear or MapBossRouteKind.BossArenaClear or MapBossRouteKind.BossTrigger;
}

/// <summary>
/// Classifies the KGF_* key/gate strings on master_area_connect_escalator rows. Confirmed patterns
/// on the installed build: KGF_&lt;stage&gt;_MIDBOSSnn_CLEAR, KGF_&lt;stage&gt;_BOSS_CLEAR,
/// KGF_RFT_FIXED_AREA_BOSS_nnnn, KGF_&lt;stage&gt;_(MBnn|BOSS)_BTN_*; everything else is an
/// ordinary button or gate key.
/// </summary>
public static class TowerBossRoutes
{
    private static readonly System.Text.RegularExpressions.Regex BossClearPattern = new(
        "^KGF_(?<stage>[A-Z0-9]+)_MIDBOSS(?<first>\\d{2})(?:_(?<second>\\d{2}))?_CLEAR$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BossArenaClearPattern = new(
        "^KGF_(?<stage>[A-Z0-9]+)_BOSS_CLEAR$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FixedAreaBossPattern = new(
        "^KGF_[A-Z0-9]+_FIXED_AREA_BOSS_\\d+$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BossTriggerPattern = new(
        "^KGF_(?<stage>[A-Z0-9]+)_(?<boss>MB(?<mb>\\d{2})|BOSS)_BTN",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static MapBossRoute Classify(string? key, string? gate)
    {
        var keyText = key ?? string.Empty;
        var gateText = gate ?? string.Empty;

        var bossClear = BossClearPattern.Match(keyText);
        if (bossClear.Success)
            return new MapBossRoute(MapBossRouteKind.BossClear, BossClearLabel(bossClear));

        if (BossArenaClearPattern.IsMatch(keyText) || FixedAreaBossPattern.IsMatch(keyText))
            return new MapBossRoute(MapBossRouteKind.BossArenaClear, "Requires section boss cleared");

        var trigger = BossTriggerPattern.Match(gateText);
        if (!trigger.Success) trigger = BossTriggerPattern.Match(keyText);
        if (trigger.Success)
        {
            var label = trigger.Groups["mb"].Success
                ? $"Boss trigger: {TowerBosses.NameForIndex(Number(trigger.Groups["mb"].Value))}"
                : "Boss arena trigger";
            return new MapBossRoute(MapBossRouteKind.BossTrigger, label);
        }

        return keyText.Length > 0 || gateText.Length > 0
            ? new MapBossRoute(MapBossRouteKind.KeyOrButton, "Locked route")
            : MapBossRoute.None;
    }

    private static string BossClearLabel(System.Text.RegularExpressions.Match match)
    {
        var first = TowerBosses.NameForIndex(Number(match.Groups["first"].Value));
        if (match.Groups["second"].Success)
            return $"Requires {first} & {TowerBosses.NameForIndex(Number(match.Groups["second"].Value))} cleared";
        return $"Requires {first} cleared";
    }

    /// <summary>MIDBOSS00/MB00 are boss 1, so shift the zero-based id by one.</summary>
    private static int Number(string digits) => int.TryParse(digits, out var value) ? value + 1 : 0;
}

/// <summary>The tower layout for a single map rotation.</summary>
public sealed record TowerMapTemplate(
    string Id,
    IReadOnlyList<MapNode> Nodes,
    IReadOnlyList<MapEdge> Edges);

/// <summary>
/// One entry of the pre-generated term calendar (<c>master_area_template_term</c>).
/// <paramref name="ExpiresUtc"/> is the reset boundary that <b>starts</b> the term: the template is
/// live from that instant until the next entry's boundary (the DB column keeps the legacy name
/// <c>expires</c>). The game stores the same pairing as <c>soul.tmplid</c>/<c>soul.termid</c>.
/// </summary>
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
