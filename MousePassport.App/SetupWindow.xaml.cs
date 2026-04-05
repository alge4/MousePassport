using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Text;
using MousePassport.App.Models;
using MousePassport.App.Services;

namespace MousePassport.App;

public partial class SetupWindow : Window
{
    private const double CanvasPadding = 24.0;
    private const double HandleRadius = 10.0;
    private const int DebugAdjacencyTolerancePx = 16;

    private readonly IReadOnlyList<MonitorDescriptor> _monitors;
    private readonly IReadOnlyList<SharedEdge> _edges;
    private readonly Dictionary<string, EdgePort> _ports;
    private readonly Action<IReadOnlyCollection<EdgePort>, EnforcementMode, bool> _onSave;
    private bool _viewReady;

    private readonly List<DrawableEdge> _drawables = [];
    private DragState? _dragState;
    private TransformContext _transform;

    public SetupWindow(
        IReadOnlyList<MonitorDescriptor> monitors,
        IReadOnlyList<SharedEdge> edges,
        IEnumerable<EdgePort> currentPorts,
        EnforcementMode currentMode,
        bool suspendWhenFullscreenForeground,
        Action<IReadOnlyCollection<EdgePort>, EnforcementMode, bool> onSave)
    {
        _monitors = monitors ?? [];
        _edges = edges ?? [];
        _onSave = onSave ?? throw new ArgumentNullException(nameof(onSave));
        _ports = (currentPorts ?? Enumerable.Empty<EdgePort>()).ToDictionary(
            p => p.EdgeId,
            p => new EdgePort { EdgeId = p.EdgeId, PortStart = p.PortStart, PortEnd = p.PortEnd },
            StringComparer.Ordinal);

        EnsurePortsForNewEdges();
        InitializeComponent();
        LegacyHookModeCheckBox.IsChecked = currentMode == EnforcementMode.Hook;
        SuspendFullscreenCheckBox.IsChecked = suspendWhenFullscreenForeground;
        _viewReady = true;
        Loaded += (_, _) => Redraw();
        SizeChanged += (_, _) => Redraw();
    }

    private void EnsurePortsForNewEdges()
    {
        foreach (var edge in _edges)
        {
            if (_ports.ContainsKey(edge.Id))
            {
                continue;
            }

            _ports[edge.Id] = new EdgePort
            {
                EdgeId = edge.Id,
                PortStart = edge.SegmentStart,
                PortEnd = edge.SegmentEnd
            };
        }
    }

