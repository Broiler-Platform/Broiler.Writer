using System;
using System.Collections.Generic;
using System.Diagnostics;
using Broiler.Graphics;
using Broiler.Graphics.WebAssembly;
using Broiler.UI;

namespace Broiler.Writer.WebAssembly;

/// <summary>
/// Queued dispatcher that coalesces managed work onto the browser animation-frame tick. Posting a
/// callback schedules a frame; the frame drains the queue before rendering.
/// </summary>
internal sealed class BrowserUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = [];
    private readonly Action _schedule;

    internal BrowserUiDispatcher(Action schedule) => _schedule = schedule;

    public bool CheckAccess() => true;

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _queue.Enqueue(callback);
        _schedule();
    }

    internal int Drain()
    {
        int count = 0;
        while (_queue.TryDequeue(out Action? callback))
        {
            callback();
            count++;
        }

        return count;
    }
}

/// <summary>Monotonic clock backed by the managed stopwatch, matching the browser performance timeline.</summary>
internal sealed class BrowserUiClock : IUiClock
{
    private readonly long _started = Stopwatch.GetTimestamp();

    public UiTimestamp Now => new(Stopwatch.GetElapsedTime(_started));
}

/// <summary>
/// The Writer's <see cref="IUiHost"/>. Renders the session's <see cref="BRenderList"/> straight
/// through the direct-Canvas 2D backend (<see cref="BrowserCanvasRenderer"/>) — the production
/// presentation path — and bridges cursor, clipboard, caret, and system-settings host contracts to
/// the browser. Structurally identical to the Broiler.UI WebAssembly demo host.
/// </summary>
internal sealed class BrowserCanvasUiHost :
    IUiHost, IUiTextInputHost, IUiClipboardHost, IUiCursorHost, IUiSystemSettingsHost, IDisposable
{
    private readonly BrowserCanvasRenderer _renderer;
    private UiTextCaretInfo? _caret;
    private bool _clipboardEventActive;
    private string? _clipboardReadText;
    private string? _clipboardWriteText;
    private bool _disposed;

    internal BrowserCanvasUiHost(BrowserCanvasRenderer renderer, bool reducedMotion, bool darkScheme)
    {
        _renderer = renderer;
        Settings = UiSystemSettings.Default with
        {
            ReducedMotion = reducedMotion,
            ColorScheme = darkScheme ? UiColorScheme.Dark : UiColorScheme.Light,
        };
    }

    public BSize ViewportSize { get; private set; } = new(1280, 800);

    public double Scale { get; private set; } = 1;

    public UiSystemSettings Settings { get; private set; }

    /// <summary>Background color cleared behind the render list.</summary>
    internal BColor ClearColor { get; set; } = BColor.White;

    internal UiTextCaretInfo? CurrentCaret => _caret;

    internal long FrameIndex => _renderer.FrameIndex;

    public BRenderList CreateRenderList(int capacity = 0) => new(capacity);

    public void Invalidate(UiInvalidation invalidation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BrowserInterop.ScheduleFrame();
    }

    public void Present(BRenderList renderList)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(renderList);

        int backingWidth = Math.Max(1, (int)Math.Ceiling(ViewportSize.Width * Scale));
        int backingHeight = Math.Max(1, (int)Math.Ceiling(ViewportSize.Height * Scale));
        _renderer.PresentFrame(
            renderList,
            backingWidth,
            backingHeight,
            Scale,
            ViewportSize.Width,
            ViewportSize.Height,
            ClearColor);
    }

    public void PublishCaret(UiTextCaretInfo caret) => _caret = caret;

    public void ClearCaret(UiElement owner)
    {
        if (ReferenceEquals(_caret?.Owner, owner))
            _caret = null;
    }

    public bool TryGetText(out string text)
    {
        text = _clipboardEventActive ? _clipboardReadText ?? string.Empty : string.Empty;
        return _clipboardEventActive && _clipboardReadText is not null;
    }

    public void SetText(string text)
    {
        if (_clipboardEventActive)
            _clipboardWriteText = text ?? string.Empty;
    }

    public void SetCursor(UiCursorShape shape) => BrowserInterop.SetCursor(shape.ToString());

    internal void BeginFrame() => _caret = null;

    internal void ApplyColorScheme(bool darkScheme) =>
        Settings = Settings with { ColorScheme = darkScheme ? UiColorScheme.Dark : UiColorScheme.Light };

    internal void BeginClipboardEvent(string? readText)
    {
        _clipboardEventActive = true;
        _clipboardReadText = readText;
        _clipboardWriteText = null;
    }

    internal string EndClipboardEvent()
    {
        string result = _clipboardWriteText ?? string.Empty;
        _clipboardEventActive = false;
        _clipboardReadText = null;
        _clipboardWriteText = null;
        return result;
    }

    internal bool Resize(double width, double height, double dpr)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(dpr) ||
            width <= 0 || height <= 0 || dpr <= 0)
            return false;

        if (ViewportSize.Width.Equals(width) && ViewportSize.Height.Equals(height) && Scale.Equals(dpr))
            return true;

        ViewportSize = new BSize(width, height);
        Scale = dpr;
        BrowserInterop.ScheduleFrame();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _caret = null;
        _clipboardEventActive = false;
        _clipboardReadText = null;
        _clipboardWriteText = null;
    }
}
