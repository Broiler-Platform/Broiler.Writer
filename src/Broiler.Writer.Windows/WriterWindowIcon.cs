using System;
using System.Runtime.InteropServices;

namespace Broiler.Writer;

/// <summary>
/// Puts the icon the build embeds in the executable (<c>ApplicationIcon</c>) on the main window,
/// and reloads it at the right size when the window moves to a monitor with another DPI.
/// </summary>
/// <remarks>
/// Embedding an icon does not put it on the window. The executable's icon is what Explorer, Start
/// and a pinned shortcut show; the taskbar button and Alt+Tab show the window's big icon, and the
/// caption and its system menu the small one. Direct2DWindow registers its window class with
/// neither, so a window that is not handed an icon gets Windows' generic glyph in its caption, and
/// only the taskbar falls back to the executable's.
/// </remarks>
internal sealed class WriterWindowIcon : IDisposable
{
    /// <summary>
    /// The resource id the C# compiler gives <c>ApplicationIcon</c> (IDI_APPLICATION). The apphost
    /// and a NativeAOT publish both carry it over into the executable.
    /// </summary>
    private const int ApplicationIconId = 32512;

    private const uint WmSetIcon = 0x0080;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmDpiChanged = 0x02E0;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint ImageIcon = 1;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private readonly SubclassProc _callback;
    private IntPtr _window;
    private IntPtr _big;
    private IntPtr _small;

    private WriterWindowIcon(IntPtr window)
    {
        _window = window;
        _callback = HandleMessage;
    }

    /// <summary>
    /// Gives <paramref name="window"/> the executable's icon, or returns null and leaves the window
    /// alone when the process was not started from the Writer's own executable - under dotnet.exe,
    /// whose icon is not stored under that id.
    /// </summary>
    public static WriterWindowIcon? TryAttach(IntPtr window)
    {
        var icon = new WriterWindowIcon(window);
        if (!icon.Load(DpiOf(window)))
            return null;

        // The subclass only follows DPI changes and cleans up at WM_NCDESTROY. Without it the icon
        // still shows and Dispose still destroys it, which is no reason to fail the window over.
        _ = SetWindowSubclass(window, icon._callback, 1, 0);
        return icon;
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            RemoveWindowSubclass(_window, _callback, 1);

            // Still on show: take the icons off the window before destroying them.
            SendMessage(_window, WmSetIcon, new IntPtr(IconBig), IntPtr.Zero);
            SendMessage(_window, WmSetIcon, new IntPtr(IconSmall), IntPtr.Zero);
            _window = IntPtr.Zero;
        }

        DestroyIcons(_big, _small);
        _big = IntPtr.Zero;
        _small = IntPtr.Zero;
    }

    /// <summary>
    /// Loads both icons at the sizes <paramref name="dpi"/> asks for and swaps them in. Each size is
    /// loaded from the icon resource rather than scaled from the other, so Windows picks the image
    /// closest to it.
    /// </summary>
    private bool Load(uint dpi)
    {
        IntPtr module = GetModuleHandle(IntPtr.Zero);
        IntPtr big = LoadImage(module, new IntPtr(ApplicationIconId), ImageIcon,
            MetricForDpi(SmCxIcon, dpi), MetricForDpi(SmCyIcon, dpi), 0);
        IntPtr small = LoadImage(module, new IntPtr(ApplicationIconId), ImageIcon,
            MetricForDpi(SmCxSmIcon, dpi), MetricForDpi(SmCySmIcon, dpi), 0);

        if (big == IntPtr.Zero || small == IntPtr.Zero)
        {
            DestroyIcons(big, small);
            return false;
        }

        SendMessage(_window, WmSetIcon, new IntPtr(IconBig), big);
        SendMessage(_window, WmSetIcon, new IntPtr(IconSmall), small);

        DestroyIcons(_big, _small);
        _big = big;
        _small = small;
        return true;
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, nuint referenceData)
    {
        if (message == WmDpiChanged)
        {
            // Keep what is showing if the reload fails; a wrong size beats no icon.
            _ = Load(unchecked((uint)((long)wParam & 0xFFFF)));
        }
        else if (message == WmNcDestroy)
        {
            // The window is going, and the icons with it.
            RemoveWindowSubclass(window, _callback, 1);
            _window = IntPtr.Zero;
            Dispose();
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static void DestroyIcons(IntPtr big, IntPtr small)
    {
        if (big != IntPtr.Zero)
            DestroyIcon(big);
        if (small != IntPtr.Zero)
            DestroyIcon(small);
    }

    /// <summary>
    /// The DPI the window is shown at. Windows before 10 1607 has no per-window DPI, and
    /// <see cref="MetricForDpi"/> does not use one there.
    /// </summary>
    private static uint DpiOf(IntPtr window)
    {
        uint dpi = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? GetDpiForWindow(window) : 0;
        return dpi == 0 ? 96u : dpi;
    }

    private static int MetricForDpi(int index, uint dpi) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)
            ? GetSystemMetricsForDpi(index, dpi)
            : GetSystemMetrics(index);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
