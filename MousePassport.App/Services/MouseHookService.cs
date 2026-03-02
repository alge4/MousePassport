using System.Diagnostics;
using System.Runtime.InteropServices;
using MousePassport.App.Interop;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

public enum CorrectionMode
{
    BlockAndReposition,
    AllowThenReposition
}

public sealed class MouseHookService : IDisposable
{
    private const double Epsilon = 1.5;
    private const int BoundaryInsetPx = 3;
    private static readonly TimeSpan ProgrammaticSuppression = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan HardPinDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EdgeCooldownDuration = TimeSpan.FromMilliseconds(420);

    private readonly MonitorLayoutService _layoutService;
    private readonly Func<LayoutPortConfig?> _configAccessor;
    private readonly Func<IReadOnlyList<MonitorDescriptor>> _monitorsAccessor;
    private readonly Func<IReadOnlyList<SharedEdge>> _edgesAccessor;

    private NativeMethods.LowLevelMouseProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPoint _previous;
    private bool _hasPrevious;
    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private DateTime _hardPinUntilUtc = DateTime.MinValue;
    private DateTime _edgeCooldownUntilUtc = DateTime.MinValue;
    private DateTime _nextDiagnosticLogUtc = DateTime.MinValue;
    private string? _lastMonitorName;
    private string? _hardPinSourceDevice;
    private string? _edgeCooldownEdgeId;
    private IntPoint _hardPinPoint;

    public MouseHookService(
        MonitorLayoutService layoutService,
        Func<LayoutPortConfig?> configAccessor,
        Func<IReadOnlyList<MonitorDescriptor>> monitorsAccessor,
        Func<IReadOnlyList<SharedEdge>> edgesAccessor)
    {
        _layoutService = layoutService;
        _configAccessor = configAccessor;
        _monitorsAccessor = monitorsAccessor;
        _edgesAccessor = edgesAccessor;
    }

