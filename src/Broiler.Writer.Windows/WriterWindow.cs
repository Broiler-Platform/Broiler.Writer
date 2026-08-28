using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.App;
using Broiler.Graphics;
using Broiler.Graphics.Windows;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.Standard;

namespace Broiler.Writer;

/// <summary>Win32/Direct2D host for the platform-neutral Writer application.</summary>
[SupportedOSPlatform("windows7.0")]
internal sealed class WriterWindow : Direct2DWindow
{
    private readonly WriterWindowsUiHost _host;
    private readonly WriterApp _app;

    // Created in OnCreated: the Win32 clipboard is addressed by window handle,
    // and there is no handle until the window exists.
    private WindowsClipboard? _clipboard;

#pragma warning disable CS0618
    private readonly StandardLegacyGraphicsInputAdapter _legacyInput = new("broiler-writer");
#pragma warning restore CS0618

    public WriterWindow(WriterDocumentFormats documentFormats)
        : base(new BWindowOptions
        {
            Title = "Broiler Writer",
            ClientWidth = 1120,
            ClientHeight = 780,
            ClearColor = WriterPalette.Canvas,
            RenderOptions = new BRenderOptions(Antialias: true, VSync: true, SubpixelText: true),
        })
    {
        // The Windows host, not the plain one: it offers IUiWindowHost, so a dialog breaks out
        // into its own OS window instead of rendering inside this one.
        _host = new WriterWindowsUiHost(
            () => ClientSize,
            () => DpiScale,
            Invalidate,
            static _ => { },
            () => NativeHandle,
            ReadClipboardText,
            WriteClipboardText,
            getRenderer: () => Renderer);
        _app = new WriterApp(_host, CloseNativeWindow, documentFormats: documentFormats);
    }

    protected override void OnCreated() => _clipboard = new WindowsClipboard(NativeHandle);

    /// <summary>
    /// Null rather than empty when there is no clipboard to read: the host
    /// treats that as "this machine offers none", which is what the window
    /// reports before it exists and if the OS refuses the clipboard.
    /// </summary>
    private string? ReadClipboardText() =>
        _clipboard is not null && _clipboard.TryGetText(out string text) ? text : null;

    private void WriteClipboardText(string text) => _clipboard?.SetText(text);

    protected override BRenderList? BuildRenderList(BSize clientSize) => _app.RenderFrame();

    protected override void OnResized(BSize clientSize, double dpiScale) => _app.Invalidate();

    protected override void OnPointerDown(BPointerEventArgs e) =>
        Dispatch(_legacyInput.FromPointerButton(e));

    protected override void OnPointerMove(BPointerEventArgs e) =>
        Dispatch(_legacyInput.FromPointerMove(e));

    protected override void OnPointerUp(BPointerEventArgs e) =>
        Dispatch(_legacyInput.FromPointerButton(e));

    protected override void OnMouseWheel(BMouseWheelEventArgs e) =>
        Dispatch(_legacyInput.FromMouseWheel(e));

    protected override void OnKeyDown(BKeyEventArgs e) =>
        Dispatch(_legacyInput.FromKey(e, KeyboardKeyTransition.Down));

    protected override void OnKeyUp(BKeyEventArgs e) =>
        Dispatch(_legacyInput.FromKey(e, KeyboardKeyTransition.Up));

    protected override void OnTextInput(BTextInputEventArgs e) =>
        Dispatch(_legacyInput.FromText(e));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The app first: disposing its session closes any dialog that is still open, which
            // tears down the host window it broke out into. The host then only has to sweep up
            // whatever survived that.
            _app.Dispose();
            _host.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CloseNativeWindow()
    {
        if (!PostToUiThread(CloseNativeWindowNow))
            CloseNativeWindowNow();
    }

    private void CloseNativeWindowNow()
    {
        if (NativeHandle != IntPtr.Zero)
            _ = DestroyWindow(NativeHandle);
    }

    private void Dispatch(UiInputEvent input) => _app.Dispatch(input);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
