using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using MousePassport.App.Interop;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

public sealed class MonitorLayoutService
{
    // Mixed DPI / scaling can produce tiny coordinate mismatches between monitors
    // that still behave as touching in Windows display management.
    private const int AdjacencyTolerancePx = 16;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitorApi = GetMonitorsFromMonitorApi();
        var displaySettingsApi = GetMonitorsFromDisplaySettingsApi();

        return SelectBestMonitorSet(monitorApi, displaySettingsApi);
    }

    private List<MonitorDescriptor> GetMonitorsFromMonitorApi()
    {
        var monitors = new List<MonitorDescriptor>();

        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT __, IntPtr ___) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            monitors.Add(new MonitorDescriptor
            {
                DeviceName = info.szDevice,
                IsPrimary = (info.dwFlags & NativeMethods.MonitorInfoPrimary) != 0,
                Bounds = new IntRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            });
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return monitors;
    }

    private static List<MonitorDescriptor> GetMonitorsFromDisplaySettingsApi()
    {
        var monitors = new List<MonitorDescriptor>();
        uint deviceIndex = 0;

        while (true)
        {
            var display = new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty
            };

            if (!NativeMethods.EnumDisplayDevices(null, deviceIndex, ref display, 0))
            {
                break;
            }

            deviceIndex++;
            if ((display.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) == 0)
            {
                continue;
            }

            var mode = new NativeMethods.DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>()
            };

            if (!NativeMethods.EnumDisplaySettingsEx(display.DeviceName, NativeMethods.EnumCurrentSettings, ref mode, 0))
            {
                continue;
            }

            var left = mode.dmPosition.x;
            var top = mode.dmPosition.y;
            var width = (int)mode.dmPelsWidth;
            var height = (int)mode.dmPelsHeight;
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            monitors.Add(new MonitorDescriptor
            {
                DeviceName = display.DeviceName,
                IsPrimary = (display.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0,
                Bounds = new IntRect(left, top, left + width, top + height)
            });
        }

        return monitors;
    }

    private IReadOnlyList<MonitorDescriptor> SelectBestMonitorSet(
        IReadOnlyList<MonitorDescriptor> monitorApi,
        IReadOnlyList<MonitorDescriptor> displaySettingsApi)
    {
        if (monitorApi.Count == 0)
        {
            return displaySettingsApi;
        }

        if (displaySettingsApi.Count == 0)
        {
            return monitorApi;
        }

        var monitorApiEdges = GetSharedEdges(monitorApi).Count;
        var displaySettingsEdges = GetSharedEdges(displaySettingsApi).Count;

        if (displaySettingsEdges > monitorApiEdges)
        {
            return displaySettingsApi;
        }

        return monitorApi;
    }

    public IReadOnlyList<SharedEdge> GetSharedEdges(IReadOnlyList<MonitorDescriptor> monitors)
    {
        var edges = new List<SharedEdge>();

        for (var i = 0; i < monitors.Count; i++)
        {
            for (var j = i + 1; j < monitors.Count; j++)
            {
                var a = monitors[i];
                var b = monitors[j];

                AddVerticalEdgeIfAny(edges, a, b);
                AddHorizontalEdgeIfAny(edges, a, b);
            }
        }

        return edges;
    }

    public string ComputeLayoutId(IReadOnlyList<MonitorDescriptor> monitors)
    {
        var parts = monitors
            .OrderBy(m => m.DeviceName, StringComparer.Ordinal)
            .Select(m => $"{m.DeviceName}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Right},{m.Bounds.Bottom}:{m.IsPrimary}");
        var payload = string.Join("|", parts);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    public MonitorDescriptor? FindMonitorAt(
        IReadOnlyList<MonitorDescriptor> monitors,
        IntPoint point,
        string? preferredDeviceName = null)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var strict = monitors.Where(m => m.Bounds.Contains(point)).ToList();
        var selected = SelectPreferred(strict, preferredDeviceName);
        if (selected is not null)
        {
            return selected;
        }

        var inclusive = monitors.Where(m => m.Bounds.ContainsInclusive(point)).ToList();
        selected = SelectPreferred(inclusive, preferredDeviceName);
        if (selected is not null)
        {
            return selected;
        }

        return monitors
            .OrderBy(m => m.Bounds.DistanceSquaredTo(point))
            .FirstOrDefault();
    }

    private static MonitorDescriptor? SelectPreferred(
        IReadOnlyList<MonitorDescriptor> candidates,
        string? preferredDeviceName)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            var preferred = candidates.FirstOrDefault(
                m => string.Equals(m.DeviceName, preferredDeviceName, StringComparison.Ordinal));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return candidates[0];
    }

    private static void AddVerticalEdgeIfAny(ICollection<SharedEdge> edges, MonitorDescriptor a, MonitorDescriptor b)
    {
        var aToB = Math.Abs(a.Bounds.Right - b.Bounds.Left) <= AdjacencyTolerancePx;
        var bToA = Math.Abs(b.Bounds.Right - a.Bounds.Left) <= AdjacencyTolerancePx;
        if (aToB || bToA)
        {
            var overlapTop = Math.Max(a.Bounds.Top, b.Bounds.Top);
            var overlapBottom = Math.Min(a.Bounds.Bottom, b.Bounds.Bottom);
            if (overlapBottom <= overlapTop)
            {
                return;
            }

            var constant = aToB
                ? (a.Bounds.Right + b.Bounds.Left) / 2
                : (b.Bounds.Right + a.Bounds.Left) / 2;
            edges.Add(new SharedEdge
            {
                Id = CreateEdgeId(a.DeviceName, b.DeviceName, EdgeOrientation.Vertical, constant, overlapTop, overlapBottom),
                MonitorA = a.DeviceName,
                MonitorB = b.DeviceName,
                Orientation = EdgeOrientation.Vertical,
                ConstantCoordinate = constant,
                SegmentStart = overlapTop,
                SegmentEnd = overlapBottom
            });
        }
    }

    private static void AddHorizontalEdgeIfAny(ICollection<SharedEdge> edges, MonitorDescriptor a, MonitorDescriptor b)
    {
        var aToB = Math.Abs(a.Bounds.Bottom - b.Bounds.Top) <= AdjacencyTolerancePx;
        var bToA = Math.Abs(b.Bounds.Bottom - a.Bounds.Top) <= AdjacencyTolerancePx;
        if (aToB || bToA)
        {
            var overlapLeft = Math.Max(a.Bounds.Left, b.Bounds.Left);
            var overlapRight = Math.Min(a.Bounds.Right, b.Bounds.Right);
            if (overlapRight <= overlapLeft)
            {
                return;
            }

            var constant = aToB
                ? (a.Bounds.Bottom + b.Bounds.Top) / 2
                : (b.Bounds.Bottom + a.Bounds.Top) / 2;
            edges.Add(new SharedEdge
            {
                Id = CreateEdgeId(a.DeviceName, b.DeviceName, EdgeOrientation.Horizontal, constant, overlapLeft, overlapRight),
                MonitorA = a.DeviceName,
                MonitorB = b.DeviceName,
                Orientation = EdgeOrientation.Horizontal,
                ConstantCoordinate = constant,
                SegmentStart = overlapLeft,
                SegmentEnd = overlapRight
            });
        }
    }

    private static string CreateEdgeId(
        string monitorA,
        string monitorB,
        EdgeOrientation orientation,
        int constant,
        int start,
        int end)
    {
        var sorted = new[] { monitorA, monitorB }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var axis = orientation == EdgeOrientation.Vertical ? "V" : "H";
        return $"{sorted[0]}|{sorted[1]}|{axis}|{constant}|{start}|{end}";
    }
}