    public CorrectionMode Mode { get; set; } = CorrectionMode.BlockAndReposition;
    public bool IsEnabled { get; set; } = true;
    public bool IsStarted => _hookHandle != IntPtr.Zero;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        IntPtr moduleHandle;
        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            moduleHandle = NativeMethods.GetModuleHandle(module?.ModuleName);
        }
        catch
        {
            moduleHandle = NativeMethods.GetModuleHandle(null);
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _hookProc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to install mouse hook.");
        }

        if (NativeMethods.GetCursorPos(out var point))
        {
            _previous = new IntPoint(point.X, point.Y);
            _hasPrevious = true;
            var monitors = _monitorsAccessor();
            _lastMonitorName = _layoutService.FindMonitorAt(monitors, _previous)?.DeviceName;
        }
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hasPrevious = false;
        _lastMonitorName = null;
        _hardPinSourceDevice = null;
        _hardPinUntilUtc = DateTime.MinValue;
        _edgeCooldownEdgeId = null;
        _edgeCooldownUntilUtc = DateTime.MinValue;
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < NativeMethods.HcAction || wParam.ToInt32() != NativeMethods.WmMouseMove || lParam == IntPtr.Zero)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var hook = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        var current = new IntPoint(hook.pt.X, hook.pt.Y);

        if (DateTime.UtcNow <= _hardPinUntilUtc && !string.IsNullOrWhiteSpace(_hardPinSourceDevice))
        {
            var monitorsForPin = _monitorsAccessor();
            var currentMonitor = _layoutService.FindMonitorAt(monitorsForPin, current, _hardPinSourceDevice);
            if (currentMonitor is null || !string.Equals(currentMonitor.DeviceName, _hardPinSourceDevice, StringComparison.Ordinal))
            {
                NativeMethods.SetCursorPos(_hardPinPoint.X, _hardPinPoint.Y);
                _previous = _hardPinPoint;
                _lastMonitorName = _hardPinSourceDevice;
                return (IntPtr)1;
            }
        }

        if (DateTime.UtcNow <= _suppressUntilUtc)
        {
            // Ignore synthetic movement emitted around SetCursorPos correction.
            // Updating state here can incorrectly "advance" into the blocked monitor.
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!IsEnabled)
        {
            _hardPinUntilUtc = DateTime.MinValue;
            _hardPinSourceDevice = null;
            _edgeCooldownEdgeId = null;
            _edgeCooldownUntilUtc = DateTime.MinValue;
            _previous = current;
            _hasPrevious = true;
            var currentMonitors = _monitorsAccessor();
            _lastMonitorName = _layoutService.FindMonitorAt(currentMonitors, current, _lastMonitorName)?.DeviceName;
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!_hasPrevious)
        {
            _previous = current;
            _hasPrevious = true;
            var currentMonitors = _monitorsAccessor();
            _lastMonitorName = _layoutService.FindMonitorAt(currentMonitors, current, _lastMonitorName)?.DeviceName;
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var monitors = _monitorsAccessor();
        var source = _layoutService.FindMonitorAt(monitors, _previous, _lastMonitorName);
        var target = _layoutService.FindMonitorAt(monitors, current, source?.DeviceName ?? _lastMonitorName);
        if (source is null || target is null || string.Equals(source.DeviceName, target.DeviceName, StringComparison.Ordinal))
        {
            _previous = current;
            _lastMonitorName = target?.DeviceName ?? source?.DeviceName ?? _lastMonitorName;
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var config = _configAccessor();
        var edges = _edgesAccessor();
        var directional = EvaluateDirectionalBoundary(_previous, current, source, target, edges, config);
        if (directional.DecisionMade)
        {
            if (directional.Allow && !IsOnEdgeCooldown(directional.Edge))
            {
                _hardPinUntilUtc = DateTime.MinValue;
                _hardPinSourceDevice = null;
                _edgeCooldownEdgeId = null;
                _edgeCooldownUntilUtc = DateTime.MinValue;
                LogTransition("ALLOW", source.DeviceName, target.DeviceName, _previous, current, directional.Edge, directional.IntersectionAxisValue);
                _previous = current;
                _lastMonitorName = target.DeviceName;
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            LogTransition("BLOCK", source.DeviceName, target.DeviceName, _previous, current, directional.Edge, directional.IntersectionAxisValue);
            _suppressUntilUtc = DateTime.UtcNow.Add(ProgrammaticSuppression);
            var directionalBlockedPoint = GetBlockedPoint(source, _previous, current, directional.Edge);
            NativeMethods.SetCursorPos(directionalBlockedPoint.X, directionalBlockedPoint.Y);
            _previous = directionalBlockedPoint;
            _lastMonitorName = source.DeviceName;
            _hardPinPoint = directionalBlockedPoint;
            _hardPinSourceDevice = source.DeviceName;
            _hardPinUntilUtc = DateTime.UtcNow.Add(HardPinDuration);
            ActivateEdgeCooldown(directional.Edge);

            if (Mode == CorrectionMode.AllowThenReposition)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            return (IntPtr)1;
        }

        var crossing = FindCrossing(_previous, current, source.DeviceName, target.DeviceName, edges);
        if (crossing is not null &&
            IsAllowed(crossing.Value.IntersectionAxisValue, crossing.Value.Edge, config) &&
            !IsOnEdgeCooldown(crossing.Value.Edge))
        {
            _hardPinUntilUtc = DateTime.MinValue;
            _hardPinSourceDevice = null;
            _edgeCooldownEdgeId = null;
            _edgeCooldownUntilUtc = DateTime.MinValue;
            LogTransition("ALLOW", source.DeviceName, target.DeviceName, _previous, current, crossing.Value.Edge, crossing.Value.IntersectionAxisValue);
            _previous = current;
            _lastMonitorName = target.DeviceName;
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        LogTransition("BLOCK", source.DeviceName, target.DeviceName, _previous, current, crossing?.Edge, crossing?.IntersectionAxisValue);
        _suppressUntilUtc = DateTime.UtcNow.Add(ProgrammaticSuppression);
        var blockedPoint = GetBlockedPoint(source, _previous, current, crossing?.Edge);
        NativeMethods.SetCursorPos(blockedPoint.X, blockedPoint.Y);
        _previous = blockedPoint;
        _lastMonitorName = source.DeviceName;
        _hardPinPoint = blockedPoint;
        _hardPinSourceDevice = source.DeviceName;
        _hardPinUntilUtc = DateTime.UtcNow.Add(HardPinDuration);
        ActivateEdgeCooldown(crossing?.Edge);

        if (Mode == CorrectionMode.AllowThenReposition)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        return (IntPtr)1;
    }

    private static bool IsAllowed(double intersectionAxisValue, SharedEdge edge, LayoutPortConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        var port = config.EdgePorts.FirstOrDefault(x => string.Equals(x.EdgeId, edge.Id, StringComparison.Ordinal));
        if (port is null)
        {
            return true;
        }

        // Do not pad pass-through bounds with epsilon; strict boundary control
        // is required to prevent leakage outside the configured edge segment.
        var min = Math.Min(port.PortStart, port.PortEnd);
        var max = Math.Max(port.PortStart, port.PortEnd);
        return intersectionAxisValue >= min && intersectionAxisValue <= max;
    }

    private static DirectionalDecision EvaluateDirectionalBoundary(
        IntPoint previous,
        IntPoint current,
        MonitorDescriptor source,
        MonitorDescriptor target,
        IReadOnlyList<SharedEdge> edges,
        LayoutPortConfig? config)
    {
        var related = edges.Where(edge =>
            (string.Equals(edge.MonitorA, source.DeviceName, StringComparison.Ordinal) &&
             string.Equals(edge.MonitorB, target.DeviceName, StringComparison.Ordinal)) ||
            (string.Equals(edge.MonitorA, target.DeviceName, StringComparison.Ordinal) &&
             string.Equals(edge.MonitorB, source.DeviceName, StringComparison.Ordinal))).ToList();

        if (related.Count == 0)
        {
            return DirectionalDecision.NoDecision;
        }

        DirectionalDecision? best = null;
        foreach (var edge in related)
        {
            if (edge.Orientation == EdgeOrientation.Horizontal)
            {
                var sourceAbove = source.Bounds.Bottom <= edge.ConstantCoordinate + 1;
                var crossed = sourceAbove
                    ? previous.Y <= edge.ConstantCoordinate + Epsilon && current.Y >= edge.ConstantCoordinate - Epsilon
                    : previous.Y >= edge.ConstantCoordinate - Epsilon && current.Y <= edge.ConstantCoordinate + Epsilon;
                if (!crossed)
                {
                    continue;
                }

                var axis = previous.X;
                var allow = IsAllowed(axis, edge, config);
                var candidate = new DirectionalDecision(true, allow, edge, axis);
                best = PreferDecision(best, candidate);
                continue;
            }

            var sourceLeft = source.Bounds.Right <= edge.ConstantCoordinate + 1;
            var crossedVertical = sourceLeft
                ? previous.X <= edge.ConstantCoordinate + Epsilon && current.X >= edge.ConstantCoordinate - Epsilon
                : previous.X >= edge.ConstantCoordinate - Epsilon && current.X <= edge.ConstantCoordinate + Epsilon;
            if (!crossedVertical)
            {
                continue;
            }

            var axisVertical = previous.Y;
            var allowVertical = IsAllowed(axisVertical, edge, config);
            var verticalCandidate = new DirectionalDecision(true, allowVertical, edge, axisVertical);
            best = PreferDecision(best, verticalCandidate);
        }

        return best ?? DirectionalDecision.NoDecision;
    }

    private static DirectionalDecision PreferDecision(DirectionalDecision? current, DirectionalDecision candidate)
    {
        if (current is null)
        {
            return candidate;
        }

        // Blocking decisions win over allowing decisions for safety.
        if (!candidate.Allow && current.Value.Allow)
        {
            return candidate;
        }

        if (candidate.Allow == current.Value.Allow)
        {
            return candidate;
        }

        return current.Value;
    }

    private static CrossingCandidate? FindCrossing(
        IntPoint previous,
        IntPoint current,
        string sourceMonitorName,
        string targetMonitorName,
        IReadOnlyList<SharedEdge> edges)
    {
        var relatedEdges = new List<SharedEdge>();
        var candidates = new List<CrossingCandidate>();
        foreach (var edge in edges)
        {
            var relatesSourceTarget =
                (string.Equals(edge.MonitorA, sourceMonitorName, StringComparison.Ordinal) &&
                 string.Equals(edge.MonitorB, targetMonitorName, StringComparison.Ordinal)) ||
                (string.Equals(edge.MonitorA, targetMonitorName, StringComparison.Ordinal) &&
                 string.Equals(edge.MonitorB, sourceMonitorName, StringComparison.Ordinal));

            if (!relatesSourceTarget)
            {
                continue;
            }

            relatedEdges.Add(edge);
            var candidate = IntersectSegment(previous, current, edge);
            if (candidate is not null)
            {
                candidates.Add(candidate.Value);
            }
        }

        if (candidates.Count == 0)
        {
            var projectedOnly = relatedEdges
                .Select(edge => BuildProjectedCandidate(previous, edge))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!.Value)
                .OrderBy(candidate => candidate.T)
                .ToList();

            if (projectedOnly.Count == 0)
            {
                return null;
            }

            return projectedOnly[0];
        }

        var ordered = candidates.OrderBy(c => c.T).ToList();
        if (ordered.Count > 1 && Math.Abs(ordered[0].T - ordered[1].T) <= 0.0001)
        {
            return null;
        }

        return ordered[0];
    }

    private static CrossingCandidate? BuildProjectedCandidate(IntPoint previous, SharedEdge edge)
    {
        var axis = edge.Orientation == EdgeOrientation.Horizontal ? previous.X : previous.Y;
        if (axis < edge.SegmentStart - Epsilon || axis > edge.SegmentEnd + Epsilon)
        {
            return null;
        }

        // Use t=1.0 so explicit line intersections still win over projections.
        return new CrossingCandidate(edge, 1.0, axis);
    }

    private static CrossingCandidate? IntersectSegment(IntPoint previous, IntPoint current, SharedEdge edge)
    {
        var x1 = previous.X;
        var y1 = previous.Y;
        var x2 = current.X;
        var y2 = current.Y;

        if (edge.Orientation == EdgeOrientation.Vertical)
        {
            var dx = x2 - x1;
            if (Math.Abs(dx) < Epsilon)
            {
                return null;
            }

            var t = (edge.ConstantCoordinate - x1) / (double)dx;
            if (t < 0.0 || t > 1.0)
            {
                return null;
            }

            var y = y1 + ((y2 - y1) * t);
            if (y < edge.SegmentStart - Epsilon || y > edge.SegmentEnd + Epsilon)
            {
                return null;
            }

            return new CrossingCandidate(edge, t, y);
        }
        else
        {
            var dy = y2 - y1;
            if (Math.Abs(dy) < Epsilon)
            {
                return null;
            }

            var t = (edge.ConstantCoordinate - y1) / (double)dy;
            if (t < 0.0 || t > 1.0)
            {
                return null;
            }

            var x = x1 + ((x2 - x1) * t);
            if (x < edge.SegmentStart - Epsilon || x > edge.SegmentEnd + Epsilon)
            {
                return null;
            }

            return new CrossingCandidate(edge, t, x);
        }
    }

    private readonly record struct CrossingCandidate(SharedEdge Edge, double T, double IntersectionAxisValue);
    private readonly record struct DirectionalDecision(bool DecisionMade, bool Allow, SharedEdge? Edge, double? IntersectionAxisValue)
    {
        public static DirectionalDecision NoDecision => new(false, true, null, null);
    }

    private static IntPoint GetBlockedPoint(
        MonitorDescriptor source,
        IntPoint sourceSide,
        IntPoint attempted,
        SharedEdge? crossingEdge)
    {
        var minX = source.Bounds.Left;
        var maxX = source.Bounds.Right - 1;
        var minY = source.Bounds.Top;
        var maxY = source.Bounds.Bottom - 1;

        if (crossingEdge is null)
        {
            return new IntPoint(
                ClampInt(attempted.X, minX, maxX),
                ClampInt(attempted.Y, minY, maxY));
        }

        if (crossingEdge.Orientation == EdgeOrientation.Horizontal)
        {
            var sourceAbove = source.Bounds.Bottom <= crossingEdge.ConstantCoordinate + 1;
            var y = sourceAbove
                ? Math.Min(maxY, crossingEdge.ConstantCoordinate - BoundaryInsetPx)
                : Math.Max(minY, crossingEdge.ConstantCoordinate + BoundaryInsetPx);

            // Preserve the source-side X so we don't get shunted into the portal.
            var x = ClampInt(sourceSide.X, minX, maxX);
            return new IntPoint(x, y);
        }

        var sourceLeft = source.Bounds.Right <= crossingEdge.ConstantCoordinate + 1;
        var clampedX = sourceLeft
            ? Math.Min(maxX, crossingEdge.ConstantCoordinate - BoundaryInsetPx)
            : Math.Max(minX, crossingEdge.ConstantCoordinate + BoundaryInsetPx);
        // Preserve the source-side Y for vertical boundaries.
        var clampedY = ClampInt(sourceSide.Y, minY, maxY);
        return new IntPoint(clampedX, clampedY);
    }

    private static int ClampInt(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private void ActivateEdgeCooldown(SharedEdge? edge)
    {
        if (edge is null)
        {
            return;
        }

        _edgeCooldownEdgeId = edge.Id;
        _edgeCooldownUntilUtc = DateTime.UtcNow.Add(EdgeCooldownDuration);
    }

    private bool IsOnEdgeCooldown(SharedEdge? edge)
    {
        if (edge is null || string.IsNullOrWhiteSpace(_edgeCooldownEdgeId))
        {
            return false;
        }

        if (DateTime.UtcNow > _edgeCooldownUntilUtc)
        {
            _edgeCooldownEdgeId = null;
            return false;
        }

        return string.Equals(edge.Id, _edgeCooldownEdgeId, StringComparison.Ordinal);
    }

    private void LogTransition(
        string action,
        string sourceDevice,
        string targetDevice,
        IntPoint previous,
        IntPoint current,
        SharedEdge? edge,
        double? intersectionAxis)
    {
        if (DateTime.UtcNow < _nextDiagnosticLogUtc)
        {
            return;
        }

        // Keep logs lightweight while still giving actionable transition data.
        _nextDiagnosticLogUtc = DateTime.UtcNow.AddMilliseconds(120);
        var edgeText = edge is null
            ? "none"
            : $"{edge.Orientation}@{edge.ConstantCoordinate}[{edge.SegmentStart}..{edge.SegmentEnd}]";
        var ix = intersectionAxis?.ToString("F2") ?? "n/a";

        DiagnosticsLog.Write(
            $"Hook {action} src={sourceDevice} dst={targetDevice} prev=({previous.X},{previous.Y}) cur=({current.X},{current.Y}) edge={edgeText} ix={ix}");
    }
}
