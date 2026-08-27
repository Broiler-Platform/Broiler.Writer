using System.Runtime.InteropServices.JavaScript;

namespace Broiler.Writer.WebAssembly;

/// <summary>
/// Managed-to-page bridge for the browser Writer. The page module registers these under the
/// <c>writer.*</c> namespace of <c>main.js</c>. The direct-Canvas backend owns actual pixel
/// presentation, so this surface only carries scheduling, cursor, caret, status, environment, and
/// the browser-native open/save glue (there is no ambient file system in the browser sandbox).
/// </summary>
internal static partial class BrowserInterop
{
    /// <summary>Requests a coalesced animation-frame render (managed → page).</summary>
    [JSImport("writer.scheduleFrame", "main.js")]
    internal static partial void ScheduleFrame();

    /// <summary>Sets the CSS cursor for the canvas (managed → page).</summary>
    [JSImport("writer.setCursor", "main.js")]
    internal static partial void SetCursor(string cursor);

    /// <summary>Signals that the Writer is initialized so the page can attach input and observers.</summary>
    [JSImport("writer.ready", "main.js")]
    internal static partial void Ready(double logicalWidth, double logicalHeight);

    /// <summary>Reports an unrecoverable managed failure to the page.</summary>
    [JSImport("writer.failed", "main.js")]
    internal static partial void Failed(string exceptionType, string details);

    /// <summary>
    /// Publishes per-frame page state: the managed text caret rectangle (for the hidden native
    /// editor and IME placement), the current status text, and the active color scheme.
    /// </summary>
    [JSImport("writer.publishFrame", "main.js")]
    internal static partial void PublishFrame(
        double frameIndex,
        bool caretActive,
        double caretX,
        double caretY,
        double caretWidth,
        double caretHeight,
        int caretIndex,
        int selectionStart,
        int selectionLength,
        bool focusedIsText,
        string statusText,
        bool darkTheme);

    /// <summary>Reads the page's <c>prefers-color-scheme</c> at startup (page → managed, sync).</summary>
    [JSImport("writer.prefersDarkScheme", "main.js")]
    internal static partial bool PrefersDarkScheme();

    /// <summary>Reads the page's <c>prefers-reduced-motion</c> at startup (page → managed, sync).</summary>
    [JSImport("writer.prefersReducedMotion", "main.js")]
    internal static partial bool PrefersReducedMotion();

    /// <summary>
    /// Opens the browser's native file picker (managed → page). The page reads the chosen file and
    /// calls back into <c>BrowserWriterExports.LoadDocument</c> with the name and base64 bytes.
    /// </summary>
    [JSImport("writer.requestOpenFile", "main.js")]
    internal static partial void RequestOpenFile(string acceptExtensions);

    /// <summary>
    /// Triggers a browser download of an encoded document (managed → page). The bytes are passed as
    /// base64 to keep the interop signature a simple string.
    /// </summary>
    [JSImport("writer.downloadFile", "main.js")]
    internal static partial void DownloadFile(string fileName, string base64Data);

    /// <summary>
    /// Prompts for a file name for Save As (page → managed, sync). Returns an empty string when the
    /// user cancels. The chosen extension selects the save format.
    /// </summary>
    [JSImport("writer.promptFileName", "main.js")]
    internal static partial string PromptFileName(string defaultName);
}
