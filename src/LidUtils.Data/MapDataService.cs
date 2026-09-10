using LidUtils.Core;
using Microsoft.Data.Sqlite;

namespace LidUtils.Data;

/// <summary>
/// Reads the tower map graph for the five main rotations plus the term calendar from
/// masters.db. Every access is read-only; the game never needs to be closed for this view.
/// </summary>
public sealed class MapDataService : IMapDataService
{
    private const string EnglishLanguage = "int";

    public async Task<TowerMapLoadResult> LoadAsync(
        string databasePath,
        string? language = null,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return new TowerMapLoadResult([], "4HMA", default, default, [], [],
                ["The masters database path does not exist; the tower map is unavailable."]);
        }

        try
        {
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
            var tables = await LoadTablesAsync(connection, cancellationToken);
            if (!HasColumns(tables, "master_area_connect_node", "flrid", "areaid", "isdef", "ofsx", "stgid") ||
                !HasColumns(tables, "master_area_connect_escalator", "flrid", "areaid", "toflr", "toarea", "dir") ||
                !HasColumns(tables, "master_area_template_term", "expires", "tmplid"))
            {
                return new TowerMapLoadResult([], "4HMA", default, default, [], [],
                ["The masters database is missing the tower map tables (master_area_connect_node / master_area_connect_escalator / master_area_template_term)."]);
            }

            var hasFloorTable = HasColumns(tables, "master_floor", "id", "areaid", "no", "name");
            if (!hasFloorTable)
                warnings.Add("Table 'master_floor' is missing or incomplete; area names and floor numbers will fall back to slot ids.");
            var hasTextTable = HasColumns(tables, "master_text", "sct", "id", "lang", "txt");
            if (!hasTextTable)
                warnings.Add("Table 'master_text' is missing or incomplete; area name keys are shown instead of localized names.");
            var hasElevatorTables = HasColumns(tables, "master_elevator_stop_floor", "id", "elvid") &&
                HasColumns(tables, "master_elevator", "id", "name");
            if (!hasElevatorTables)
                warnings.Add("The elevator tables are missing or incomplete; elevator service will not be shown.");

            var requestedLanguage = string.IsNullOrWhiteSpace(language) ? EnglishLanguage : language.Trim();
            var areaNames = hasTextTable
                ? await LoadAreaNamesAsync(connection, requestedLanguage, cancellationToken)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var floorInfo = hasFloorTable
                ? await LoadFloorInfoAsync(connection, cancellationToken)
                : new Dictionary<string, (int FloorNumber, string NameKey)>();
            var elevatorStops = hasElevatorTables
                ? await LoadElevatorStopsAsync(connection, cancellationToken)
                : new Dictionary<string, (string CarId, string CarLabel)>(StringComparer.Ordinal);
            var bossCatalog = await MapBossCatalog.LoadAsync(connection, tables, requestedLanguage, cancellationToken);

            var templates = new List<TowerMapTemplate>(TowerMapCatalog.TemplateIds.Count);
            foreach (var templateId in TowerMapCatalog.TemplateIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nodes = await LoadNodesAsync(connection, templateId, floorInfo, areaNames, elevatorStops, bossCatalog, cancellationToken);
                var edges = await LoadEdgesAsync(connection, templateId, nodes, cancellationToken);
                templates.Add(new TowerMapTemplate(templateId, nodes, edges));
            }

            var terms = await LoadTermsAsync(connection, cancellationToken);
            var active = ResolveActiveTerm(terms, nowUtc ?? DateTimeOffset.UtcNow);
            return new TowerMapLoadResult(
                templates,
                active.TemplateId,
                active.StartUtc,
                active.ExpiresUtc,
                terms.Skip(Math.Max(0, terms.Count - 4)).ToArray(),
                terms.Count == 0 ? [] : terms.SkipWhile(term => term.ExpiresUtc <= active.ExpiresUtc).Take(4).ToArray(),
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new TowerMapLoadResult([], "4HMA", default, default, [], [],
                [$"The tower map could not be read: {exception.Message}"]);
        }
    }

