using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Broiler.Writer.WebAssembly;

/// <summary>
/// Managed-to-page bridge for the browser Writer. The page module registers these under the
/// <c>writer.*</c> namespace of <c>main.js</c>. The direct-Canvas backend owns actual pixel
/// presentation, so this surface only carries scheduling, cursor, caret, status, environment, and
/// the browser-native open/save glue (there is no ambient file system in the browser sandbox).
/// </summary>
internal static partial class BrowserInterop
{
    [JSImport("writer.openUrl", "main.js")]
    internal static partial void OpenUrl(string url);

    /// <summary>Requests a coalesced animation-frame render (managed → page).</summary>
    [JSImport("writer.scheduleFrame", "main.js")]
    internal static partial void ScheduleFrame();

    /// <summary>Sets the CSS cursor for the canvas (managed → page).</summary>
    /// <summary>
    /// Sets the page title. A browser tab is where this application's caption lives, so the
    /// document's name goes here rather than into a title bar it does not have.
    /// </summary>
    [JSImport("writer.setTitle", "main.js")]
    internal static partial void SetTitle(string title);

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

    [JSImport("writer.setHasUnsavedChanges", "main.js")]
    internal static partial void SetHasUnsavedChanges(bool modified);

    [JSImport("writer.setDocumentState", "main.js")]
    internal static partial void SetDocumentState(string name, bool modified, double generation, double revision);

    [JSImport("writer.reportOperation", "main.js")]
    internal static partial void ReportOperation(string outcome, string message, string notes);

    /// <summary>
    /// Triggers a browser download of an encoded document (managed → page). The bytes are passed as
    /// base64 to keep the interop signature a simple string.
    /// </summary>
    [JSImport("writer.downloadFile", "main.js")]
    internal static partial void DownloadFile(string fileName, string base64Data, int token);

    /// <summary>
    /// Opens the asynchronous export-name dialog. The page reports the result via ExportNameSelected.
    /// </summary>
    [JSImport("writer.requestExportName", "main.js")]
    internal static partial void RequestExportName(string defaultName);

    /// <summary>Writes plain text through the Async Clipboard API outside a trusted clipboard event.</summary>
    [JSImport("writer.clipboardWriteText", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    internal static partial Task<bool> ClipboardWriteText(string text);

    /// <summary>Reads plain text through the Async Clipboard API; resolves to null when refused.</summary>
    [JSImport("writer.clipboardReadText", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string?> ClipboardReadText();
}
