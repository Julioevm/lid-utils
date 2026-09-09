using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

/// <summary>
/// View model for the read-only tower map under Game Database → Map. It holds the loaded
/// rotations, the template/band selection, and the pre-computed screen layout (nodes, edges,
/// rows) that the MapView control draws. All geometry work stays in plain numbers so the layout
/// is unit-testable without a WPF dispatcher.
/// </summary>
public sealed class MapViewModel : INotifyPropertyChanged
{
    public const string WholeTowerBandKey = "all";

    private readonly IMapDataService? _service;
    private TowerMapLoadResult? _data;
    private string _selectedTemplate = "4HMA";
    private MapBandOption? _selectedBand;
    private MapAreaRow? _selectedArea;
    private string _statusTitle = "Validate masters.db to view the tower map.";
    private string _statusDetails = string.Empty;
    private string _rotationSummary = string.Empty;
    private bool _isLoading;
    private int _geometryVersion;
    private bool _showAreaLabels;

    public MapViewModel(IMapDataService? service = null)
    {
        _service = service;
        _selectedBand = BandOptions[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Vertical pixels reserved for one floor row.</summary>
    public const double RowHeight = 40d;

    /// <summary>Horizontal gutter (px) holding the floor-number labels.</summary>
    public const double GutterWidth = 92d;

    /// <summary>Pixels per ofsx unit when laying out the map's horizontal axis.</summary>
    public const double OfsScale = 0.22d;

    /// <summary>Minimum centre-to-centre distance between dots sharing one floor row.</summary>
    public const double MinimumSameRowGap = 26d;

    private const double TopPad = 30d;
    private const double BottomPad = 26d;
    private const double HeadGap = 30d;

    public static IReadOnlyList<string> TemplateOptions { get; } = TowerMapCatalog.TemplateIds;

    public static IReadOnlyList<MapBandOption> BandOptions { get; } =
    [
        new(WholeTowerBandKey, "Whole tower · F1–F50", ["S_MET", "S_ARC", "S_AMS", "S_RFT", "S_HZM", "S_LAS"], IncludeHead: true),
        new("S_MET", "Metro · F1–F10", ["S_MET"], IncludeHead: true),
        new("S_ARC", "Arcade · F11–F20", ["S_ARC"], IncludeHead: false),
        new("S_AMS", "Amusement · F21–F30", ["S_AMS"], IncludeHead: false),
        new("S_RFT", "Rooftop · F31–F40", ["S_RFT"], IncludeHead: false),
        new("S_HZM", "Hazama · F41–F50", ["S_HZM"], IncludeHead: false)
    ];

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetField(ref _statusTitle, value);
    }

    public string StatusDetails
    {
        get => _statusDetails;
        private set => SetField(ref _statusDetails, value);
    }

    public string RotationSummary
    {
        get => _rotationSummary;
        private set => SetField(ref _rotationSummary, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool HasData => _data is not null &&
        _data.FindTemplate(SelectedTemplate) is { Nodes.Count: > 0 };

    public string SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetField(ref _selectedTemplate, value)) return;
            SelectedArea = null;
            Rebuild();
        }
    }

    public MapBandOption SelectedBand
    {
        get => _selectedBand!;
        set
        {
            if (value is null || !SetField(ref _selectedBand, value)) return;
            SelectedArea = null;
            Rebuild();
        }
    }

    public bool ShowAreaLabels
    {
        get => _showAreaLabels;
        set
        {
            if (!SetField(ref _showAreaLabels, value)) return;
            Rebuild();
        }
    }