    private static async Task<IReadOnlyList<MapNode>> LoadNodesAsync(
        SqliteConnection connection,
        string templateId,
        IReadOnlyDictionary<string, (int FloorNumber, string NameKey)> floorInfo,
        IReadOnlyDictionary<string, string> areaNames,
        IReadOnlyDictionary<string, (string CarId, string CarLabel)> elevatorStops,
        MapBossCatalog bossCatalog,
        CancellationToken cancellationToken)
    {
        var nodes = new List<MapNode>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT flrid, areaid, elvflrid, isdef, ofsx, stgid FROM master_area_connect_node " +
            "WHERE id = $template ORDER BY flrid, areaid;";
        command.Parameters.AddWithValue("$template", templateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var floorId = Text(reader, 0);
            var areaId = Text(reader, 1);
            if (string.IsNullOrWhiteSpace(floorId)) continue;
            var stageId = Text(reader, 5);
            if (!TowerMapCatalog.Stages.Any(stage => stage.StageId == stageId)) continue;

            var nodeKey = floorId + "\x1f" + areaId;
            var isHead = string.Equals(floorId, TowerMapCatalog.HeadFloorId, StringComparison.Ordinal);
            var floorNumber = floorInfo.TryGetValue(nodeKey, out var info)
                ? info.FloorNumber
                : TowerMapCatalog.FloorNumberFor(floorId) ?? 0;
            var nameKey = floorInfo.TryGetValue(nodeKey, out info) ? info.NameKey : string.Empty;
            var stopId = isHead ? TowerMapCatalog.WaitingRoomStopId : Text(reader, 2);
            var car = ResolveElevatorCar(stopId, elevatorStops, isHead);
            var boss = bossCatalog.Resolve(stageId, floorId, areaId);
            var node = new MapNode(
                templateId,
                stageId,
                floorId,
                floorNumber,
                areaId,
                nameKey,
                ResolveAreaName(nameKey, areaId, areaNames),
                reader.GetInt32(3) != 0,
                stopId,
                car.CarId,
                car.CarLabel,
                reader.IsDBNull(4) ? 0d : reader.GetDouble(4))
            {
                MainBossMin = boss.MainBossMin,
                MainBossMax = boss.MainBossMax,
                MainBossTypes = boss.MainBossTypes,
                IsForceManRoom = boss.IsForceManRoom,
                ForceManGate = boss.ForceManGate,
                IsBossArena = boss.IsBossArena,
                ArenaBoss = boss.ArenaBoss
            };
            nodes.Add(node);
        }

        return nodes;
    }

    private static (string CarId, string CarLabel) ResolveElevatorCar(
        string stopId,
        IReadOnlyDictionary<string, (string CarId, string CarLabel)> elevatorStops,
        bool isWaitingRoom)
    {
        if (elevatorStops.TryGetValue(stopId, out var resolved)) return resolved;
        if (isWaitingRoom)
        {
            // The Waiting Room is not a floor stop row; it belongs to the main elevator car.
            return elevatorStops.TryGetValue(TowerMapCatalog.MainElevatorCarId + "_MET_FLR_01", out var fallback)
                ? fallback
                : (TowerMapCatalog.MainElevatorCarId, "MAIN ELEVATOR");
        }

        return (string.Empty, string.Empty);
    }

    private static async Task<IReadOnlyList<MapEdge>> LoadEdgesAsync(
        SqliteConnection connection,
        string templateId,
        IReadOnlyList<MapNode> nodes,
        CancellationToken cancellationToken)
    {
        var nodeKeys = new HashSet<string>(nodes.Select(node => node.Key), StringComparer.Ordinal);
        var edges = new List<MapEdge>();
        var seenPairs = new HashSet<(string Source, string Target)>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT flrid, areaid, toflr, toarea, ci, key, gate FROM master_area_connect_escalator " +
            "WHERE id = $template AND dir = 0 AND toflr <> '' ORDER BY flrid, areaid, toflr, toarea;";
        command.Parameters.AddWithValue("$template", templateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var floorId = Text(reader, 0);
            var areaId = Text(reader, 1);
            var toFloorId = Text(reader, 2);
            var toAreaId = Text(reader, 3);
            if (string.IsNullOrWhiteSpace(toFloorId)) continue;
            var sourceKey = floorId + "/" + areaId;
            var targetKey = toFloorId + "/" + toAreaId;
            if (!nodeKeys.Contains(sourceKey) || !nodeKeys.Contains(targetKey)) continue;
            if (!seenPairs.Add((sourceKey, targetKey))) continue;
            edges.Add(new MapEdge(
                templateId,
                floorId,
                areaId,
                toFloorId,
                toAreaId,
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Text(reader, 5),
                Text(reader, 6)));
        }

        return edges;
    }

