using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics;
using Broiler.Graphics.Linux;
using Broiler.Graphics.Linux.OpenGL;
using Broiler.App;

namespace Broiler.Writer;

internal static class LinuxWriterRunner
{
    private static readonly TimeSpan InteractivePollInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan OffscreenPollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly BRenderOptions RenderOptions = new(Antialias: true, VSync: true, SubpixelText: true);

    public static async Task<int> RunAsync(
        LinuxWriterOptions options,
        WriterDocumentFormats documentFormats,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(documentFormats);

        Console.WriteLine("Broiler Writer");
        Console.WriteLine();
        PrintRuntime(LinuxGraphicsRuntimeDiagnostics.Capture());
        Print("Windowing baseline", LinuxGraphicsDependencies.CheckWindowingBaseline());
        Print("OpenGL", LinuxOpenGlRenderer.CheckDependencies());

        bool closeRequested = false;
        long frameIndex = 0;
        long renderTicks = 0;
        int renderedFrames = 0;
        BFrameContext lastFrameContext = BFrameContext.Default;

        using LinuxOpenGlRenderer renderer = new();
        BSurfaceDescriptor descriptor = BSurfaceDescriptor.Default(new BSize(options.Width, options.Height));
        using IBroilerSurface surface = options.OpenWindow
            ? renderer.CreateX11WindowSurface(descriptor, "Broiler Writer")
            : renderer.CreateSurface(descriptor);

        LinuxOpenGlX11WindowSurface? x11Window = surface as LinuxOpenGlX11WindowSurface;
        bool canUseEvdev = options.EnableEvdevInput && x11Window is not null;
        if (options.EnableEvdevInput && x11Window is null)
            Console.WriteLine("evdev input requested, but no focus-capable X11 window exists; input is disabled.");

        // The X11 clipboard, on its own connection. Absent when running
        // offscreen with no display, and then copy and paste report themselves
        // unavailable rather than silently working only inside this process.
        using LinuxX11Clipboard? clipboard = x11Window is not null ? LinuxX11Clipboard.TryOpen() : null;
        if (x11Window is not null && clipboard is null)
            Console.WriteLine("No X11 clipboard is available; copy and paste are disabled.");

        using WriterUiHost host = new(
            () => surface.Size,
            () => surface.DpiScale,
            static () => { },
            renderList =>
            {
                lastFrameContext = new BFrameContext(WriterPalette.Canvas, frameIndex++, RenderOptions);
                renderer.Render(surface, renderList, lastFrameContext);
            },
            () => clipboard is not null && clipboard.TryGetText(out string text) ? text : null,
            text => clipboard?.SetText(text),
            getRenderer: () => renderer);
        // The X11 window can be renamed now, so the Linux head tracks the open document in its
        // title bar the way the Windows one does. Offscreen there is no window to rename, and the
        // callback is simply absent rather than a no-op that pretends otherwise.
        using WriterApp app = new(
            host,
            () => closeRequested = true,
            documentFormats: documentFormats,
            setWindowTitle: x11Window is null ? null : x11Window.SetTitle);

        await using LinuxInputCoordinator input = new(canUseEvdev, Console.WriteLine, externalPointer: x11Window is not null);
        await input.InitializeAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset start = DateTimeOffset.UtcNow;
        using PeriodicTimer timer = new(options.OpenWindow ? InteractivePollInterval : OffscreenPollInterval);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool processedWindowEvents = false;

            // An X11 copy is a promise to answer requests: between these calls,
            // another application pasting from Writer sees an empty clipboard.
            clipboard?.ProcessPendingEvents();
            if (x11Window is not null)
            {
                processedWindowEvents = x11Window.ProcessPendingEvents();
                bool inputActive = options.IgnoreInputFocus || x11Window.IsFocused;
                await input.SetActiveAsync(inputActive, cancellationToken).ConfigureAwait(false);
            }

            input.SetViewport(host.ViewportSize);
            if (x11Window is not null && x11Window.TryGetPointerPosition(out int pointerX, out int pointerY, out _))
            {
                double scale = host.Scale <= 0 ? 1.0 : host.Scale;
                input.SetExternalPointer(pointerX / scale, pointerY / scale);
            }

            input.Drain(app.Dispatch);

            if (host.IsInvalidated || processedWindowEvents)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                app.RenderFrame();
                stopwatch.Stop();
                renderTicks += stopwatch.ElapsedTicks;
                renderedFrames++;
            }

            if (!options.OpenWindow)
                break;

