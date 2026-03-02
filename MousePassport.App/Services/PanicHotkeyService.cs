using System.Windows.Interop;
using MousePassport.App.Interop;

namespace MousePassport.App.Services;

public sealed class PanicHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly int _hotkeyId = 0xBEEF;
    private bool _isRegistered;

    public PanicHotkeyService()
    {
        var parameters = new HwndSourceParameters("MousePassportHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0x800000
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler? PanicTriggered;

    public void Register()
    {
        if (_isRegistered)
        {
            return;
        }

        _isRegistered = NativeMethods.RegisterHotKey(
            _source.Handle,
            _hotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt,
            NativeMethods.VkPause);
    }

    public void Unregister()
    {
        if (!_isRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, _hotkeyId);
        _isRegistered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && wParam.ToInt32() == _hotkeyId)
        {
            PanicTriggered?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }
}
