using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using MousePassport.App.Models;
using MousePassport.App.Services;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MousePassport.App;

public sealed class AppController : IDisposable
{
    private readonly MonitorLayoutService _layoutService = new();
    private readonly PortConfigService _configService = new();
    private readonly PanicHotkeyService _panicHotkey = new();

    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Forms.ToolStripMenuItem _configureItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    private readonly MouseHookService _hookService;
    private readonly ClipCursorService _clipService;
    private readonly DispatcherTimer _clipTimer;
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private IReadOnlyList<SharedEdge> _edges = [];
    private LayoutPortConfig? _config;
    private string _layoutId = string.Empty;
    private SetupWindow? _setupWindow;

    public AppController()
    {
        _hookService = new MouseHookService(
            _layoutService,
            () => _config,
            () => _monitors,
            () => _edges);
        _clipService = new ClipCursorService(
            _layoutService,
            () => _config,
            () => _monitors,
            () => _edges);
        _clipTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _clipTimer.Tick += (_, _) => _clipService.Update();

        _enabledItem = new Forms.ToolStripMenuItem("Enabled");
        _enabledItem.Click += (_, _) => ToggleEnabled();

        _configureItem = new Forms.ToolStripMenuItem("Configure...");
        _configureItem.Click += (_, _) => ShowSetupWindow();

        _exitItem = new Forms.ToolStripMenuItem("Exit");
        _exitItem.Click += (_, _) => ExitApplication();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_configureItem);
        menu.Items.Add(_exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "MousePassport",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSetupWindow();

        _panicHotkey.PanicTriggered += (_, _) =>
        {
            SetEnabled(false);
            ShowBalloon("MousePassport disabled", "Panic hotkey pressed.");
        };
    }

    public void Start()
    {
        RefreshLayoutAndConfig();
        _hookService.Start();
        _clipTimer.Start();
        _panicHotkey.Register();
        ApplyEnabledState();
        UpdateTrayState();
        DiagnosticsLog.Write($"App started. LayoutId={_layoutId}, Monitors={_monitors.Count}, Edges={_edges.Count}, Enabled={_config?.EnforcementEnabled ?? true}, Mode={_config?.EnforcementMode}");

        SystemEvents.DisplaySettingsChanged += SystemEventsOnDisplaySettingsChanged;
        ShowBalloon("MousePassport started", "Double-click tray icon to configure pass-through edges.");
    }

    public void Dispose()
    {
        DiagnosticsLog.Write("App disposing.");
        SystemEvents.DisplaySettingsChanged -= SystemEventsOnDisplaySettingsChanged;

        _setupWindow?.Close();
        _clipTimer.Stop();
        _clipService.Dispose();
        _hookService.Dispose();
        _panicHotkey.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void ToggleEnabled()
    {
        SetEnabled(!(_config?.EnforcementEnabled ?? true));
    }

    private void SetEnabled(bool enabled)
    {
        if (_config is null)
        {
            return;
        }

        _config.EnforcementEnabled = enabled;
        _configService.Save(_config);
        ApplyEnabledState();
        UpdateTrayState();
        DiagnosticsLog.Write($"Enforcement toggled. Enabled={enabled}");
    }

    private void ApplyEnabledState()
    {
        var enabled = _config?.EnforcementEnabled ?? true;
        var mode = _config?.EnforcementMode ?? EnforcementMode.ClipCursor;
        _hookService.IsEnabled = enabled && mode == EnforcementMode.Hook;
        _clipService.IsEnabled = enabled && mode == EnforcementMode.ClipCursor;
        if (!_clipService.IsEnabled)
        {
            _clipService.ReleaseClip();
        }
    }

    private void ShowSetupWindow()
    {
        if (_setupWindow is not null)
        {
            _setupWindow.Activate();
            return;
        }

        RefreshLayoutAndConfig();
        if (_config is null)
        {
            return;
        }

        _setupWindow = new SetupWindow(
            _monitors,
            _edges,
            _config.EdgePorts,
            _config.EnforcementMode,
            OnSetupSaved);
        _setupWindow.Closed += (_, _) =>
        {
            DiagnosticsLog.Write("SetupWindow closed.");
            _setupWindow = null;
        };
        _setupWindow.Show();
        _setupWindow.Activate();
        DiagnosticsLog.Write("SetupWindow opened.");
    }

    private void OnSetupSaved(IReadOnlyCollection<EdgePort> ports, EnforcementMode mode)
    {
        if (_config is null)
        {
            return;
        }

        _config.EdgePorts.Clear();
        foreach (var port in ports)
        {
            _config.EdgePorts.Add(new EdgePort
            {
                EdgeId = port.EdgeId,
                PortStart = port.PortStart,
                PortEnd = port.PortEnd
            });
        }

        _config.EnforcementMode = mode;
        _configService.Save(_config);
        ApplyEnabledState();
        UpdateTrayState();
        DiagnosticsLog.Write($"SetupWindow saved {ports.Count} edge ports. Mode={mode}");
    }

    private void ExitApplication()
    {
        DiagnosticsLog.Write("Exit clicked from tray.");
        System.Windows.Application.Current.Shutdown();
    }

    private void RefreshLayoutAndConfig()
    {
        _monitors = _layoutService.GetMonitors();
        _edges = _layoutService.GetSharedEdges(_monitors);
        _layoutId = _layoutService.ComputeLayoutId(_monitors);

        _config = _configService.Load(_layoutId) ?? _configService.BuildDefault(_layoutId, _edges);
        if (_config.EnforcementMode == EnforcementMode.Hook)
        {
            _config.EnforcementMode = EnforcementMode.ClipCursor;
            DiagnosticsLog.Write("Migrated enforcement mode to ClipCursor default.");
        }
        EnsurePortCoverage();
        _configService.Save(_config);
    }

    private void EnsurePortCoverage()
    {
        if (_config is null)
        {
            return;
        }

        var existing = _config.EdgePorts.ToDictionary(x => x.EdgeId, StringComparer.Ordinal);
        foreach (var edge in _edges)
        {
            if (existing.ContainsKey(edge.Id))
            {
                continue;
            }

            _config.EdgePorts.Add(new EdgePort
            {
                EdgeId = edge.Id,
                PortStart = edge.SegmentStart,
                PortEnd = edge.SegmentEnd
            });
        }
    }

    private void SystemEventsOnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.Invoke(() =>
        {
            var wasEnabled = _config?.EnforcementEnabled ?? true;
            _hookService.IsEnabled = false;

            RefreshLayoutAndConfig();
            if (_config is null)
            {
                return;
            }

            _config.EnforcementEnabled = wasEnabled;
            _configService.Save(_config);

            ApplyEnabledState();
            UpdateTrayState();
            ShowBalloon("MousePassport refreshed", "Display layout changed; monitor edges were recalculated.");
        });
    }

    private void UpdateTrayState()
    {
        var enabled = _config?.EnforcementEnabled ?? true;
        var mode = _config?.EnforcementMode ?? EnforcementMode.ClipCursor;
        _enabledItem.Checked = enabled;
        _trayIcon.Text = enabled
            ? $"MousePassport ({mode})"
            : $"MousePassport (Disabled/{mode})";
    }

    private void ShowBalloon(string title, string message)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(2000);
    }

    private static Icon LoadTrayIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetName().Name + ".Assets.tray.ico";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream != null)
        {
            try
            {
                return new Icon(stream);
            }
            catch
            {
                // fallback below
            }
        }
        return SystemIcons.Application;
    }
}
