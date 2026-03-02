using MousePassport.App.Interop;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

public sealed class ClipCursorService : IDisposable
{
    private const int GuardBandPx = 36;
    private const int UnlockDepthPx = 260;

    private readonly MonitorLayoutService _layoutService;
    private readonly Func<LayoutPortConfig?> _configAccessor;
    private readonly Func<IReadOnlyList<MonitorDescriptor>> _monitorsAccessor;
    private readonly Func<IReadOnlyList<SharedEdge>> _edgesAccessor;

    private NativeMethods.RECT? _lastAppliedRect;

    public ClipCursorService(
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

    public bool IsEnabled { get; set; }

    public void Update()
    {
        if (!IsEnabled)
        {
            ReleaseClip();
            return;
        }

        var config = _configAccessor();
        if (config is null || !config.EnforcementEnabled)
        {
            ReleaseClip();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            ReleaseClip();
            return;
        }

        var monitors = _monitorsAccessor();
        var edges = _edgesAccessor();
        var currentPoint = new IntPoint(point.X, point.Y);
        var currentMonitor = _layoutService.FindMonitorAt(monitors, currentPoint);
        if (currentMonitor is null)
        {
            ReleaseClip();
            return;
        }

        var clipRect = BuildClipRect(currentMonitor, currentPoint, monitors, edges, config);
        if (HasSameRect(_lastAppliedRect, clipRect))
        {
            return;
        }

        if (NativeMethods.ClipCursor(ref clipRect))
        {
            _lastAppliedRect = clipRect;
        }
    }

    public void ReleaseClip()
    {
        if (_lastAppliedRect is null)
        {
            return;
        }

        NativeMethods.ClipCursor(IntPtr.Zero);
        _lastAppliedRect = null;
    }

    public void Dispose()
    {
        ReleaseClip();
    }

    private NativeMethods.RECT BuildClipRect(
        MonitorDescriptor source,
        IntPoint cursor,
        IReadOnlyList<MonitorDescriptor> monitors,
        IReadOnlyList<SharedEdge> edges,
        LayoutPortConfig config)
    {
        var baseRect = ToClipRect(source.Bounds);
        var related = edges
            .Where(edge => string.Equals(edge.MonitorA, source.DeviceName, StringComparison.Ordinal) ||
                           string.Equals(edge.MonitorB, source.DeviceName, StringComparison.Ordinal))
            .ToList();
        if (related.Count == 0)
        {
            return baseRect;
        }

        GuardCandidate? best = null;
        foreach (var edge in related)
        {
            var port = config.EdgePorts.FirstOrDefault(x => string.Equals(x.EdgeId, edge.Id, StringComparison.Ordinal));
            if (port is null)
            {
                continue;
            }

            var min = Math.Min(port.PortStart, port.PortEnd);
            var max = Math.Max(port.PortStart, port.PortEnd);

            if (edge.Orientation == EdgeOrientation.Horizontal)
            {
                var distance = Math.Abs(cursor.Y - edge.ConstantCoordinate);
                if (distance > GuardBandPx || cursor.X < min || cursor.X > max)
                {
                    continue;
                }

                var targetName = string.Equals(edge.MonitorA, source.DeviceName, StringComparison.Ordinal) ? edge.MonitorB : edge.MonitorA;
                var target = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, targetName, StringComparison.Ordinal));
                if (target is null)
                {
                    continue;
                }

                var pairTop = Math.Min(source.Bounds.Top, target.Bounds.Top);
                var pairBottom = Math.Max(source.Bounds.Bottom, target.Bounds.Bottom) - 1;
                var top = Math.Max(pairTop, edge.ConstantCoordinate - UnlockDepthPx);
                var bottom = Math.Min(pairBottom, edge.ConstantCoordinate + UnlockDepthPx);

                var candidate = new GuardCandidate(distance, new IntRect(min, top, max + 1, bottom + 1));
                best = ChooseCloser(best, candidate);
            }
            else
            {
                var distance = Math.Abs(cursor.X - edge.ConstantCoordinate);
                if (distance > GuardBandPx || cursor.Y < min || cursor.Y > max)
                {
                    continue;
                }

                var targetName = string.Equals(edge.MonitorA, source.DeviceName, StringComparison.Ordinal) ? edge.MonitorB : edge.MonitorA;
                var target = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, targetName, StringComparison.Ordinal));
                if (target is null)
                {
                    continue;
                }

                var pairLeft = Math.Min(source.Bounds.Left, target.Bounds.Left);
                var pairRight = Math.Max(source.Bounds.Right, target.Bounds.Right) - 1;
                var left = Math.Max(pairLeft, edge.ConstantCoordinate - UnlockDepthPx);
                var right = Math.Min(pairRight, edge.ConstantCoordinate + UnlockDepthPx);

                var candidate = new GuardCandidate(distance, new IntRect(left, min, right + 1, max + 1));
                best = ChooseCloser(best, candidate);
            }
        }

        if (best is null)
        {
            return baseRect;
        }

        return ToClipRect(best.Value.Rect);
    }

    private static GuardCandidate ChooseCloser(GuardCandidate? current, GuardCandidate candidate)
    {
        if (current is null || candidate.Distance < current.Value.Distance)
        {
            return candidate;
        }

        return current.Value;
    }

    private static NativeMethods.RECT ToClipRect(IntRect rect)
    {
        return new NativeMethods.RECT
        {
            Left = rect.Left,
            Top = rect.Top,
            Right = rect.Right,
            Bottom = rect.Bottom
        };
    }

    private static bool HasSameRect(NativeMethods.RECT? current, NativeMethods.RECT next)
    {
        return current is not null &&
               current.Value.Left == next.Left &&
               current.Value.Top == next.Top &&
               current.Value.Right == next.Right &&
               current.Value.Bottom == next.Bottom;
    }

    private readonly record struct GuardCandidate(int Distance, IntRect Rect);
}
