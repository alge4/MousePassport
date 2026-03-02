using System.IO;
using System.Text;

namespace MousePassport.App.Services;

public static class DiagnosticsLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath;

    static DiagnosticsLog()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "MousePassport");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, "runtime.log");
    }

    public static string PathOnDisk => LogPath;

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Avoid affecting app behavior when log write fails (e.g. disk full, access denied).
            }
        }
    }
}
