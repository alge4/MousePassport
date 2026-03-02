using System.IO;
using System.Text.Json;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

public sealed class PortConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;

    public PortConfigService(string? configDirectory = null)
    {
        var directory = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MousePassport");
        Directory.CreateDirectory(directory);
        _configPath = Path.Combine(directory, "config.json");
    }

    public LayoutPortConfig BuildDefault(string layoutId, IEnumerable<SharedEdge> edges)
    {
        var config = new LayoutPortConfig
        {
            LayoutId = layoutId,
            EnforcementEnabled = true
        };

        foreach (var edge in edges)
        {
            config.EdgePorts.Add(new EdgePort
            {
                EdgeId = edge.Id,
                PortStart = edge.SegmentStart,
                PortEnd = edge.SegmentEnd
            });
        }

        return config;
    }

    public LayoutPortConfig? Load(string layoutId)
    {
        if (!File.Exists(_configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<LayoutPortConfig>(json, JsonOptions);
            if (config is null || !string.Equals(config.LayoutId, layoutId, StringComparison.Ordinal))
            {
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Config load failed: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    public void Save(LayoutPortConfig config)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        var tempPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }
}
