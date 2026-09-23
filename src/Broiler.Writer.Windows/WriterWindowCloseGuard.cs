using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Broiler.Writer;

/// <summary>
/// Intercepts WM_CLOSE before Direct2DWindow's main-window message loop destroys the window.
/// The app calls DestroyWindow only after resolving any unsaved document changes.
/// </summary>
internal sealed class WriterWindowCloseGuard : IDisposable
{
    private readonly Action _requestClose;
    private readonly SubclassProc _callback;
    private IntPtr _window;

    public WriterWindowCloseGuard(IntPtr window, Action requestClose)
    {
        _window = window;
        _requestClose = requestClose;
        _callback = HandleMessage;
        if (!SetWindowSubclass(window, _callback, 1, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_window == IntPtr.Zero)
            return;
        RemoveWindowSubclass(_window, _callback, 1);
        _window = IntPtr.Zero;
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, nuint referenceData)
    {
        if (message == 0x0010) // WM_CLOSE, including Alt+F4 and the system menu.
        {
            _requestClose();
            return IntPtr.Zero;
        }
        if (message == 0x0082) // WM_NCDESTROY
            Dispose();
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