            if (!options.RunUntilClose &&
                (DateTimeOffset.UtcNow - start).TotalMilliseconds >= options.DurationMilliseconds)
            {
                break;
            }
        }
        while (!closeRequested &&
               !input.QuitRequested &&
               (x11Window is null || !x11Window.IsCloseRequested) &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        await input.SetActiveAsync(false, cancellationToken).ConfigureAwait(false);
        using BBitmap bitmap = ReadSurface(surface);
        Console.WriteLine("Writer presentation:");
        Console.WriteLine("  presentation: " + PresentationState(surface));
        Console.WriteLine("  diagnostic: " + SurfaceDiagnostic(surface));
        Console.WriteLine("  bitmap: " + bitmap.Width.ToString(CultureInfo.InvariantCulture) + "x" + bitmap.Height.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  render-time: " + FormatMilliseconds(renderTicks) + " ms total across " + renderedFrames.ToString(CultureInfo.InvariantCulture) + " frame(s)");
        Console.WriteLine("  input: " + InputSummary(input.Snapshot));
        SaveArtifacts(options, bitmap, host.LastRenderList, descriptor, lastFrameContext);
        Console.WriteLine();
        return 0;
    }

    private static BBitmap ReadSurface(IBroilerSurface surface) =>
        surface switch
        {
            LinuxOpenGlSurface openGlSurface => openGlSurface.ReadToBitmap(),
            LinuxOpenGlX11WindowSurface x11Surface => x11Surface.ReadToBitmap(),
            _ => throw new InvalidOperationException("Unexpected Linux writer surface."),
        };

    private static string PresentationState(IBroilerSurface surface) =>
        surface switch
        {
            LinuxOpenGlSurface openGlSurface => openGlSurface.IsGpuBacked ? "EGL/OpenGL pbuffer" : "CPU fallback",
            LinuxOpenGlX11WindowSurface x11Surface => x11Surface.IsGpuBacked ? "X11/EGL/OpenGL window" : "CPU fallback",
            _ => "unknown",
        };

    private static string SurfaceDiagnostic(IBroilerSurface surface) =>
        surface switch
        {
            LinuxOpenGlSurface openGlSurface => openGlSurface.Diagnostic,
            LinuxOpenGlX11WindowSurface x11Surface => x11Surface.Diagnostic,
            _ => string.Empty,
        };

    private static void SaveArtifacts(
        LinuxWriterOptions options,
        BBitmap backendBitmap,
        BRenderList? renderList,
        BSurfaceDescriptor descriptor,
        BFrameContext frameContext)
    {
        if (options.ArtifactDirectory is null)
            return;

        Directory.CreateDirectory(options.ArtifactDirectory);
        string backendPath = Path.Combine(options.ArtifactDirectory, "broiler-writer-linux-opengl.png");
        backendBitmap.Save(backendPath);

        if (renderList is not null)
        {
            using BImageRenderer cpu = new();
            using BBitmap cpuBitmap = cpu.RenderToImage(renderList, descriptor, frameContext);
            string cpuPath = Path.Combine(options.ArtifactDirectory, "broiler-writer-linux-cpu.png");
            cpuBitmap.Save(cpuPath);
            Console.WriteLine("  artifacts: " + cpuPath + "; " + backendPath);
            return;
        }

        Console.WriteLine("  artifact: " + backendPath);
    }

    private static string InputSummary(LinuxWriterInputSnapshot input)
    {
        if (!input.Enabled)
            return "disabled";
        if (!input.Initialized)
            return "requested but not opened";

        string keyboard = input.KeyboardDevice ?? "none";
        string mouse = input.MouseDevice ?? "none";
        return "active=" + input.Active.ToString(CultureInfo.InvariantCulture) +
            ", keyboard=" + keyboard +
            ", mouse=" + mouse +
            ", keys=" + input.KeyEvents.ToString(CultureInfo.InvariantCulture) +
            ", text=" + input.TextEvents.ToString(CultureInfo.InvariantCulture) +
            ", moves=" + input.MouseMoveEvents.ToString(CultureInfo.InvariantCulture) +
            ", buttons=" + input.MouseButtonEvents.ToString(CultureInfo.InvariantCulture) +
            ", wheels=" + input.MouseWheelEvents.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatMilliseconds(long ticks) =>
        (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.###", CultureInfo.InvariantCulture);

    private static void PrintRuntime(LinuxGraphicsRuntimeReport report)
    {
        Console.WriteLine("Runtime:");
        Console.WriteLine("  os: " + report.OperatingSystemDescription);
        Console.WriteLine("  framework: " + report.FrameworkDescription);
        Console.WriteLine("  arch: " + report.ProcessArchitecture);
        Console.WriteLine("  rid: " + report.RuntimeIdentifier);
        Console.WriteLine("  display: " + report.DisplayServer.Diagnostic);
        Console.WriteLine();
    }

    private static void Print(string title, System.Collections.Generic.IReadOnlyList<LinuxNativeLibraryStatus> statuses)
    {
        Console.WriteLine(title + ":");
        foreach (LinuxNativeLibraryStatus status in statuses)
        {
            string state = status.IsAvailable ? "available" : "missing";
            Console.WriteLine("  " + status.Id + ": " + state + " - " + status.Diagnostic);
        }

        Console.WriteLine();
    }
}