    private static async Task<Dictionary<string, (int FloorNumber, string NameKey)>> LoadFloorInfoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (int, string)>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, areaid, no, name FROM master_floor;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Text(reader, 0);
            if (string.IsNullOrWhiteSpace(id)) continue;
            result[id + "\x1f" + Text(reader, 1)] = (
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                Text(reader, 3));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> LoadAreaNamesAsync(
        SqliteConnection connection,
        string language,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lang in new[] { EnglishLanguage, language }.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, txt FROM master_text WHERE sct = 'AREA_NAME' AND lang = $lang;";
            command.Parameters.AddWithValue("$lang", lang);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Text(reader, 0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var text = Text(reader, 1);
                if (text.Length == 0) continue;
                result[id] = text; // the requested language overrides English when both exist
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, (string CarId, string CarLabel)>> LoadElevatorStopsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var stopToCar = new Dictionary<string, string>(StringComparer.Ordinal);
        var carToLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, elvid FROM master_elevator_stop_floor;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stopId = Text(reader, 0);
                if (string.IsNullOrWhiteSpace(stopId)) continue;
                stopToCar[stopId] = Text(reader, 1);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name FROM master_elevator;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var carId = Text(reader, 0);
                if (string.IsNullOrWhiteSpace(carId)) continue;
                carToLabel[carId] = Text(reader, 1);
            }
        }

        return stopToCar.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var label = carToLabel.TryGetValue(pair.Value, out var value) && value.Length > 0
                    ? value
                    : pair.Value;
                return (pair.Value, label);
            },
            StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<TowerTermEntry>> LoadTermsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var terms = new List<TowerTermEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT expires, tmplid FROM master_area_template_term ORDER BY expires;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            var expires = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0));
            var templateId = Text(reader, 1);
            if (templateId.Length == 0) continue;
            terms.Add(new TowerTermEntry(templateId, expires));
        }

        return terms;
    }

    private static async Task<IReadOnlyDictionary<string, HashSet<string>>> LoadTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = Text(reader, 0);
            if (table.Length == 0) continue;
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = "SELECT name FROM pragma_table_info($table);";
            infoCommand.Parameters.AddWithValue("$table", table);
            await using var infoReader = await infoCommand.ExecuteReaderAsync(cancellationToken);
            while (await infoReader.ReadAsync(cancellationToken))
                columns.Add(Text(infoReader, 0));
            result[table] = columns;
        }

        return result;
    }

    private static (string TemplateId, DateTimeOffset StartUtc, DateTimeOffset ExpiresUtc) ResolveActiveTerm(
        IReadOnlyList<TowerTermEntry> terms,
        DateTimeOffset nowUtc)
    {
        if (terms.Count == 0) return ("4HMA", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        for (var index = 0; index < terms.Count; index++)
        {
            if (terms[index].ExpiresUtc <= nowUtc) continue;
            var start = index == 0 ? DateTimeOffset.UnixEpoch : terms[index - 1].ExpiresUtc;
            return (terms[index].TemplateId, start, terms[index].ExpiresUtc);
        }

        // The calendar ends in the past (unusual); treat the last term as the active one.
        var last = terms[^1];
        var lastStart = terms.Count == 1 ? DateTimeOffset.UnixEpoch : terms[^2].ExpiresUtc;
        return (last.TemplateId, lastStart, last.ExpiresUtc);
    }

    private static string ResolveAreaName(string nameKey, string areaId, IReadOnlyDictionary<string, string> areaNames)
    {
        if (string.IsNullOrWhiteSpace(nameKey)) return string.Empty;
        var dot = nameKey.IndexOf('.');
        if (dot <= 0 || dot == nameKey.Length - 1) return string.Empty;
        var id = nameKey[(dot + 1)..];
        return areaNames.TryGetValue(id, out var name) ? name : string.Empty;
    }

    private static bool HasColumns(IReadOnlyDictionary<string, HashSet<string>> tables, string table, params string[] columns) =>
        tables.TryGetValue(table, out var available) && columns.All(available.Contains);

    private static string Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty;

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
