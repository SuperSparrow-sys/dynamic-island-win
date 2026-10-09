using System.Windows.Interop;

namespace DynamicBay.Core;

/// <summary>Hidden window that receives clipboard, hotkey and display-change messages.</summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;
    public IntPtr Handle => _source.Handle;

    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("DynamicBay.Messages") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Message is not null)
        {
            foreach (Func<int, IntPtr, IntPtr, bool> h in Message.GetInvocationList())
                if (h(msg, wParam, lParam)) handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
