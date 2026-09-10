using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

/// <summary>
/// Read-only lookup tables that describe where main (section) bosses and paid Force Men rooms
/// appear. Loaded once per map load and applied to every node; every table is optional and
/// degrades to "no data". See docs/map_boss_plan.md for the schema details.
/// </summary>
internal sealed class MapBossCatalog
{
    private const string KeySeparator = "\x1f";

    /// <summary>Tokens that are too generic to identify a boss chunk reliably.</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal) { "BOSS", "GOAL" };

    private static readonly IReadOnlyDictionary<string, MapBossType> EmptyNames =
        new Dictionary<string, MapBossType>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, (int Min, int Max)> EmptyPlacements =
        new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Unit, string Type)>> EmptyStageUnits =
        new Dictionary<string, IReadOnlyList<(string Unit, string Type)>>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyDropVariants =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, MapBossType> EmptyArenaBoss =
        new Dictionary<string, MapBossType>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, MapBossGate> EmptyGates =
        new Dictionary<string, MapBossGate>(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, MapBossType> _names;
    private readonly IReadOnlyDictionary<string, (int Min, int Max)> _placements;
    private readonly AreaKinds _areaKinds;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<(string Unit, string Type)>> _stageUnits;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _dropVariants;
    private readonly IReadOnlyDictionary<string, MapBossType> _arenaBoss;
    private readonly IReadOnlyDictionary<string, MapBossGate> _gates;

    private MapBossCatalog(
        IReadOnlyDictionary<string, MapBossType> names,
        IReadOnlyDictionary<string, (int Min, int Max)> placements,
        AreaKinds areaKinds,
        IReadOnlyDictionary<string, IReadOnlyList<(string Unit, string Type)>> stageUnits,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dropVariants,
        IReadOnlyDictionary<string, MapBossType> arenaBoss,
        IReadOnlyDictionary<string, MapBossGate> gates)
    {
        _names = names;
        _placements = placements;
        _areaKinds = areaKinds;
        _stageUnits = stageUnits;
        _dropVariants = dropVariants;
        _arenaBoss = arenaBoss;
        _gates = gates;
    }

    /// <summary>Reads every boss table that is present. Missing tables silently yield no boss data.</summary>
    public static async Task<MapBossCatalog> LoadAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, HashSet<string>> tables,
        string language,
        CancellationToken cancellationToken)
    {
        var names = HasColumns(tables, "master_mboss", "id", "name")
            ? await LoadBossNamesAsync(connection, cancellationToken)
            : EmptyNames;
        var placements = HasColumns(tables, "master_floor", "id", "areaid", "mbsmin", "mbsmax")
            ? await LoadMainBossPlacementsAsync(connection, cancellationToken)
            : EmptyPlacements;
        var areaKinds = HasColumns(tables, "master_area_setting_unit", "stgid", "areaid", "unit")
            ? await LoadAreaKindsAsync(connection, cancellationToken)
            : AreaKinds.Empty;
        var stageUnits = HasColumns(tables, "master_stage_mboss", "stgid", "unit", "type")
            ? await LoadMainBossUnitsAsync(connection, cancellationToken)
            : EmptyStageUnits;
        var dropVariants = HasColumns(tables, "master_floor_drop_gen", "flrid", "areaid", "type")
            ? await LoadMainBossDropVariantsAsync(connection, cancellationToken)
            : EmptyDropVariants;
        var arenaBoss = HasColumns(tables, "master_stage_mboss", "stgid", "type")
            ? await LoadArenaBossTypesAsync(connection, names, cancellationToken)
            : EmptyArenaBoss;
        var gates = HasColumns(tables, "master_stage_gate", "flrid", "gateid") &&
                    HasColumns(tables, "master_gate", "id", "val0", "val1")
            ? await LoadForceManGatesAsync(connection, tables, language, cancellationToken)
            : EmptyGates;

        return new MapBossCatalog(names, placements, areaKinds, stageUnits, dropVariants, arenaBoss, gates);
    }

    /// <summary>Resolves the boss facts for one map node (a floor/area placement).</summary>
    public MapBossInfo Resolve(string stageId, string floorId, string areaId)
    {
        var placementKey = floorId + KeySeparator + areaId;
        _placements.TryGetValue(placementKey, out var range);
        var mainBossMin = range.Min;
        var mainBossMax = range.Max;

        var areaKey = stageId + KeySeparator + areaId;
        var isForceMan = _areaKinds.ForceManAreas.Contains(areaKey);
        var isBossArena = _areaKinds.BossArenaAreas.Contains(areaKey);

        var mainBossTypes = mainBossMax > 0 || mainBossMin > 0
            ? ResolveMainBossTypes(stageId, areaId, placementKey)
            : [];

        MapBossGate? gate = null;
        if (isForceMan) _gates.TryGetValue(floorId, out gate);

        MapBossType? arenaBoss = null;
        if (isBossArena) _arenaBoss.TryGetValue(stageId, out arenaBoss);

        return new MapBossInfo(mainBossMin, mainBossMax, mainBossTypes, isForceMan, gate, isBossArena, arenaBoss);
    }

    private IReadOnlyList<MapBossType> ResolveMainBossTypes(string stageId, string areaId, string placementKey)
    {
        // The drop generator names the exact variant for the handful of placements that define one.
        if (_dropVariants.TryGetValue(placementKey, out var dropped) && dropped.Count > 0)
            return Materialise(dropped);

        // Otherwise infer from the area's level-chunk units (master_stage_mboss tokens).
        if (_stageUnits.TryGetValue(stageId, out var tokens) &&
            _areaKinds.UnitsByArea.TryGetValue(stageId + KeySeparator + areaId, out var units))
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                foreach (var token in tokens)
                {
                    if (GenericTokens.Contains(token.Unit)) continue;
                    if (MatchesToken(unit, token.Unit)) found.Add(token.Type);
                }
            }

            if (found.Count > 0) return Materialise(found);
        }

        return [];
    }

    private IReadOnlyList<MapBossType> Materialise(IEnumerable<string> ids) =>
        ids.Select(id => _names.TryGetValue(id, out var type) ? type : new MapBossType(id, string.Empty))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Matches a master_stage_mboss unit token against a full area unit name. The zone prefix
    /// (METRO_, ARCADE_, ...) makes plain substring matching unsafe, so the token must sit on an
    /// underscore boundary: METRO_A14_BR and METRO_A14_BR_V01 both match A14_BR, but
    /// AMUSEMENT_SMALLBOSS does not match BOSS.
    /// </summary>
    private static bool MatchesToken(string unit, string token) =>
        unit.Equals(token, StringComparison.Ordinal) ||
        unit.EndsWith("_" + token, StringComparison.Ordinal) ||
        unit.Contains("_" + token + "_", StringComparison.Ordinal);

    private static async Task<Dictionary<string, MapBossType>> LoadBossNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MapBossType>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM master_mboss;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Text(reader, 0);
            if (id.Length == 0) continue;
            result[id] = new MapBossType(id, Text(reader, 1));
        }

        return result;
    }

    private static async Task<Dictionary<string, (int Min, int Max)>> LoadMainBossPlacementsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, areaid, mbsmin, mbsmax FROM master_floor;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var floorId = Text(reader, 0);
            var areaId = Text(reader, 1);
            if (floorId.Length == 0 || areaId.Length == 0) continue;
            var min = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            var max = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            if (min <= 0 && max <= 0) continue;
            result[floorId + KeySeparator + areaId] = (min, max);
        }

        return result;
    }

    private static async Task<AreaKinds> LoadAreaKindsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var unitsByArea = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var builder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var forceManAreas = new HashSet<string>(StringComparer.Ordinal);
        var bossArenaAreas = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stgid, areaid, unit FROM master_area_setting_unit;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stageId = Text(reader, 0);
            var areaId = Text(reader, 1);
            var unit = Text(reader, 2);
            if (stageId.Length == 0 || areaId.Length == 0) continue;
            var key = stageId + KeySeparator + areaId;
            if (!builder.TryGetValue(key, out var list)) builder[key] = list = [];
            list.Add(unit);
            if (unit.EndsWith("SMALLBOSS", StringComparison.Ordinal) || unit.EndsWith("SMALLBOSS2", StringComparison.Ordinal))
                forceManAreas.Add(key);
            else if (unit.EndsWith("_BOSS", StringComparison.Ordinal))
                bossArenaAreas.Add(key);
        }

        foreach (var pair in builder) unitsByArea[pair.Key] = pair.Value;
        return new AreaKinds(unitsByArea, forceManAreas, bossArenaAreas);
    }

    private static async Task<Dictionary<string, IReadOnlyList<(string Unit, string Type)>>> LoadMainBossUnitsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var builder = new Dictionary<string, List<(string Unit, string Type)>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stgid, unit, type FROM master_stage_mboss WHERE type LIKE 'MBOSS%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stageId = Text(reader, 0);
            var unit = Text(reader, 1);
            var type = Text(reader, 2);
            if (stageId.Length == 0 || unit.Length == 0 || type.Length == 0) continue;
            if (!builder.TryGetValue(stageId, out var list)) builder[stageId] = list = [];
            list.Add((unit, type));
        }

        var result = new Dictionary<string, IReadOnlyList<(string Unit, string Type)>>(StringComparer.Ordinal);
        foreach (var pair in builder) result[pair.Key] = pair.Value;
        return result;
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> LoadMainBossDropVariantsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var builder = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT flrid, areaid, type FROM master_floor_drop_gen WHERE type LIKE 'PTGENTP_MBOSS%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var floorId = Text(reader, 0);
            var areaId = Text(reader, 1);
            var type = Text(reader, 2);
            if (floorId.Length == 0 || areaId.Length == 0 || type.Length == 0) continue;
            var key = floorId + KeySeparator + areaId;
            if (!builder.TryGetValue(key, out var set)) builder[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(type.Replace("PTGENTP_", string.Empty, StringComparison.Ordinal));
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in builder) result[pair.Key] = pair.Value.ToArray();
        return result;
    }

    private static async Task<Dictionary<string, MapBossType>> LoadArenaBossTypesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, MapBossType> names,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MapBossType>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stgid, type FROM master_stage_mboss WHERE type LIKE 'STAGE_BOSS%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stageId = Text(reader, 0);
            var type = Text(reader, 1);
            if (stageId.Length == 0 || type.Length == 0) continue;
            result[stageId] = names.TryGetValue(type, out var resolved) ? resolved : new MapBossType(type, string.Empty);
        }

        return result;
    }

    private static async Task<Dictionary<string, MapBossGate>> LoadForceManGatesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, HashSet<string>> tables,
        string language,
        CancellationToken cancellationToken)
    {
        var labels = HasColumns(tables, "master_text", "sct", "id", "lang", "txt")
            ? await LoadForceManTextAsync(connection, language, cancellationToken)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, MapBossGate>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sg.flrid, sg.gateid, g.val0, g.val1 FROM master_stage_gate sg " +
            "JOIN master_gate g ON g.id = sg.gateid WHERE sg.flrid <> '';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var floorId = Text(reader, 0);
            var gateId = Text(reader, 1);
            if (floorId.Length == 0 || gateId.Length == 0) continue;
            var fee = int.TryParse(Text(reader, 2), out var value) ? value : 0;
            var difficultyKey = Text(reader, 3);
            var dot = difficultyKey.LastIndexOf('.');
            var id = dot >= 0 && dot < difficultyKey.Length - 1 ? difficultyKey[(dot + 1)..] : difficultyKey;
            var label = labels.TryGetValue(id, out var text) && text.Length > 0 ? text : id;
            result[floorId] = new MapBossGate(gateId, fee, difficultyKey, label);
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> LoadForceManTextAsync(
        SqliteConnection connection,
        string language,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lang in new[] { "int", language }.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, txt FROM master_text WHERE sct = '4FORCEMEN' AND lang = $lang;";
            command.Parameters.AddWithValue("$lang", lang);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Text(reader, 0);
                var text = Text(reader, 1);
                if (id.Length == 0 || text.Length == 0) continue;
                result[id] = text;
            }
        }

        return result;
    }

    private static bool HasColumns(IReadOnlyDictionary<string, HashSet<string>> tables, string table, params string[] columns) =>
        tables.TryGetValue(table, out var available) && columns.All(available.Contains);

    private static string Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty;

    /// <summary>Per-area unit lists plus the area classifications derived from them.</summary>
    private sealed record AreaKinds(
        IReadOnlyDictionary<string, IReadOnlyList<string>> UnitsByArea,
        IReadOnlySet<string> ForceManAreas,
        IReadOnlySet<string> BossArenaAreas)
    {
        public static readonly AreaKinds Empty = new(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }
}

/// <summary>Boss facts resolved for one map node.</summary>
internal readonly record struct MapBossInfo(
    int MainBossMin,
    int MainBossMax,
    IReadOnlyList<MapBossType> MainBossTypes,
    bool IsForceManRoom,
    MapBossGate? ForceManGate,
    bool IsBossArena,
    MapBossType? ArenaBoss);
