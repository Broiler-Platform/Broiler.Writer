using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Graphics;
using Broiler.UI;

namespace Broiler.Writer;

/// <summary>
/// The Windows head's <see cref="WriterUiHost"/>. It adds the optional <see cref="IUiWindowHost"/>
/// capability, which is what lets a dialog break out of the main window into a real OS window the
/// user can move to another monitor (Broiler.UI ADR 0025 and 0026).
/// </summary>
/// <remarks>
/// The capability is added *here* rather than on <see cref="WriterUiHost"/> because a host either
/// offers it or does not: Broiler.UI discovers it with <c>Host is IUiWindowHost</c>, so a host that
/// implemented it and then failed would turn the documented fallback into an exception. The Linux,
/// Android and WebAssembly heads keep the plain host, do not answer the capability, and their
/// dialogs stay logical subwindows rendered inside the main viewport.
/// </remarks>
[SupportedOSPlatform("windows7.0")]
internal sealed class WriterWindowsUiHost : WriterUiHost, IUiWindowHost
{
    private readonly List<WriterHostWindow> _hostWindows = [];
    private readonly Func<BWindow?> _getOwnerWindow;
    private readonly Func<string?>? _getClipboardText;
    private readonly Action<string>? _setClipboardText;

    public WriterWindowsUiHost(
        Func<BSize> getViewportSize,
        Func<double> getScale,
        Action invalidate,
        Action<BRenderList> present,
        Func<BWindow?> getOwnerWindow,
        Func<string?>? getClipboardText = null,
        Action<string>? setClipboardText = null,
        Action<UiTextCaretInfo?>? caretChanged = null,
        Func<IBroilerRenderer?>? getRenderer = null)
        : base(getViewportSize, getScale, invalidate, present, getClipboardText, setClipboardText, caretChanged, getRenderer)
    {
        _getOwnerWindow = getOwnerWindow ?? throw new ArgumentNullException(nameof(getOwnerWindow));
        _getClipboardText = getClipboardText;
        _setClipboardText = setClipboardText;
    }

    public IUiHostWindow CreateHostWindow(UiHostWindowRequest request)
    {
        BWindow? owner = _getOwnerWindow();
        var window = new WriterHostWindow(ToScreenPlacement(owner, request), owner, _getClipboardText, _setClipboardText);
        _hostWindows.Add(window);
        window.Closed += (_, _) => _hostWindows.Remove(window);
        window.Show();
        return window;
    }

    /// <summary>
    /// Maps the requested placement from the owner's client coordinates onto the screen.
    ///
    /// Broiler.UI positions a dialog against the window it belongs to — <c>GetDialogPlacement</c>
    /// centers it in the main viewport — but a native window is placed on the desktop. Without the
    /// translation a dialog centered in a window that is itself halfway down a large monitor opens
    /// that far into the top-left corner of the screen instead. The client origin is in physical
    /// pixels and the placement is in device-independent ones, so it is scaled on the way through.
    /// </summary>
    private UiHostWindowRequest ToScreenPlacement(BWindow? owner, UiHostWindowRequest request)
    {
        if (request.Placement.IsEmpty)
            return request;

        IntPtr ownerHandle = owner?.NativeHandle ?? IntPtr.Zero;
        if (ownerHandle == IntPtr.Zero)
            return request;

        var origin = default(NativePoint);
        if (!ClientToScreen(ownerHandle, ref origin))
            return request;

        double scale = Scale > 0 ? Scale : 1;
        BRect placement = request.Placement;
        return request with
        {
            Placement = new BRect(
                (origin.X / scale) + placement.X,
                (origin.Y / scale) + placement.Y,
                placement.Width,
                placement.Height),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Disposing a host window destroys it, which raises Closed and removes it from the
            // list; iterate a copy so that does not mutate the collection underneath us.
            foreach (WriterHostWindow window in _hostWindows.ToArray())
                window.Dispose();

            _hostWindows.Clear();
        }

        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
