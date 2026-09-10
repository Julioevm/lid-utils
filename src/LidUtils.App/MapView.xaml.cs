using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LidUtils.Core;

namespace LidUtils.App;

/// <summary>
/// Draws the layout produced by MapViewModel on a canvas: floor rows, connection arrows and
/// area chips. Handles drag-to-pan, click-to-select and the initial scroll down to F1.
/// </summary>
public partial class MapView : UserControl
{
    private static readonly Brush RowLineBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35));
    private static readonly Brush GutterTextBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
    private static readonly Brush EdgeBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush HeadEdgeBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x72));
    private static readonly Brush GatedEdgeBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));
    private static readonly Brush ElevatorRingBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xC3, 0x6A));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Colors.White);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xC2, 0xCF));

    // Boss overlay: section boss arena (red), roaming section boss (purple), Four Force Men gates (gold).
    private static readonly Brush BossBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0x58, 0x7A));
    private static readonly Brush MainBossBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x7B, 0xD9));
    private static readonly Brush BossTriggerBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x4A));
    private static readonly Brush BossBadgeBackground = new SolidColorBrush(Color.FromRgb(0x21, 0x18, 0x24));
    private static readonly Brush BossBadgeTextBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xDD, 0xEA));

    // Community area-information badge drawn below a node (materials + marker glyphs).
    private static readonly Brush LootBadgeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x23));
    private static readonly Brush LootBadgeBorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x55));
    private static readonly Brush LootBadgeTextBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xC2, 0xCF));

    // One palette slot per elevator service (car). Slot 0 is always the main tower elevator.
    private static readonly SolidColorBrush[] ElevatorPalette =
    [
        new(Color.FromRgb(0xF0, 0xC2, 0x4B)), // gold       – main elevator
        new(Color.FromRgb(0x5F, 0xA8, 0xF0)), // blue
        new(Color.FromRgb(0x5F, 0xD0, 0x8A)), // green
        new(Color.FromRgb(0xE2, 0x87, 0xDA)), // pink
        new(Color.FromRgb(0xFF, 0x9B, 0x57)), // orange
        new(Color.FromRgb(0x4E, 0xC9, 0xC0)), // teal
        new(Color.FromRgb(0xB8, 0x9B, 0xF0)), // lilac
        new(Color.FromRgb(0xEF, 0x7A, 0x7A))  // salmon
    ];

    private static Brush ElevatorBrush(int colorIndex)
    {
        if (colorIndex >= 0 && colorIndex < ElevatorPalette.Length) return ElevatorPalette[colorIndex];
        return ElevatorRingBrush;
    }

    private readonly Dictionary<string, Brush> _stageBrushes = new(StringComparer.Ordinal)
    {
        ["S_MET"] = new SolidColorBrush(Color.FromRgb(0x5C, 0xB0, 0xFF)),
        ["S_ARC"] = new SolidColorBrush(Color.FromRgb(0x63, 0xC7, 0x63)),
        ["S_AMS"] = new SolidColorBrush(Color.FromRgb(0xC4, 0x7B, 0xD9)),
        ["S_RFT"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x4A)),
        ["S_HZM"] = new SolidColorBrush(Color.FromRgb(0xE4, 0x58, 0x7A)),
        ["S_LAS"] = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4A)),
        ["HEAD"] = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB8))
    };

    private Point _panStart;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private bool _isPanning;
    private bool _hadData;
    private MapViewModel? _viewModel;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MapViewModel? ViewModel => DataContext as MapViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = ViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _hadData = false;
        Redraw();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.GeometryVersion)) Redraw();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Redraw();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnTodayClicked(object sender, RoutedEventArgs e) => ViewModel?.SelectTodayTemplate();

    private void Redraw()
    {
        var vm = ViewModel;
        var canvas = MapCanvas;
        canvas.Children.Clear();
        if (vm is null)
        {
            return;
        }

        canvas.Width = vm.ContentWidth;
        canvas.Height = vm.ContentHeight;

        foreach (var row in vm.Rows)
        {
            DrawRow(row.Y, row.Label, canvas);
        }

        foreach (var edge in vm.Edges)
        {
            DrawEdge(edge, canvas);
        }

        foreach (var segment in vm.ElevatorSegments)
        {
            DrawElevatorSegment(segment, canvas);
        }

        var selectedKey = vm.SelectedArea?.Key;
        foreach (var node in vm.Nodes)
        {
            DrawNode(node, isSelected: node.Key == selectedKey, canvas);
        }

        var wantsInitialScroll = !_hadData && vm.HasData && vm.Rows.Count > 0;
        _hadData = vm.HasData;
        if (wantsInitialScroll)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                MapScrollViewer.UpdateLayout();
                MapScrollViewer.ScrollToVerticalOffset(MapScrollViewer.ScrollableHeight);
            }));
        }
    }

    private void DrawRow(double y, string label, Canvas canvas)
    {
        var line = new Line
        {
            X1 = 0,
            X2 = canvas.Width,
            Y1 = y,
            Y2 = y,
            Stroke = RowLineBrush,
            StrokeThickness = 1
        };
        canvas.Children.Add(line);

        var text = new TextBlock
        {
            Text = label,
            Width = MapViewModel.GutterWidth - 18,
            FontSize = 10.5,
            Foreground = GutterTextBrush,
            TextAlignment = TextAlignment.Right,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(text, 6);
        Canvas.SetTop(text, y - 8);
        canvas.Children.Add(text);
    }

    private void DrawElevatorSegment(MapElevatorSegmentItem segment, Canvas canvas)
    {
        var brush = ElevatorBrush(segment.ColorIndex);
        var line = new Line
        {
            X1 = segment.X1,
            Y1 = segment.Y1,
            X2 = segment.X2,
            Y2 = segment.Y2,
            Stroke = brush,
            StrokeThickness = 1.6,
            Opacity = 0.85
        };
        ToolTipService.SetToolTip(line, segment.ToolTipText);
        canvas.Children.Add(line);
    }

    private void DrawEdge(MapEdgeItem edge, Canvas canvas)
    {
        var kind = edge.BossRoute.Kind;
        var brush = kind switch
        {
            MapBossRouteKind.BossArenaClear => BossBrush,
            MapBossRouteKind.BossClear => MainBossBrush,
            MapBossRouteKind.BossTrigger => BossTriggerBrush,
            _ => edge.IsHeadEdge ? HeadEdgeBrush : edge.IsGated ? GatedEdgeBrush : EdgeBrush
        };
        var thickness = kind switch
        {
            MapBossRouteKind.BossArenaClear => 2.0,
            MapBossRouteKind.BossClear => 1.8,
            MapBossRouteKind.BossTrigger => 1.5,
            _ => edge.IsHeadEdge ? 1.4 : edge.IsGated ? 1.2 : 1.6
        };
        var line = new Line
        {
            X1 = edge.X1,
            Y1 = edge.Y1,
            X2 = edge.X2,
            Y2 = edge.Y2,
            Stroke = brush,
            StrokeThickness = thickness
        };
        DoubleCollection? dash = kind switch
        {
            MapBossRouteKind.BossClear => new DoubleCollection { 5.0, 2.0 },
            MapBossRouteKind.BossTrigger => new DoubleCollection { 2.0, 2.0 },
            _ => edge.IsGated ? new DoubleCollection { 3.0, 2.5 } : null
        };
        if (dash is not null) line.StrokeDashArray = dash;
        ToolTipService.SetToolTip(line, edge.ToolTipText);
        canvas.Children.Add(line);
        DrawArrowHead(edge.X1, edge.Y1, edge.X2, edge.Y2, brush, canvas);
    }

    private static void DrawArrowHead(double x1, double y1, double x2, double y2, Brush brush, Canvas canvas)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 8d) return;
        var ux = dx / length;
        var uy = dy / length;
        const double size = 6.5d;
        const double back = 9d;
        var baseX = x2 - ux * back;
        var baseY = y2 - uy * back;
        var px = -uy * size;
        var py = ux * size;
        var polygon = new Polygon
        {
            Points = new PointCollection
            {
                new(x2, y2),
                new(baseX + px, baseY + py),
                new(baseX - px, baseY - py)
            },
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 0.5
        };
        canvas.Children.Add(polygon);
    }

    private void DrawNode(MapNodeItem node, bool isSelected, Canvas canvas)
    {
        if (node.IsHead)
        {
            var head = new Border
            {
                Width = 92,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = _stageBrushes["HEAD"],
                Child = new TextBlock
                {
                    Text = TowerMapCatalog.WaitingRoomName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 9.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTipService.SetToolTip(head, node.ToolTipText);
            Canvas.SetLeft(head, node.X - 46);
            Canvas.SetTop(head, node.Y - 10);
            canvas.Children.Add(head);
            return;
        }

        var brush = StageBrush(node.StageId);
        var diameter = node.Radius * 2;
        if (node.Node.IsBase)
        {
            var dot = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = brush,
                Stroke = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF5)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(dot, node.X - node.Radius);
            Canvas.SetTop(dot, node.Y - node.Radius);
            AttachChip(dot, node, canvas);
        }
        else
        {
            var path = new Path
            {
                Data = new StreamGeometry(),
                Stroke = brush,
                StrokeThickness = 1.6,
                Fill = new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xE0, 0xE0))
            };
            if (path.Data is StreamGeometry geometry)
            {
                using var context = geometry.Open();
                context.BeginFigure(new Point(node.X, node.Y - node.Radius), true, true);
                context.LineTo(new Point(node.X + node.Radius, node.Y), false, false);
                context.LineTo(new Point(node.X, node.Y + node.Radius), false, false);
                context.LineTo(new Point(node.X - node.Radius, node.Y), false, false);
                context.Close();
            }

            AttachChip(path, node, canvas);
        }

        if (node.HasElevator)
        {
            var ring = new Ellipse
            {
                Width = diameter + 6,
                Height = diameter + 6,
                Stroke = node.ElevatorColorIndex >= 0 ? ElevatorBrush(node.ElevatorColorIndex) : ElevatorRingBrush,
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ring, node.X - node.Radius - 3);
            Canvas.SetTop(ring, node.Y - node.Radius - 3);
            canvas.Children.Add(ring);
        }

        if (isSelected)
        {
            var ring = new Ellipse
            {
                Width = diameter + 10,
                Height = diameter + 10,
                Stroke = SelectionBrush,
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ring, node.X - node.Radius - 5);
            Canvas.SetTop(ring, node.Y - node.Radius - 5);
            canvas.Children.Add(ring);
        }

        if (node.BossBadge.Length > 0 && (ViewModel?.ShowBossInfo ?? true))
        {
            DrawBossBadge(node, canvas);
        }

        if (node.HasLoot && (ViewModel?.ShowLootInfo ?? true))
        {
            DrawLootBadge(node, canvas);
        }

        if (node.ShowLabel)
        {
            var label = new TextBlock
            {
                Text = node.LabelText,
                FontSize = 9.5,
                Foreground = LabelBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, node.X + node.Radius + 5);
            Canvas.SetTop(label, node.Y - 7);
            canvas.Children.Add(label);
        }
    }

    private void AttachChip(Shape shape, MapNodeItem node, Canvas canvas)
    {
        shape.Tag = node.Key;
        shape.ToolTip = node.ToolTipText;
        shape.Cursor = Cursors.Hand;
        shape.MouseLeftButtonDown += OnChipMouseLeftButtonDown;
        canvas.Children.Add(shape);
    }

    /// <summary>Small boss marker drawn above a node: BOSS and/or FFM.</summary>
    private static void DrawBossBadge(MapNodeItem node, Canvas canvas)
    {
        const double width = 60d;
        var border = node.HasBossArena ? BossBrush : ElevatorRingBrush;
        var badge = new Border
        {
            Width = width,
            Height = 13d,
            CornerRadius = new CornerRadius(6),
            Background = BossBadgeBackground,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = node.BossBadge,
                FontSize = 8d,
                FontWeight = FontWeights.SemiBold,
                Foreground = BossBadgeTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTipService.SetToolTip(badge, node.ToolTipText);
        Canvas.SetLeft(badge, node.X - width / 2d);
        Canvas.SetTop(badge, node.Y - node.Radius - 16d);
        canvas.Children.Add(badge);
    }

    /// <summary>Community loot marker drawn below a node: material name plus marker glyphs.</summary>
    private static void DrawLootBadge(MapNodeItem node, Canvas canvas)
    {
        var width = Math.Max(26d, node.LootBadge.Length * 5.2d + 10d);
        var badge = new Border
        {
            Width = width,
            Height = 12d,
            CornerRadius = new CornerRadius(6),
            Background = LootBadgeBackground,
            BorderBrush = LootBadgeBorderBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = node.LootBadge,
                FontSize = 7.5d,
                Foreground = LootBadgeTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTipService.SetToolTip(badge, node.ToolTipText);
        Canvas.SetLeft(badge, node.X - width / 2d);
        Canvas.SetTop(badge, node.Y + node.Radius + 2d);
        canvas.Children.Add(badge);
    }

    private void OnChipMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Shape { Tag: string key })
        {
            var row = ViewModel?.AreaRows.FirstOrDefault(candidate => candidate.Key == key);
            if (row is not null) ViewModel!.SelectArea(row);
        }

        e.Handled = true;
    }

    private Brush StageBrush(string stageId) =>
        _stageBrushes.TryGetValue(stageId, out var brush) ? brush : EdgeBrush;

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(MapScrollViewer);
        _panStartOffsetX = MapScrollViewer.HorizontalOffset;
        _panStartOffsetY = MapScrollViewer.VerticalOffset;
        MapCanvas.CaptureMouse();
        MapCanvas.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var current = e.GetPosition(MapScrollViewer);
        MapScrollViewer.ScrollToHorizontalOffset(_panStartOffsetX + (_panStart.X - current.X));
        MapScrollViewer.ScrollToVerticalOffset(_panStartOffsetY + (_panStart.Y - current.Y));
        e.Handled = true;
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        MapCanvas.ReleaseMouseCapture();
        MapCanvas.Cursor = null;
        e.Handled = true;
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        MapCanvas.ReleaseMouseCapture();
        MapCanvas.Cursor = null;
    }
}
