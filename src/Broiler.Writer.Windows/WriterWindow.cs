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
    /// <summary>How often to look at the clock while a tooltip is waiting. Roughly 30 Hz.</summary>
    private const double TooltipTickMilliseconds = 33;

    /// <summary>The icon is drawn once at this size and let the shell scale it down.</summary>
    private const int AppIconPixels = 64;

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
            () => this,
            ReadClipboardText,
            WriteClipboardText,
            getRenderer: () => Renderer);
        // The document's name belongs in the caption, so the Writer is given a way to put it there.
        // SetTitle is safe from here: the app only ever renames from the UI thread.
        _app = new WriterApp(
            _host,
            CloseNativeWindow,
            documentFormats: documentFormats,
            setWindowTitle: SetTitle,
            setTicking: SetTicking);
    }

    protected override void OnCreated()
    {
        _clipboard = new WindowsClipboard(NativeHandle);

        // The app named the document before this window existed, and SetTitle is a no-op until it
        // does, so the caption is pushed once more now that there is something to push it to.
        SetTitle(_app.WindowTitle);

        // The window carried the runtime's default icon, which says .NET rather than Writer. It is
        // drawn from the same geometry as the toolbar icons rather than shipped as an .ico: one
        // description, every size, no binary in the tree.
        SetIcon(WriterApp.CreateAppIcon(AppIconPixels));
    }

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

    /// <summary>
    /// Runs a timer only while something is waiting on the clock - today, a tooltip counting out
    /// its delay. A word processor draws when the document changes and not otherwise, so without a
    /// tick the delay would never elapse; with a permanent one the window would redraw sixty times
    /// a second to watch a pointer that is not moving.
    /// </summary>
    private void SetTicking(bool ticking)
    {
        if (ticking)
            StartAnimationTimer(TooltipTickMilliseconds);
        else
            StopAnimationTimer();
    }

    protected override void OnAnimationTick() => _app.AnimationTick();

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
