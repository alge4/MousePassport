using MousePassport.App.Models;
using MousePassport.App.Services;
using Xunit;

namespace MousePassport.Tests;

public sealed class PortConfigServiceTests
{
    [Fact]
    public void BuildDefault_creates_config_with_layout_id_and_enforcement_enabled()
    {
        var service = new PortConfigService();
        var edges = new[]
        {
            new SharedEdge
            {
                Id = "edge-1",
                MonitorA = "\\\\.\\DISPLAY1",
                MonitorB = "\\\\.\\DISPLAY2",
                Orientation = EdgeOrientation.Horizontal,
                ConstantCoordinate = 1080,
                SegmentStart = 0,
                SegmentEnd = 1920
            }
        };

        var config = service.BuildDefault("layout-abc", edges);

        Assert.Equal("layout-abc", config.LayoutId);
        Assert.True(config.EnforcementEnabled);
        Assert.Single(config.EdgePorts);
        Assert.Equal("edge-1", config.EdgePorts[0].EdgeId);
        Assert.Equal(0, config.EdgePorts[0].PortStart);
        Assert.Equal(1920, config.EdgePorts[0].PortEnd);
    }

    [Fact]
    public void BuildDefault_includes_port_per_edge()
    {
        var service = new PortConfigService();
        var edges = new[]
        {
            new SharedEdge
            {
                Id = "e1",
                MonitorA = "A",
                MonitorB = "B",
                Orientation = EdgeOrientation.Vertical,
                ConstantCoordinate = 1920,
                SegmentStart = 100,
                SegmentEnd = 500
            },
            new SharedEdge
            {
                Id = "e2",
                MonitorA = "B",
                MonitorB = "C",
                Orientation = EdgeOrientation.Horizontal,
                ConstantCoordinate = 0,
                SegmentStart = 0,
                SegmentEnd = 800
            }
        };

        var config = service.BuildDefault("lid", edges);

        Assert.Equal(2, config.EdgePorts.Count);
        Assert.Equal("e1", config.EdgePorts[0].EdgeId);
        Assert.Equal(100, config.EdgePorts[0].PortStart);
        Assert.Equal(500, config.EdgePorts[0].PortEnd);
        Assert.Equal("e2", config.EdgePorts[1].EdgeId);
        Assert.Equal(0, config.EdgePorts[1].PortStart);
        Assert.Equal(800, config.EdgePorts[1].PortEnd);
    }

    [Fact]
    public void Save_and_Load_round_trips_config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MousePassport.Tests", Path.GetRandomFileName());
        try
        {
            var service = new PortConfigService(dir);
            var edges = new[]
            {
                new SharedEdge
                {
                    Id = "roundtrip-edge",
                    MonitorA = "M1",
                    MonitorB = "M2",
                    Orientation = EdgeOrientation.Horizontal,
                    ConstantCoordinate = 700,
                    SegmentStart = 10,
                    SegmentEnd = 90
                }
            };
            var original = service.BuildDefault("roundtrip-layout", edges);
            original.EnforcementEnabled = false;

            service.Save(original);
            var loaded = service.Load("roundtrip-layout");

            Assert.NotNull(loaded);
            Assert.Equal(original.LayoutId, loaded.LayoutId);
            Assert.Equal(original.EnforcementEnabled, loaded.EnforcementEnabled);
            Assert.Equal(original.EdgePorts.Count, loaded.EdgePorts.Count);
            Assert.Equal(original.EdgePorts[0].EdgeId, loaded.EdgePorts[0].EdgeId);
            Assert.Equal(original.EdgePorts[0].PortStart, loaded.EdgePorts[0].PortStart);
            Assert.Equal(original.EdgePorts[0].PortEnd, loaded.EdgePorts[0].PortEnd);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void Load_returns_null_for_mismatched_layout_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MousePassport.Tests", Path.GetRandomFileName());
        try
        {
            var service = new PortConfigService(dir);
            var config = service.BuildDefault("layout-one", Array.Empty<SharedEdge>());
            service.Save(config);

            var loaded = service.Load("layout-other");

            Assert.Null(loaded);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