    public MapAreaRow? SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (!SetField(ref _selectedArea, value)) return;
            RaiseLayoutChanged();
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedDetailTitle));
            OnPropertyChanged(nameof(SelectedDetails));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedArea is not null;
    public string SelectedName => SelectedArea is null ? "No area selected" : SelectedArea.Name;
    public string SelectedDetailTitle => SelectedArea is null
        ? "No area selected"
        : $"{SelectedArea.Name} · Floor {SelectedArea.Node.FloorNumber}";
    public string SelectedDetails => SelectedArea is null
        ? "Click an area dot on the map or a row in the list to inspect it."
        : BuildDetails(SelectedArea.Node);

    public string ActiveTemplateSummary => HasData
        ? $"Rotation {_data!.ActiveTemplateId} is live until {FormatDate(_data.ActiveTermExpiresUtc)}." +
          (_data.UpcomingTerms.Count == 0
              ? string.Empty
              : $" Next: {string.Join(" → ", _data.UpcomingTerms.Select(term => term.TemplateId))}")
        : string.Empty;

    /// <summary>Monotonic version of the canvas layout; the view redraws when it changes.</summary>
    public int GeometryVersion => _geometryVersion;

    public string NodeCountText => Nodes.Count == 0
        ? "No areas in view."
        : $"{Nodes.Count:N0} area(s) · {Edges.Count:N0} connection(s)";
    public string EdgeCountText => Edges.Count.ToString("N0");

    public IReadOnlyList<MapNodeItem> Nodes { get; private set; } = [];
    public IReadOnlyList<MapEdgeItem> Edges { get; private set; } = [];
    public IReadOnlyList<MapRowItem> Rows { get; private set; } = [];
    public IReadOnlyList<MapAreaRow> AreaRows { get; private set; } = [];
    public IReadOnlyList<MapElevatorSegmentItem> ElevatorSegments { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public double ContentWidth { get; private set; } = 300d;
    public double ContentHeight { get; private set; } = 300d;

    public void SelectArea(MapAreaRow row)
    {
        if (row is not null) SelectedArea = row;
    }

    public void ClearSelection() => SelectedArea = null;

    /// <summary>Selects the rotation the term calendar says is live right now.</summary>
    public void SelectTodayTemplate()
    {
        if (_data is null) return;
        if (TemplateOptions.Contains(_data.ActiveTemplateId, StringComparer.Ordinal))
        {
            SelectedTemplate = _data.ActiveTemplateId;
            StatusTitle = $"Today's rotation: {_data.ActiveTemplateId}";
            StatusDetails = $"Template {_data.ActiveTemplateId} is live until {FormatDate(_data.ActiveTermExpiresUtc)}.";
        }
    }

    /// <summary>Loads the tower data from masters.db (read-only).</summary>
    public async Task LoadAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        Reset();
        if (_service is null)
        {
            StatusTitle = "Map data is unavailable in this build.";
            return;
        }

        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            StatusTitle = "Validate masters.db to view the tower map.";
            return;
        }

        IsLoading = true;
        StatusTitle = "Loading tower map…";
        try
        {
            var result = await _service.LoadAsync(databasePath, cancellationToken: cancellationToken);
            SetResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusTitle = "Map loading cancelled";
            StatusDetails = string.Empty;
        }
        catch (Exception exception)
        {
            StatusTitle = "Map could not be loaded";
            StatusDetails = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Installs an already loaded result (used when the database is validated).</summary>
    public void SetResult(TowerMapLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _data = result;
        Warnings = result.Warnings;
        _selectedTemplate = TemplateOptions.Contains(result.ActiveTemplateId, StringComparer.Ordinal)
            ? result.ActiveTemplateId
            : result.Templates.Count > 0
                ? result.Templates[0].Id
                : SelectedTemplate;

        var rotated = _data.FindTemplate(_selectedTemplate);
        StatusTitle = "Tower map ready";
        StatusDetails = rotated is { Nodes.Count: > 0 }
            ? $"{rotated.Nodes.Count:N0} area(s) and {rotated.Edges.Count:N0} connection(s) in rotation {rotated.Id}."
            : "No floor areas were found for the selected rotation.";
        RotationSummary = ActiveTemplateSummary;
        SelectedArea = null;
        OnPropertyChanged(nameof(SelectedTemplate));
        OnPropertyChanged(nameof(RotationSummary));
        Rebuild();
    }

    /// <summary>Clears the loaded map and returns to the empty state.</summary>
    public void Reset()
    {
        _data = null;
        Warnings = [];
        StatusTitle = "Validate masters.db to view the tower map.";
        StatusDetails = string.Empty;
        RotationSummary = string.Empty;
        _selectedTemplate = "4HMA";
        SelectedArea = null;
        Nodes = [];
        Edges = [];
        Rows = [];
        AreaRows = [];
        ElevatorSegments = [];
        ContentWidth = 300d;
        ContentHeight = 300d;
        OnPropertyChanged(nameof(SelectedTemplate));
        RaiseLayoutChanged();
    }

    private void Rebuild()
    {
        var data = _data;
        var template = data?.FindTemplate(SelectedTemplate);
        if (data is null || template is null || template.Nodes.Count == 0)
        {
            Nodes = [];
            Edges = [];
            Rows = [];
            AreaRows = [];
            ElevatorSegments = [];
            ContentWidth = 300d;
            ContentHeight = 300d;
            RaiseLayoutChanged();
            return;
        }

        var band = SelectedBand;
        var visible = template.Nodes
            .Where(node => BandIncludes(band, node))
            .ToArray();
        var areaNodes = visible.Where(node => !node.IsHead).ToArray();
        var floors = areaNodes.Select(node => node.FloorNumber).Where(floor => floor > 0).Distinct().Order().ToArray();
        var maxFloor = floors.Length == 0 ? 1 : floors[^1];
        var minFloor = floors.Length == 0 ? 1 : floors[0];
        var headNode = visible.FirstOrDefault(node => node.IsHead);

        // Each elevator car gets its own palette slot so routes are visually distinct.
        var colorByCar = visible
            .Select(node => node.ElevatorCarId)
            .Where(carId => !string.IsNullOrWhiteSpace(carId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(carId => string.Equals(carId, TowerMapCatalog.MainElevatorCarId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(carId => carId, StringComparer.Ordinal)
            .Select((carId, index) => (carId, index))
            .ToDictionary(pair => pair.carId, pair => pair.index, StringComparer.Ordinal);

        var rawMinimum = areaNodes.Length == 0 ? 0d : areaNodes.Min(node => node.OfsX * OfsScale);
        var shift = GutterWidth + 28d - rawMinimum;

        // Horizontal axis: settle every row independently. Chips keep their ofsx order but are
        // spread to a guaranteed minimum distance so dots on one floor never overlap. When area
        // labels are shown each label sits to the right of its dot, so every pair on a row is
        // also pushed far enough apart for the left dot's name to clear the next dot.
        var settled = new Dictionary<MapNode, double>();
        foreach (var group in areaNodes.GroupBy(node => node.FloorNumber))
        {
            var ordered = group.OrderBy(node => node.OfsX).ToArray();
            var cursor = double.MinValue;
            MapNode? previous = null;
            foreach (var node in ordered)
            {
                var advance = MinimumSameRowGap;
                if (ShowAreaLabels && previous is not null)
                    advance = Math.Max(advance, 30d + EstimateLabelWidth(DisplayName(previous)));
                cursor = Math.Max(cursor + advance, shift + node.OfsX * OfsScale);
                settled[node] = cursor;
                previous = node;
            }
        }

        var nodeItems = new List<MapNodeItem>(visible.Length);
        foreach (var floorGroup in areaNodes.GroupBy(node => node.FloorNumber).OrderBy(group => group.Key))
        {
            var ordered = floorGroup.OrderBy(node => node.OfsX).ToArray();
            foreach (var node in ordered)
            {
                var x = settled[node];
                nodeItems.Add(new MapNodeItem
                {
                    Node = node,
                    X = x,
                    Y = TopPad + (maxFloor - node.FloorNumber) * RowHeight,
                    Radius = node.IsBase ? 7d : 5d,
                    StageId = node.StageId,
                    ToolTipText = BuildToolTip(template, node),
                    LabelText = DisplayName(node),
                    ShowLabel = ShowAreaLabels,
                    ElevatorColorIndex = colorByCar.TryGetValue(node.ElevatorCarId, out var colorIndex) ? colorIndex : -1
                });
            }
        }

        if (headNode is not null)
        {
            var floorOneY = TopPad + (maxFloor - 1) * RowHeight;
            nodeItems.Add(new MapNodeItem
            {
                Node = headNode,
                X = shift,
                Y = floorOneY + RowHeight + HeadGap,
                Radius = 9d,
                StageId = "HEAD",
                ToolTipText = BuildToolTip(template, headNode),
                LabelText = TowerMapCatalog.WaitingRoomName,
                ShowLabel = ShowAreaLabels,
                ElevatorColorIndex = colorByCar.TryGetValue(headNode.ElevatorCarId, out var colorIndex) ? colorIndex : -1
            });
        }

        var byKey = nodeItems.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var edgeItems = new List<MapEdgeItem>(template.Edges.Count);
        foreach (var edge in template.Edges)
        {
            if (!byKey.TryGetValue(edge.Key2, out var source) ||
                !byKey.TryGetValue(edge.TargetKey, out var target) ||
                source == target)
            {
                continue;
            }

            var dx = target.X - source.X;
            var dy = target.Y - source.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1d) continue;
            var directionX = dx / length;
            var directionY = dy / length;
            edgeItems.Add(new MapEdgeItem
            {
                Edge = edge,
                X1 = source.X + directionX * (source.Radius + 2d),
                Y1 = source.Y + directionY * (source.Radius + 2d),
                X2 = target.X - directionX * (target.Radius + 3d),
                Y2 = target.Y - directionY * (target.Radius + 3d),
                IsGated = edge.IsGated,
                IsHeadEdge = source.IsHead,
                ToolTipText = BuildEdgeToolTip(template, edge)
            });
        }

        var elevatorSegments = BuildElevatorSegments(nodeItems);

        var rows = floors.Select(floor => new MapRowItem
        {
            FloorNumber = floor,
            Label = $"F{floor}",
            Y = TopPad + (maxFloor - floor) * RowHeight
        }).ToList();

        var maxX = settled.Values.DefaultIfEmpty(0).Max();
        // When labels are on, keep enough canvas past the right-most dot for the widest
        // label that ends a row.
        var endReserve = 26d;
        if (ShowAreaLabels)
        {
            endReserve = areaNodes
                .GroupBy(node => node.FloorNumber)
                .Select(group => group.OrderBy(node => node.OfsX).Last())
                .Max(node => 12d + EstimateLabelWidth(DisplayName(node)) + 8d);
        }
        var contentWidth = maxX + endReserve;
        var contentHeight = headNode is not null
            ? nodeItems.Single(item => item.IsHead).Y + 30d + BottomPad
            : TopPad + (maxFloor - minFloor) * RowHeight + RowHeight / 2d + BottomPad;

        var areaRows = nodeItems
            .Where(item => !item.IsHead)
            .OrderBy(item => item.Node.FloorNumber)
            .ThenBy(item => item.Node.OfsX)
            .Select(item => ToAreaRow(item.Node))
            .ToArray();

        Nodes = nodeItems;
        Edges = edgeItems;
        Rows = rows;
        AreaRows = areaRows;
        ElevatorSegments = elevatorSegments;
        ContentWidth = Math.Max(contentWidth, GutterWidth + 320d);
        ContentHeight = Math.Max(contentHeight, 200d);
        RaiseLayoutChanged();
    }

    private static IReadOnlyList<MapElevatorSegmentItem> BuildElevatorSegments(IReadOnlyList<MapNodeItem> nodeItems)
    {
        var segments = new List<MapElevatorSegmentItem>();
        foreach (var serviceGroup in nodeItems
                     .Where(item => !string.IsNullOrWhiteSpace(item.ElevatorCarId))
                     .GroupBy(item => item.ElevatorCarId, StringComparer.Ordinal))
        {
            var stops = serviceGroup
                .OrderBy(item => item.Node.FloorNumber)
                .ThenBy(item => item.X)
                .ToArray();
            var first = stops[0];
            for (var index = 0; index < stops.Length - 1; index++)
            {
                var from = stops[index];
                var to = stops[index + 1];
                var dx = to.X - from.X;
                var dy = to.Y - from.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 4d) continue;
                var directionX = dx / length;
                var directionY = dy / length;
                var inset = 6d;
                segments.Add(new MapElevatorSegmentItem
                {
                    CarId = serviceGroup.Key,
                    CarLabel = first.Node.ElevatorCarLabel,
                    ColorIndex = first.ElevatorColorIndex,
                    X1 = from.X + directionX * inset,
                    Y1 = from.Y + directionY * inset,
                    X2 = to.X - directionX * inset,
                    Y2 = to.Y - directionY * inset,
                    ToolTipText = $"{first.Node.ElevatorCarLabel} · {ElevatorStopText(from.Node)} → {ElevatorStopText(to.Node)}"
                });
            }
        }

        return segments;
    }

    private static string ElevatorStopText(MapNode node) =>
        node.IsHead ? TowerMapCatalog.WaitingRoomName : $"F{node.FloorNumber} · {DisplayName(node)}";

    private static bool BandIncludes(MapBandOption band, MapNode node)
    {
        if (string.Equals(node.FloorId, TowerMapCatalog.HeadFloorId, StringComparison.Ordinal))
            return band.IncludeHead;
        return band.StageIds.Contains(node.StageId, StringComparer.Ordinal);
    }

    private static string ShortAreaLabel(string areaId)
    {
        var marker = areaId.LastIndexOf('_');
        return marker >= 0 && marker < areaId.Length - 1 ? areaId[(marker + 1)..] : areaId;
    }

    /// <summary>
    /// Rough pixel width of a 9.5px area label. Used to reserve horizontal space so a label never
    /// collides with the next dot; deliberately overestimates a little.
    /// </summary>
    private static double EstimateLabelWidth(string label) =>
        Math.Max(24d, label.Length * 6.2d + 10d);

    private static MapAreaRow ToAreaRow(MapNode node)
    {
        return new MapAreaRow
        {
            Node = node,
            FloorLabel = $"F{node.FloorNumber}",
            AreaLabel = ShortAreaLabel(node.AreaId),
            Name = DisplayName(node),
            KindLabel = node.IsHead ? TowerMapCatalog.WaitingRoomName : node.IsBase ? "Base area" : "Side area",
            ElevatorLabel = string.IsNullOrWhiteSpace(node.ElevatorCarLabel) ? "—" : node.ElevatorCarLabel,
            OffsetLabel = node.OfsX.ToString("0.#"),
            StageId = node.StageId
        };
    }

    private string BuildToolTip(TowerMapTemplate template, MapNode node)
    {
        if (node.IsHead)
            return $"{TowerMapCatalog.WaitingRoomName}\nThe hub below F1 where every climb starts; the main elevator is docked here.";
        var lines = new List<string>
        {
            DisplayName(node),
            $"{node.AreaId} · Floor {node.FloorNumber} · {StageName(node)}",
            node.IsBase
                ? "Base layout area (present in every rotation)."
                : "Side area (mounted only by this rotation)."
        };
        if (!string.IsNullOrWhiteSpace(node.ElevatorCarLabel))
            lines.Add($"Elevator: {node.ElevatorCarLabel} ({node.ElevatorStopId})");
        var outCount = template.Edges.Count(edge => string.Equals(edge.Key2, node.Key, StringComparison.Ordinal));
        var inCount = template.Edges.Count(edge => string.Equals(edge.TargetKey, node.Key, StringComparison.Ordinal));
        lines.Add($"{outCount:N0} route(s) up · {inCount:N0} route(s) from below");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildEdgeToolTip(TowerMapTemplate template, MapEdge edge)
    {
        var source = template.Nodes.FirstOrDefault(node => string.Equals(node.Key, edge.Key2, StringComparison.Ordinal));
        var target = template.Nodes.FirstOrDefault(node => string.Equals(node.Key, edge.TargetKey, StringComparison.Ordinal));
        var text = source is null || target is null
            ? $"{edge.FloorId} → {edge.ToFloorId}"
            : $"{ElevatorEdgeLabel(source)} → {ElevatorEdgeLabel(target)}";
        var notes = new List<string>();
        if (edge.Ci > 1) notes.Add($"climb span {edge.Ci}");
        if (!string.IsNullOrWhiteSpace(edge.Key)) notes.Add($"key {edge.Key}");
        if (!string.IsNullOrWhiteSpace(edge.Gate)) notes.Add($"gate {edge.Gate}");
        return notes.Count == 0 ? text : text + " · " + string.Join(", ", notes);
    }

    private static string ElevatorEdgeLabel(MapNode node) =>
        node.IsHead ? TowerMapCatalog.WaitingRoomName : $"{DisplayName(node)} (F{node.FloorNumber})";

    private string BuildDetails(MapNode node)
    {
        var template = _data?.FindTemplate(SelectedTemplate);
        if (template is null) return string.Empty;
        var stage = TowerMapCatalog.FindStage(node.StageId);
        var lines = new List<string>
        {
            $"Area id: {node.AreaId}",
            $"Floor: {node.FloorNumber} · {stage?.DisplayName ?? node.StageId}",
            node.IsHead
                ? $"Role: {TowerMapCatalog.WaitingRoomName} (tower head below F1)"
                : node.IsBase
                    ? "Role: base layout area (present in every rotation)"
                    : "Role: template-only side area",
            $"Horizontal offset (map X): {node.OfsX:0.##}"
        };
        lines.Add(string.IsNullOrWhiteSpace(node.ElevatorCarLabel)
            ? "Elevator service: none"
            : $"Elevator service: {node.ElevatorCarLabel} ({node.ElevatorStopId})");

        var outgoing = template.Edges
            .Where(edge => string.Equals(edge.Key2, node.Key, StringComparison.Ordinal))
            .OrderBy(edge => FloorOf(template, edge.TargetKey))
            .ThenBy(edge => edge.ToFloorId, StringComparer.Ordinal)
            .ToArray();
        lines.Add(outgoing.Length == 0
            ? "Routes up: none"
            : $"Routes up ({outgoing.Length:N0}):");
        foreach (var edge in outgoing) lines.Add("  ↑ " + DescribeEdgeTarget(template, edge));

        var incoming = template.Edges
            .Where(edge => string.Equals(edge.TargetKey, node.Key, StringComparison.Ordinal))
            .OrderBy(edge => FloorOf(template, edge.Key2))
            .ToArray();
        lines.Add(incoming.Length == 0
            ? "Routes from below: none"
            : $"Routes from below ({incoming.Length:N0}):");
        foreach (var edge in incoming) lines.Add("  ↓ " + DescribeEdgeSource(template, edge));

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeEdgeTarget(TowerMapTemplate template, MapEdge edge)
    {
        var target = template.Nodes.FirstOrDefault(node => string.Equals(node.Key, edge.TargetKey, StringComparison.Ordinal));
        if (target is null) return $"{edge.ToFloorId}/{edge.ToAreaId}";
        var marker = edge.IsGated ? " · " + GateText(edge) : string.Empty;
        return $"{DisplayName(target)} — F{target.FloorNumber} {target.AreaId}{marker}";
    }

    private static string DescribeEdgeSource(TowerMapTemplate template, MapEdge edge)
    {
        var source = template.Nodes.FirstOrDefault(node => string.Equals(node.Key, edge.Key2, StringComparison.Ordinal));
        if (source is null) return $"{edge.FloorId}/{edge.AreaId}";
        return $"{DisplayName(source)} — F{source.FloorNumber} {source.AreaId}";
    }

    private static string GateText(MapEdge edge)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(edge.Key)) parts.Add($"key {edge.Key}");
        if (!string.IsNullOrWhiteSpace(edge.Gate)) parts.Add($"gate {edge.Gate}");
        return string.Join(", ", parts);
    }

    private static int FloorOf(TowerMapTemplate template, string nodeKey) =>
        template.Nodes.FirstOrDefault(node => string.Equals(node.Key, nodeKey, StringComparison.Ordinal))?.FloorNumber ?? 0;

    private static string DisplayName(MapNode node) =>
        node.IsHead ? TowerMapCatalog.WaitingRoomName : string.IsNullOrWhiteSpace(node.AreaName) ? "(unnamed area)" : node.AreaName;

    private static string StageName(MapNode node) =>
        TowerMapCatalog.FindStage(node.StageId)?.DisplayName ?? node.StageId;

    private static string FormatDate(DateTimeOffset value) =>
        value == default ? "—" : value.ToLocalTime().ToString("ddd d MMM yyyy");

    private void RaiseLayoutChanged()
    {
        _geometryVersion++;
        OnPropertyChanged(nameof(GeometryVersion));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(Edges));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(AreaRows));
        OnPropertyChanged(nameof(ElevatorSegments));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(ContentHeight));
        OnPropertyChanged(nameof(NodeCountText));
        OnPropertyChanged(nameof(EdgeCountText));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
