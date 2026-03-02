using MousePassport.App.Interop;

namespace MousePassport.App;

public partial class App : System.Windows.Application
{
    private AppController? _controller;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Ensure monitor coordinates are not DPI-virtualized before we read layout.
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);

        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _controller = new AppController();
        _controller.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}

