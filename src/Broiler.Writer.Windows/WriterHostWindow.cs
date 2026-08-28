using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Graphics;
using Broiler.Graphics.Windows;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.Standard;

namespace Broiler.Writer;

/// <summary>
/// A second native window hosting a broken-out Writer dialog (Broiler.UI ADR 0025 and 0026). It is
/// a <see cref="Direct2DWindow"/> that does not own the thread's message loop — the main window's
/// loop services it, so closing a dialog does not quit the Writer — and it exposes the neutral
/// <see cref="IUiHostWindow"/> and <see cref="IUiWindowChromeHost"/> contracts so a
/// <see cref="UiSession"/> can render into it, take its input, and drive its title bar.
/// </summary>
/// <remarks>
/// Created with <see cref="BWindowChrome.Owner"/>, so Windows draws no caption and the dialog keeps
/// the single title bar it already draws for itself.
///
/// Everything a host has to answer beyond the window — render lists, the clipboard, the caret,
/// image upload — is delegated to a <see cref="WriterUiHost"/> built over this window, so a dialog
/// behaves exactly as it did while it rendered inside the main window. In particular the clipboard
/// accessors are the main window's, which is what keeps Ctrl+V in a file dialog's name box talking
/// to the real Windows clipboard rather than to a private string.
/// </remarks>
[SupportedOSPlatform("windows7.0")]
internal sealed class WriterHostWindow : Direct2DWindow, IUiHostWindow, IUiWindowChromeHost,
    IUiClipboardHost, IUiTextInputHost, IUiImageHost
{
    private readonly WriterUiHost _host;
    private UiSession? _session;

#pragma warning disable CS0618
    private readonly StandardLegacyGraphicsInputAdapter _legacyInput = new("broiler-writer-dialog");
#pragma warning restore CS0618

    public WriterHostWindow(
        UiHostWindowRequest request,
        Func<string?>? getClipboardText,
        Action<string>? setClipboardText)
        : base(new BWindowOptions
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Broiler Writer" : request.Title,
            ClientWidth = ToClientExtent(request.Placement.Width, 640),
            ClientHeight = ToClientExtent(request.Placement.Height, 420),
            Left = request.Placement.IsEmpty ? null : request.Placement.X,
            Top = request.Placement.IsEmpty ? null : request.Placement.Y,
            ClearColor = WriterPalette.Canvas,
            RenderOptions = new BRenderOptions(Antialias: true, VSync: true, SubpixelText: true),
            OwnsMessageLoop = false,
            Chrome = request.Chrome == UiHostWindowChrome.Owner ? BWindowChrome.Owner : BWindowChrome.System,
            Resizable = request.Resizable,
        })
    {
        _host = new WriterUiHost(
            () => ClientSize,
            () => DpiScale,
            InvalidateIfAlive,
            static _ => { },
            getClipboardText,
            setClipboardText,
            getRenderer: () => Renderer);

        StateChanged += (_, _) => WindowStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // IUiHost - forwarded so this window answers exactly as the main window's host does.
    BSize IUiHost.ViewportSize => _host.ViewportSize;

    double IUiHost.Scale => _host.Scale;

    BRenderList IUiHost.CreateRenderList(int capacity) => _host.CreateRenderList(capacity);

    void IUiHost.Invalidate(UiInvalidation invalidation) => _host.Invalidate(invalidation);

    void IUiHost.Present(BRenderList renderList) => _host.Present(renderList);

    // IUiHostWindow. SetTitle and CloseRequested come from BWindow, which already reports a
    // secondary window's WM_CLOSE as a request instead of destroying the window itself.
    public void Bind(UiSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InvalidateIfAlive();
    }

    public void Activate()
    {
        if (IsDisposed || NativeHandle == IntPtr.Zero)
            return;

        SetForegroundWindow(NativeHandle);
        InvalidateIfAlive();
    }

    // IUiWindowChromeHost
    public event EventHandler? WindowStateChanged;

    UiHostWindowChrome IUiWindowChromeHost.Chrome =>
        Options.Chrome == BWindowChrome.Owner ? UiHostWindowChrome.Owner : UiHostWindowChrome.System;

    bool IUiWindowChromeHost.IsResizable => Options.Resizable;

    UiHostWindowState IUiWindowChromeHost.WindowState => ToHostState(WindowState);

    void IUiWindowChromeHost.SetWindowState(UiHostWindowState state) => SetWindowState(state switch
    {
        UiHostWindowState.Minimized => BWindowState.Minimized,
        UiHostWindowState.Maximized => BWindowState.Maximized,
        _ => BWindowState.Normal,
    });

    void IUiWindowChromeHost.SetIcon(BPixelBuffer? icon) => SetIcon(icon);

    void IUiWindowChromeHost.RequestClose() => Close();

    void IUiWindowChromeHost.BeginMoveDrag() => BeginMoveDrag();

    void IUiWindowChromeHost.BeginResizeDrag(UiWindowEdge edge) => BeginResizeDrag(ToWindowEdge(edge));

    // IUiClipboardHost / IUiTextInputHost / IUiImageHost
    public bool TryGetText(out string text) => _host.TryGetText(out text);

    public void SetText(string text) => _host.SetText(text);

    public void PublishCaret(UiTextCaretInfo caret) => _host.PublishCaret(caret);

    public void ClearCaret(UiElement owner) => _host.ClearCaret(owner);

    public BImageHandle CreateImage(ReadOnlySpan<byte> encodedImage) => _host.CreateImage(encodedImage);

    public void ReleaseImage(BImageHandle image) => _host.ReleaseImage(image);

    protected override BRenderList? BuildRenderList(BSize clientSize) =>
        _session is { IsDisposed: false } session ? session.RenderFrame() : null;

    protected override void OnResized(BSize clientSize, double dpiScale) => InvalidateIfAlive();

    protected override void OnPointerDown(BPointerEventArgs e) => Dispatch(_legacyInput.FromPointerButton(e));

    protected override void OnPointerMove(BPointerEventArgs e) => Dispatch(_legacyInput.FromPointerMove(e));

    protected override void OnPointerUp(BPointerEventArgs e) => Dispatch(_legacyInput.FromPointerButton(e));

    protected override void OnMouseWheel(BMouseWheelEventArgs e) => Dispatch(_legacyInput.FromMouseWheel(e));

    protected override void OnKeyDown(BKeyEventArgs e) => Dispatch(_legacyInput.FromKey(e, KeyboardKeyTransition.Down));

    protected override void OnKeyUp(BKeyEventArgs e) => Dispatch(_legacyInput.FromKey(e, KeyboardKeyTransition.Up));

    protected override void OnTextInput(BTextInputEventArgs e) => Dispatch(_legacyInput.FromText(e));

    protected override void Dispose(bool disposing)
    {
        // Destroy the native window when the framework disposes this host window.
        if (disposing && !IsDisposed)
            Close();

        base.Dispose(disposing);
    }

    private static UiHostWindowState ToHostState(BWindowState state) => state switch
    {
        BWindowState.Minimized => UiHostWindowState.Minimized,
        BWindowState.Maximized => UiHostWindowState.Maximized,
        _ => UiHostWindowState.Normal,
    };

    private static BWindowEdge ToWindowEdge(UiWindowEdge edge) => edge switch
    {
        UiWindowEdge.Left => BWindowEdge.Left,
        UiWindowEdge.Top => BWindowEdge.Top,
        UiWindowEdge.Right => BWindowEdge.Right,
        UiWindowEdge.Bottom => BWindowEdge.Bottom,
        UiWindowEdge.TopLeft => BWindowEdge.TopLeft,
        UiWindowEdge.TopRight => BWindowEdge.TopRight,
        UiWindowEdge.BottomLeft => BWindowEdge.BottomLeft,
        UiWindowEdge.BottomRight => BWindowEdge.BottomRight,
        _ => BWindowEdge.None,
    };

    /// <summary>
    /// Routes native input into the hosted session. Dispatching can close the dialog — its Cancel
    /// button, or the owner-drawn close button — which disposes the session *and* this window while
    /// the call is still on the stack, so nothing here may assume it is still alive once
    /// <see cref="UiSession.DispatchInput"/> returns.
    /// </summary>
    private void Dispatch(UiInputEvent input)
    {
        if (_session is null || _session.IsDisposed)
            return;

        if (_session.DispatchInput(input))
            InvalidateIfAlive();
    }

    /// <summary>Repaints, unless this window is already gone. See <see cref="Dispatch"/>.</summary>
    private void InvalidateIfAlive()
    {
        if (!IsDisposed && NativeHandle != IntPtr.Zero)
            Invalidate();
    }

    private static int ToClientExtent(double requested, int fallback) =>
        requested > 1 ? (int)Math.Round(requested) : fallback;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
