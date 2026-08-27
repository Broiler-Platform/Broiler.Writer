# Broiler Writer — WebAssembly

A standalone .NET 10 Browser App that hosts the **desktop `Broiler.Writer` word processor** in the
browser: a Broiler.UI window with a Broiler-rendered menu, toolbar, and `StandardRichEdit` document
surface, running on the real managed `UiSession` and presented through the direct-Canvas 2D backend
(`Broiler.Graphics.WebAssembly.BrowserCanvasRenderer`).

Editing, formatting (bold/italic/underline/strikethrough, alignment, lists, indent, font dialog),
undo/redo, selection, the status bar, and clipboard behave exactly as in the Win32/Linux Writer.
Input is routed through the `Broiler.Input` contracts: browser Pointer Events, wheel, keyboard with
modifiers, committed/composition text from a hidden native editor, and trusted-event clipboard.

## Functional differences from the desktop Writer

The desktop app opens and saves through `StandardFileDialog` and the local file system. The browser
sandbox has no ambient file system, so:

- **Open** (`Ctrl+O`, File → Open, or the toolbar) uses the browser's native file picker. The chosen
  file's bytes are decoded by the same `Broiler.Documents` codec catalog (RTF / DOCX / HTML / Markdown).
- **Save** / **Save As** (`Ctrl+S`, File → Save) encode the document with the matching
  `Broiler.Documents` writer and **download** the result. Save As prompts for a file name whose
  extension selects the format.

- **Embedded images** in an opened document are read and written back by the codecs exactly as on
  the desktop, but they are **not drawn**: this host does not implement `IUiImageHost`, so the
  editor draws each picture as an outlined box of the right size. Nothing is lost — a document
  opened and saved here keeps its pictures — they are simply not rendered. Implementing it means
  registering an image codec catalog (`BImageCodecs.Use`) and bridging
  `BrowserCanvasRenderer.CreateImage`, which adds the managed decoders to the download payload.

Everything else — the window, menu, toolbar, RichEdit editing and formatting, the font dialog, the
status bar, and scrolling — is the same code as the desktop Writer.

## Architecture

- `Program` awaits `BrowserWriterApp.StartAsync`, which imports the direct-Canvas replay module,
  binds `BrowserCanvasRenderer` to `#writer-canvas`, and builds `BrowserWriterDemo`.
- `BrowserWriterDemo` is the browser counterpart of `Broiler.Writer.WriterApp`: it builds the same
  menu/toolbar/`StandardRichEdit` tree, hosts it on a `StandardUiSessionBuilder` session, dispatches
  input through the `Broiler.Input` event contracts, and swaps file-dialog IO for the browser-native
  open/save described above (both go through the identical `Broiler.Documents` codecs).
- `BrowserCanvasUiHost` implements `IUiHost` (+ text/clipboard/cursor/system-settings) and presents
  each `BRenderList` straight through the Canvas 2D backend — no CPU pixel copy across the boundary.
- `main.js` bridges input, animation-frame scheduling, `ResizeObserver`, cursor, caret-driven text,
  clipboard, and the file picker / download glue.

## Publish and serve

```powershell
dotnet workload install wasm-tools

dotnet publish Broiler.Writer.WebAssembly/Broiler.Writer.WebAssembly.csproj `
  -c Release -p:PublishTrimmed=true -p:RunAOTCompilation=false `
  -o artifacts/wasm-writer

python -m http.server 8767 --bind 127.0.0.1 `
  --directory artifacts/wasm-writer/wwwroot
```

Open `http://127.0.0.1:8767/`.

> Publishing untrimmed can fail to boot the mono runtime; publish with `-p:PublishTrimmed=true`.

## Publish modes

- interpreted/trimmed: `-p:PublishTrimmed=true -p:RunAOTCompilation=false`
- full AOT: `-p:PublishTrimmed=true -p:RunAOTCompilation=true`