    private void Redraw()
    {
        if (!_viewReady)
        {
            return;
        }

        if (_monitors.Count == 0)
        {
            StatusText.Text = "No monitors detected.";
            StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 214, 214));
            return;
        }

        LayoutCanvas.Children.Clear();
        _drawables.Clear();

        _transform = BuildTransform();
        DrawMonitors();
        DrawEdges();
        if (DebugOverlayCheckBox.IsChecked == true)
        {
            DrawDebugOverlayOnCanvas();
        }
        UpdateStatus();
        UpdateDebugPanel();
    }

    private TransformContext BuildTransform()
    {
        var minX = _monitors.Min(m => m.Bounds.Left);
        var minY = _monitors.Min(m => m.Bounds.Top);
        var maxX = _monitors.Max(m => m.Bounds.Right);
        var maxY = _monitors.Max(m => m.Bounds.Bottom);

        var layoutWidth = Math.Max(1, maxX - minX);
        var layoutHeight = Math.Max(1, maxY - minY);

        var targetWidth = Math.Max(1, LayoutCanvas.ActualWidth - (CanvasPadding * 2));
        var targetHeight = Math.Max(1, LayoutCanvas.ActualHeight - (CanvasPadding * 2));
        var scale = Math.Min(targetWidth / layoutWidth, targetHeight / layoutHeight);
        var offsetX = (LayoutCanvas.ActualWidth - (layoutWidth * scale)) / 2.0;
        var offsetY = (LayoutCanvas.ActualHeight - (layoutHeight * scale)) / 2.0;

        return new TransformContext(minX, minY, scale, offsetX, offsetY);
    }

    private void DrawMonitors()
    {
        foreach (var monitor in _monitors)
        {
            var left = _transform.ToCanvasX(monitor.Bounds.Left);
            var top = _transform.ToCanvasY(monitor.Bounds.Top);
            var width = monitor.Bounds.Width * _transform.Scale;
            var height = monitor.Bounds.Height * _transform.Scale;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 235, 235)),
                Stroke = monitor.IsPrimary
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 120, 228))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(64, 64, 64)),
                StrokeThickness = monitor.IsPrimary ? 3 : 1.5,
                RadiusX = 5,
                RadiusY = 5
            };

            System.Windows.Controls.Canvas.SetLeft(rect, left);
            System.Windows.Controls.Canvas.SetTop(rect, top);
            LayoutCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = monitor.DeviceName,
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.Black
            };

            System.Windows.Controls.Canvas.SetLeft(label, left + 8);
            System.Windows.Controls.Canvas.SetTop(label, top + 8);
            LayoutCanvas.Children.Add(label);

            if (DebugOverlayCheckBox.IsChecked == true)
            {
                var boundsText = new TextBlock
                {
                    Text = $"{monitor.Bounds.Left},{monitor.Bounds.Top} -> {monitor.Bounds.Right},{monitor.Bounds.Bottom}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255))
                };

                System.Windows.Controls.Canvas.SetLeft(boundsText, left + 8);
                System.Windows.Controls.Canvas.SetTop(boundsText, top + 26);
                LayoutCanvas.Children.Add(boundsText);
            }
        }
    }

    private void DrawEdges()
    {
        foreach (var edge in _edges)
        {
            var port = _ports[edge.Id];
            ApplyBoundaryAnchor(edge, port);
            NormalizePort(edge, port);

            var (x1, y1, x2, y2) = EdgeToCanvas(edge, edge.SegmentStart, edge.SegmentEnd);
            var baseline = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 72, 72)),
                StrokeDashArray = [3, 3],
                StrokeThickness = 2
            };
            LayoutCanvas.Children.Add(baseline);

            var (px1, py1, px2, py2) = EdgeToCanvas(edge, port.PortStart, port.PortEnd);
            var passZone = new Line
            {
                X1 = px1,
                Y1 = py1,
                X2 = px2,
                Y2 = py2,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 168, 90)),
                StrokeThickness = 5
            };
            LayoutCanvas.Children.Add(passZone);

            var startHandle = CreateHandle(px1, py1, edge, true);
            var endHandle = CreateHandle(px2, py2, edge, false);
            if (BoundaryHandleModeCheckBox.IsChecked == true)
            {
                var startActive = IsStartHandleActiveForBoundaryMode(edge);
                startHandle.Opacity = startActive ? 1.0 : 0.30;
                startHandle.IsHitTestVisible = startActive;
                endHandle.Opacity = startActive ? 0.30 : 1.0;
                endHandle.IsHitTestVisible = !startActive;
            }
            LayoutCanvas.Children.Add(startHandle);
            LayoutCanvas.Children.Add(endHandle);

            _drawables.Add(new DrawableEdge(edge, passZone, startHandle, endHandle));
        }
    }

    private Ellipse CreateHandle(double x, double y, SharedEdge edge, bool isStartHandle)
    {
        var handle = new Ellipse
        {
            Width = HandleRadius * 2,
            Height = HandleRadius * 2,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 168, 90)),
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 2.5,
            Tag = new HandleTag(edge.Id, isStartHandle),
            Cursor = edge.Orientation == EdgeOrientation.Vertical ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.SizeWE
        };

        System.Windows.Controls.Canvas.SetLeft(handle, x - HandleRadius);
        System.Windows.Controls.Canvas.SetTop(handle, y - HandleRadius);
        handle.MouseLeftButtonDown += Handle_OnMouseLeftButtonDown;
        handle.MouseLeftButtonUp += Handle_OnMouseLeftButtonUp;
        handle.MouseMove += Handle_OnMouseMove;
        return handle;
    }

    private void Handle_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse ellipse || ellipse.Tag is not HandleTag tag)
        {
            return;
        }

        _dragState = new DragState(tag.EdgeId, tag.IsStartHandle);
        ellipse.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragState is null || sender is not Ellipse ellipse || ellipse.Tag is not HandleTag)
        {
            return;
        }

        var drag = _dragState.Value;
        var edge = _edges.First(x => x.Id == drag.EdgeId);
        var port = _ports[edge.Id];
        var mouse = e.GetPosition(LayoutCanvas);

        if (edge.Orientation == EdgeOrientation.Vertical)
        {
            var value = _transform.ToVirtualY(mouse.Y);
            var clamped = Clamp(value, edge.ExtendedSegmentStart, edge.ExtendedSegmentEnd);
            if (drag.IsStartHandle)
            {
                port.PortStart = clamped;
            }
            else
            {
                port.PortEnd = clamped;
            }
        }
        else
        {
            var value = _transform.ToVirtualX(mouse.X);
            var clamped = Clamp(value, edge.ExtendedSegmentStart, edge.ExtendedSegmentEnd);
            if (drag.IsStartHandle)
            {
                port.PortStart = clamped;
            }
            else
            {
                port.PortEnd = clamped;
            }
        }

        NormalizePort(edge, port);
        UpdateEdgeVisual(edge, port);
    }

    private void Handle_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse)
        {
            ellipse.ReleaseMouseCapture();
        }

        _dragState = null;
    }

    private void UpdateEdgeVisual(SharedEdge edge, EdgePort port)
    {
        var drawable = _drawables.FirstOrDefault(x => x.Edge.Id == edge.Id);
        if (drawable.Edge is null)
        {
            return;
        }

        var (x1, y1, x2, y2) = EdgeToCanvas(edge, port.PortStart, port.PortEnd);

        drawable.PassZone.X1 = x1;
        drawable.PassZone.Y1 = y1;
        drawable.PassZone.X2 = x2;
        drawable.PassZone.Y2 = y2;

        System.Windows.Controls.Canvas.SetLeft(drawable.StartHandle, x1 - HandleRadius);
        System.Windows.Controls.Canvas.SetTop(drawable.StartHandle, y1 - HandleRadius);
        System.Windows.Controls.Canvas.SetLeft(drawable.EndHandle, x2 - HandleRadius);
        System.Windows.Controls.Canvas.SetTop(drawable.EndHandle, y2 - HandleRadius);
    }

    private (double x1, double y1, double x2, double y2) EdgeToCanvas(SharedEdge edge, int start, int end)
    {
        if (edge.Orientation == EdgeOrientation.Vertical)
        {
            var x = _transform.ToCanvasX(edge.ConstantCoordinate);
            return (x, _transform.ToCanvasY(start), x, _transform.ToCanvasY(end));
        }

        var y = _transform.ToCanvasY(edge.ConstantCoordinate);
        return (_transform.ToCanvasX(start), y, _transform.ToCanvasX(end), y);
    }

    private static void NormalizePort(SharedEdge edge, EdgePort port)
    {
        var min = Math.Min(port.PortStart, port.PortEnd);
        var max = Math.Max(port.PortStart, port.PortEnd);
        port.PortStart = Clamp(min, edge.ExtendedSegmentStart, edge.ExtendedSegmentEnd);
        port.PortEnd = Clamp(max, edge.ExtendedSegmentStart, edge.ExtendedSegmentEnd);
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyBoundaryHandleAnchors();
        _onSave(_ports.Values.Select(x => new EdgePort
        {
            EdgeId = x.EdgeId,
            PortStart = x.PortStart,
            PortEnd = x.PortEnd
        }).ToList(),
        LegacyHookModeCheckBox.IsChecked == true ? EnforcementMode.Hook : EnforcementMode.ClipCursor,
        SuspendFullscreenCheckBox.IsChecked == true);

        DiagnosticsLog.Write("SetupWindow Save clicked.");
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Write("SetupWindow Cancel clicked.");
        Close();
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var edge in _edges)
        {
            var port = _ports[edge.Id];
            port.PortStart = edge.SegmentStart;
            port.PortEnd = edge.SegmentEnd;
        }

        Redraw();
    }

    private void DebugOverlayCheckBox_OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        Redraw();
    }

    private void UpdateStatus()
    {
        if (_edges.Count == 0)
        {
            StatusText.Text =
                "No shared monitor edges found. In Windows Display Settings, make sure monitors touch each other (no gaps), then reopen this window.";
            StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 214, 128));
            return;
        }

        var modeText = BoundaryHandleModeCheckBox.IsChecked == true
            ? "Boundary mode: drag the right/bottom handle to set cutoff."
            : "Dual-handle mode: drag either handle to resize pass-through.";
        var enforcementText = LegacyHookModeCheckBox.IsChecked == true
            ? "Deprecated Hook mode selected."
            : "ClipCursor mode selected.";
        StatusText.Text = $"Detected {_edges.Count} shared edge(s). Green = allowed pass-through, red = blocked edge. {modeText} {enforcementText}";
        StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 245, 214));
    }

    private void ApplyBoundaryHandleAnchors()
    {
        if (BoundaryHandleModeCheckBox.IsChecked != true)
        {
            return;
        }

        foreach (var edge in _edges)
        {
            if (!_ports.TryGetValue(edge.Id, out var port))
            {
                continue;
            }

            ApplyBoundaryAnchor(edge, port);
            NormalizePort(edge, port);
        }
    }

    private void ApplyBoundaryAnchor(SharedEdge edge, EdgePort port)
    {
        if (BoundaryHandleModeCheckBox.IsChecked != true)
        {
            return;
        }

        if (IsStartHandleActiveForBoundaryMode(edge))
        {
            // Left/start handle is active; keep right/end fixed.
            port.PortEnd = edge.SegmentEnd;
        }
        else
        {
            // Right/end handle is active; keep left/start fixed.
            port.PortStart = edge.SegmentStart;
        }
    }

    private bool IsStartHandleActiveForBoundaryMode(SharedEdge edge)
    {
        var monitorA = _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, edge.MonitorA, StringComparison.Ordinal));
        var monitorB = _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, edge.MonitorB, StringComparison.Ordinal));
        if (monitorA is null || monitorB is null)
        {
            return edge.SegmentEnd <= 0;
        }

        var reference = SelectReferenceMonitor(monitorA, monitorB);
        const int tolerance = 2;

        if (edge.Orientation == EdgeOrientation.Horizontal)
        {
            var touchesLeft = Math.Abs(edge.SegmentStart - reference.Bounds.Left) <= tolerance;
            var touchesRight = Math.Abs(edge.SegmentEnd - reference.Bounds.Right) <= tolerance;
            if (touchesLeft && !touchesRight)
            {
                // Overlap is on left side of reference monitor -> use right/end boundary handle.
                return false;
            }

            if (touchesRight && !touchesLeft)
            {
                // Overlap is on right side of reference monitor -> use left/start boundary handle.
                return true;
            }

            // Fallback: left-half overlap => end handle, right-half overlap => start handle.
            var overlapMid = (edge.SegmentStart + edge.SegmentEnd) / 2.0;
            var monitorMid = (reference.Bounds.Left + reference.Bounds.Right) / 2.0;
            return overlapMid >= monitorMid;
        }

        var touchesTop = Math.Abs(edge.SegmentStart - reference.Bounds.Top) <= tolerance;
        var touchesBottom = Math.Abs(edge.SegmentEnd - reference.Bounds.Bottom) <= tolerance;
        if (touchesTop && !touchesBottom)
        {
            return false;
        }

        if (touchesBottom && !touchesTop)
        {
            return true;
        }

        var overlapMidVertical = (edge.SegmentStart + edge.SegmentEnd) / 2.0;
        var monitorMidVertical = (reference.Bounds.Top + reference.Bounds.Bottom) / 2.0;
        return overlapMidVertical >= monitorMidVertical;
    }

    private static MonitorDescriptor SelectReferenceMonitor(MonitorDescriptor a, MonitorDescriptor b)
    {
        var areaA = (long)a.Bounds.Width * a.Bounds.Height;
        var areaB = (long)b.Bounds.Width * b.Bounds.Height;
        if (areaA > areaB)
        {
            return a;
        }

        if (areaB > areaA)
        {
            return b;
        }

        if (a.IsPrimary && !b.IsPrimary)
        {
            return a;
        }

        if (b.IsPrimary && !a.IsPrimary)
        {
            return b;
        }

        return string.Compare(a.DeviceName, b.DeviceName, StringComparison.Ordinal) <= 0 ? a : b;
    }

    private void DrawDebugOverlayOnCanvas()
    {
        foreach (var edge in _edges)
        {
            var labelPoint = edge.Orientation == EdgeOrientation.Vertical
                ? new System.Windows.Point(_transform.ToCanvasX(edge.ConstantCoordinate) + 6, _transform.ToCanvasY((edge.SegmentStart + edge.SegmentEnd) / 2))
                : new System.Windows.Point(_transform.ToCanvasX((edge.SegmentStart + edge.SegmentEnd) / 2), _transform.ToCanvasY(edge.ConstantCoordinate) + 6);

            var tag = new TextBlock
            {
                Text = edge.Orientation == EdgeOrientation.Vertical ? $"V @ x={edge.ConstantCoordinate}" : $"H @ y={edge.ConstantCoordinate}",
                FontSize = 10,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 245, 220)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 70, 20))
            };
            System.Windows.Controls.Canvas.SetLeft(tag, labelPoint.X);
            System.Windows.Controls.Canvas.SetTop(tag, labelPoint.Y);
            LayoutCanvas.Children.Add(tag);
        }
    }

    private void UpdateDebugPanel()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Monitors: {_monitors.Count}");
        sb.AppendLine($"SharedEdges: {_edges.Count}");
        sb.AppendLine($"AdjacencyTolerancePx: {DebugAdjacencyTolerancePx}");
        sb.AppendLine($"Scale: {_transform.Scale:F3}");
        sb.AppendLine();

        for (var i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            sb.AppendLine($"[{i}] {m.DeviceName} {(m.IsPrimary ? "(Primary)" : string.Empty)}");
            sb.AppendLine($"    Bounds: L={m.Bounds.Left} T={m.Bounds.Top} R={m.Bounds.Right} B={m.Bounds.Bottom}");
            sb.AppendLine($"    Size: {m.Bounds.Width}x{m.Bounds.Height}");
        }

        if (_monitors.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Pair diagnostics:");
        }

        for (var i = 0; i < _monitors.Count; i++)
        {
            for (var j = i + 1; j < _monitors.Count; j++)
            {
                var a = _monitors[i];
                var b = _monitors[j];
                var overlapY = Math.Max(0, Math.Min(a.Bounds.Bottom, b.Bounds.Bottom) - Math.Max(a.Bounds.Top, b.Bounds.Top));
                var overlapX = Math.Max(0, Math.Min(a.Bounds.Right, b.Bounds.Right) - Math.Max(a.Bounds.Left, b.Bounds.Left));

                var dxAB = b.Bounds.Left - a.Bounds.Right;
                var dxBA = a.Bounds.Left - b.Bounds.Right;
                var dyAB = b.Bounds.Top - a.Bounds.Bottom;
                var dyBA = a.Bounds.Top - b.Bounds.Bottom;

                var closestVerticalGap = Math.Min(Math.Abs(dxAB), Math.Abs(dxBA));
                var closestHorizontalGap = Math.Min(Math.Abs(dyAB), Math.Abs(dyBA));

                var verticalNear = closestVerticalGap <= DebugAdjacencyTolerancePx && overlapY > 0;
                var horizontalNear = closestHorizontalGap <= DebugAdjacencyTolerancePx && overlapX > 0;

                var edgeDetected = _edges.Any(e =>
                    (e.MonitorA == a.DeviceName && e.MonitorB == b.DeviceName) ||
                    (e.MonitorA == b.DeviceName && e.MonitorB == a.DeviceName));

                sb.AppendLine($"- {a.DeviceName} <-> {b.DeviceName}");
                sb.AppendLine($"    overlapX={overlapX} overlapY={overlapY}");
                sb.AppendLine($"    gaps: dxAB={dxAB}, dxBA={dxBA}, dyAB={dyAB}, dyBA={dyBA}");
                sb.AppendLine($"    nearVertical={verticalNear}, nearHorizontal={horizontalNear}, edgeDetected={edgeDetected}");
            }
        }

        DebugText.Text = sb.ToString();
    }

    private readonly record struct TransformContext(int MinX, int MinY, double Scale, double OffsetX, double OffsetY)
    {
        public double ToCanvasX(int x) => OffsetX + ((x - MinX) * Scale);
        public double ToCanvasY(int y) => OffsetY + ((y - MinY) * Scale);
        public int ToVirtualX(double x) => (int)Math.Round(((x - OffsetX) / Scale) + MinX);
        public int ToVirtualY(double y) => (int)Math.Round(((y - OffsetY) / Scale) + MinY);
    }

    private readonly record struct HandleTag(string EdgeId, bool IsStartHandle);
    private readonly record struct DragState(string EdgeId, bool IsStartHandle);
    private readonly record struct DrawableEdge(SharedEdge Edge, Line PassZone, Ellipse StartHandle, Ellipse EndHandle);
}
