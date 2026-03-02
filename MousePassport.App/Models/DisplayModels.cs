using System.Text.Json.Serialization;

namespace MousePassport.App.Models;

public enum EdgeOrientation
{
    Vertical,
    Horizontal
}

public enum EnforcementMode
{
    Hook,
    ClipCursor
}

public sealed class MonitorDescriptor
{
    public required string DeviceName { get; init; }
    public required IntRect Bounds { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class SharedEdge
{
    public required string Id { get; init; }
    public required string MonitorA { get; init; }
    public required string MonitorB { get; init; }
    public required EdgeOrientation Orientation { get; init; }
    public int ConstantCoordinate { get; init; }
    public int SegmentStart { get; init; }
    public int SegmentEnd { get; init; }
}

public sealed class EdgePort
{
    public required string EdgeId { get; init; }
    public int PortStart { get; set; }
    public int PortEnd { get; set; }
}

public sealed class LayoutPortConfig
{
    public required string LayoutId { get; init; }
    public bool EnforcementEnabled { get; set; } = true;
    public EnforcementMode EnforcementMode { get; set; } = EnforcementMode.ClipCursor;
    public List<EdgePort> EdgePorts { get; init; } = [];
}

public readonly record struct IntPoint(int X, int Y);

public readonly record struct IntRect(int Left, int Top, int Right, int Bottom)
{
    [JsonIgnore]
    public int Width => Right - Left;

    [JsonIgnore]
    public int Height => Bottom - Top;

    public bool Contains(IntPoint point)
    {
        return point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }

    public bool ContainsInclusive(IntPoint point)
    {
        return point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
    }

    public int DistanceSquaredTo(IntPoint point)
    {
        var dx = 0;
        if (point.X < Left)
        {
            dx = Left - point.X;
        }
        else if (point.X > Right)
        {
            dx = point.X - Right;
        }

        var dy = 0;
        if (point.Y < Top)
        {
            dy = Top - point.Y;
        }
        else if (point.Y > Bottom)
        {
            dy = point.Y - Bottom;
        }

        return (dx * dx) + (dy * dy);
    }
}
