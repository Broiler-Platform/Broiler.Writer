using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Broiler.Graphics.WebAssembly;

namespace Broiler.Writer.WebAssembly;

/// <summary>
/// Owns the Writer lifecycle: loads the direct-Canvas 2D replay module, binds the presenter to the
/// page canvas, builds the <see cref="BrowserWriterDemo"/>, and routes the page's input, resize,
/// document, and teardown calls to it. Mirrors the role of <c>Program</c>/<c>WriterWindow.Run</c> in
/// the desktop Writer.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BrowserWriterApp
{
    private const string CanvasSelector = "#writer-canvas";

    // The initial logical box before the page reports its measured size via ResizeUi.
    private const double InitialWidth = 1280;
    private const double InitialHeight = 800;

    private static BrowserCanvasRenderer? _renderer;
    private static BrowserWriterDemo? _writer;

    internal static async Task StartAsync()
    {
        // The managed JSHost.ImportAsync runs its dynamic import() from inside _framework/, so a
        // "./" specifier resolves there and 404s. The replay module publishes to the app root and is
        // fingerprinted in the page import map by its root URL, so import it with "../".
        await BrowserCanvasRenderer.LoadModuleAsync("../broiler.graphics.webassembly.js");

        var renderer = new BrowserCanvasRenderer();
        renderer.Initialize(CanvasSelector);
        _renderer = renderer;

        _writer = new BrowserWriterDemo(
            renderer,
            reducedMotion: BrowserInterop.PrefersReducedMotion(),
            darkScheme: BrowserInterop.PrefersDarkScheme(),
            InitialWidth,
            InitialHeight,
            dpr: 1);

        _writer.RenderScheduledFrame();
        BrowserInterop.Ready(_writer.ViewportWidth, _writer.ViewportHeight);
    }

    internal static void Resize(double width, double height, double dpr) => Run(demo => demo.Resize(width, height, dpr));

    internal static void RenderFrame() => Run(static demo => demo.RenderScheduledFrame());

    internal static void PointerMove(double x, double y, int buttons, double timestampMilliseconds) =>
        Run(demo => demo.PointerMove(x, y, buttons, timestampMilliseconds));

    internal static void PointerButton(double x, double y, int buttons, int button, bool down, double timestampMilliseconds) =>
        Run(demo => demo.PointerButton(x, y, buttons, button, down, timestampMilliseconds));

    internal static void PointerWheel(double x, double y, int buttons, bool horizontal, double deltaNotches, double timestampMilliseconds) =>
        Run(demo => demo.PointerWheel(x, y, buttons, horizontal, deltaNotches, timestampMilliseconds));

    internal static void KeyboardKey(string keyName, bool down, int modifiers, int nativeKeyCode, bool repeat, int location, double timestampMilliseconds) =>
        Run(demo => demo.KeyboardKey(keyName, down, modifiers, nativeKeyCode, repeat, location, timestampMilliseconds));

    internal static void TextInput(string text, double timestampMilliseconds) =>
        Run(demo => demo.TextInput(text, timestampMilliseconds));

    internal static void TextComposition(string text, int state, int selectionStart, int selectionLength, double timestampMilliseconds) =>
        Run(demo => demo.TextComposition(text, state, selectionStart, selectionLength, timestampMilliseconds));

    internal static string ClipboardEvent(string operation, string text)
    {
        try
        {
            return _writer?.ClipboardEvent(operation, text) ?? string.Empty;
        }
        catch (Exception exception)
        {
            BrowserInterop.Failed(exception.GetType().Name, exception.ToString());
            return string.Empty;
        }
    }

    internal static void LoadDocument(string fileName, string base64Data) =>
        Run(demo => demo.LoadDocument(fileName, base64Data));

    internal static void CancelPointer(double timestampMilliseconds) => Run(demo => demo.CancelPointer(timestampMilliseconds));

    internal static void Dispose()
    {
        BrowserWriterDemo? writer = _writer;
        _writer = null;
        writer?.Dispose();
        BrowserCanvasRenderer? renderer = _renderer;
        _renderer = null;
        renderer?.Dispose();
    }

    private static void Run(Action<BrowserWriterDemo> action)
    {
        try
        {
            if (_writer is BrowserWriterDemo demo)
                action(demo);
        }
        catch (Exception exception)
        {
            BrowserInterop.Failed(exception.GetType().Name, exception.ToString());
        }
    }
}

/// <summary>Page-to-managed entry points invoked by <c>main.js</c>.</summary>
internal static partial class BrowserWriterExports
{
    [JSExport]
    internal static void ResizeUi(double logicalWidth, double logicalHeight, double dpr) =>
        BrowserWriterApp.Resize(logicalWidth, logicalHeight, dpr);

    [JSExport]
    internal static void RenderUiFrame() => BrowserWriterApp.RenderFrame();

    [JSExport]
    internal static void UiPointerMove(double x, double y, int buttons, double timestampMilliseconds) =>
        BrowserWriterApp.PointerMove(x, y, buttons, timestampMilliseconds);

    [JSExport]
    internal static void UiPointerButton(double x, double y, int buttons, int button, bool down, double timestampMilliseconds) =>
        BrowserWriterApp.PointerButton(x, y, buttons, button, down, timestampMilliseconds);

    [JSExport]
    internal static void UiPointerWheel(double x, double y, int buttons, bool horizontal, double deltaNotches, double timestampMilliseconds) =>
        BrowserWriterApp.PointerWheel(x, y, buttons, horizontal, deltaNotches, timestampMilliseconds);

    [JSExport]
    internal static void UiKeyboardKey(string keyName, bool down, int modifiers, int nativeKeyCode, bool repeat, int location, double timestampMilliseconds) =>
        BrowserWriterApp.KeyboardKey(keyName, down, modifiers, nativeKeyCode, repeat, location, timestampMilliseconds);

    [JSExport]
    internal static void UiTextInput(string text, double timestampMilliseconds) =>
        BrowserWriterApp.TextInput(text, timestampMilliseconds);

    [JSExport]
    internal static void UiTextComposition(string text, int state, int selectionStart, int selectionLength, double timestampMilliseconds) =>
        BrowserWriterApp.TextComposition(text, state, selectionStart, selectionLength, timestampMilliseconds);

    [JSExport]
    internal static string UiClipboardEvent(string operation, string text) => BrowserWriterApp.ClipboardEvent(operation, text);

    [JSExport]
    internal static void LoadDocument(string fileName, string base64Data) =>
        BrowserWriterApp.LoadDocument(fileName, base64Data);

    [JSExport]
    internal static void UiCancelPointer(double timestampMilliseconds) =>
        BrowserWriterApp.CancelPointer(timestampMilliseconds);

    [JSExport]
    internal static void Dispose() => BrowserWriterApp.Dispose();
}
