using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.FormatCodes;
using Broiler.Documents.Html;
using Broiler.Documents.Markdown;
using Broiler.Documents.Model;
using Broiler.Documents.Rtf;
using Broiler.Graphics;
using Broiler.Graphics.WebAssembly;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.Input.Text;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Edit.Standard;
using Broiler.UI.FontDialog.Standard;
using Broiler.UI.FormatCodeView.Standard;
using Broiler.UI.Label;
using Broiler.UI.Label.Standard;
using Broiler.UI.Menu;
using Broiler.UI.Menu.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.Splitter;
using Broiler.UI.Splitter.Standard;
using Broiler.UI.ToggleButton.Standard;
using Broiler.UI.Toolbar;
using Broiler.UI.Toolbar.Standard;
using Broiler.UI.Window.Standard;
using Broiler.Writer.FormatCodes;

namespace Broiler.Writer.WebAssembly;

/// <summary>
/// The browser counterpart of the desktop <c>Broiler.Writer.WriterApp</c>: the same Broiler.UI
/// window, menu, toolbar, and <see cref="StandardRichEdit"/> document surface, hosted on the real
/// <see cref="UiSession"/> and presented through the direct-Canvas 2D backend. Input is routed
/// through the <c>Broiler.Input</c> contracts. Because the browser sandbox has no ambient file
/// system, Open uses the browser file picker and Save/Save As download the encoded document; both
/// go through the identical <c>Broiler.Documents</c> RTF/DOCX/HTML/Markdown codecs.
/// </summary>
internal sealed class BrowserWriterDemo : IDisposable
{
    private static readonly InputDeviceId PointerDevice = InputDeviceId.FromOpaqueValue("browser-primary-pointer");
    private static readonly InputDeviceId KeyboardDevice = InputDeviceId.FromOpaqueValue("browser-keyboard");
    private static readonly InputDeviceId TextDevice = InputDeviceId.FromOpaqueValue("browser-text");

    private const string DefaultDocumentExtension = ".rtf";
    private const string OpenAcceptExtensions = ".rtf,.docx,.html,.htm,.md,.markdown";
    private static readonly BSize FontDialogPreferredSize = new(520, 322);

    private readonly BrowserCanvasUiHost _host;
    private readonly BrowserUiDispatcher _dispatcher;
    private readonly UiSession _session;
    private readonly StandardWindow _rootWindow;
    private readonly StandardRichEdit _editor;
    private readonly StandardFormatCodeView _formatCodesView;
    private readonly StandardSplitter _formatCodesSplitter;
    private readonly WriterFormatCodesController _formatCodesController;
    private readonly WriterContent _content;
    private readonly StandardMenu _menu;
    private readonly StandardToolbar _toolbar;
    private readonly StandardLabel _title;
    private readonly StandardLabel _status;
    private readonly DocumentCodecCatalog _documentCatalog = new(new DocumentCodec[]
    {
        new RtfDocumentCodec(),
        new DocxDocumentCodec(),
        new HtmlDocumentCodec(),
        new MarkdownDocumentCodec(),
    });
    private readonly List<(UiMenuItem Item, RichEditCommand Command)> _richEditMenuItems = [];
    private readonly List<(StandardButton Button, RichEditCommand Command)> _toolbarActionButtons = [];
    private readonly List<(StandardToggleButton Button, RichEditCommand Command)> _toolbarToggleButtons = [];
    private UiMenuItem? _fontMenuItem;
    private UiMenuItem? _formatCodesMenuItem;
    private StandardButton? _fontToolbarButton;
    private string _currentDocumentName = "Untitled document";
    private bool _hasSavedName;
    private string _lastAction = "Ready";
    private long _sequence;
    private MouseButtons _buttons;
    private bool _disposed;

    internal BrowserWriterDemo(
        BrowserCanvasRenderer renderer,
        bool reducedMotion,
        bool darkScheme,
        double width,
        double height,
        double dpr)
    {
        // The Writer chrome is a fixed light palette (matching the desktop app), so build the Standard
        // controls against the light theme regardless of the page's preference.
        _ = darkScheme;
        StandardControlPaint.ApplyTheme(StandardThemeTokens.Light);

        _host = new BrowserCanvasUiHost(renderer, reducedMotion, darkScheme: false)
        {
            ClearColor = WriterPalette.Canvas,
        };
        _dispatcher = new BrowserUiDispatcher(BrowserInterop.ScheduleFrame);
        _session = new StandardUiSessionBuilder()
            .WithDispatcher(_dispatcher)
            .WithClock(new BrowserUiClock())
            .Build(_host);
        _host.Resize(width, height, dpr);

        _editor = new StandardRichEdit
        {
            PreferredSize = new BSize(760, 520),
            PlaceholderText = "Start writing in Broiler Writer...",
            Font = new BFontStyle("Segoe UI", 17),
            Background = WriterPalette.Page,
            BorderColor = WriterPalette.EditorBorder,
            FocusRing = WriterPalette.Accent,
            PaddingX = 18,
            PaddingY = 16,
        };
        _formatCodesView = new StandardFormatCodeView
        {
            PreferredSize = new BSize(760, 160),
            // The view lays out on a fixed-width grid, so it needs a genuinely monospace face. Use the
            // CSS generic "monospace" (the browser's default fixed-width font) rather than a named face
            // like "Cascadia Mono" that may be absent and fall back to a proportional font — which would
            // spread glyphs off the grid and open growing gaps between the codes.
            Font = new BFontStyle("monospace", 14),
            Background = WriterPalette.FormatCodesSurface,
            Foreground = WriterPalette.Title,
            InlineCodeForeground = WriterPalette.FormatCodesInline,
            ParagraphCodeForeground = WriterPalette.FormatCodesParagraph,
            StructureCodeForeground = WriterPalette.FormatCodesStructure,
            EscapeForeground = WriterPalette.FormatCodesEscape,
            PendingForeground = WriterPalette.FormatCodesPending,
            BorderColor = WriterPalette.EditorBorder,
            FocusRing = WriterPalette.Accent,
        };
        _formatCodesSplitter = new StandardSplitter
        {
            Orientation = UiSplitterOrientation.Horizontal,
            Minimum = 0.35,
            Maximum = 0.82,
            Value = 0.68,
            PreferredSize = new BSize(760, WriterFormatCodesLayout.SplitterThickness),
            Background = WriterPalette.FormatCodesSplitter,
            GripColor = WriterPalette.Muted,
            FocusRing = WriterPalette.Accent,
        };

        _menu = CreateMenu();
        _toolbar = CreateToolbar();
        _title = new StandardLabel
        {
            Text = "Untitled document",
            Font = new BFontStyle("Segoe UI", 20, BFontWeight.SemiBold),
            Foreground = WriterPalette.Title,
        };
        _status = new StandardLabel
        {
            Text = "Ready",
            Font = new BFontStyle("Segoe UI", 13),
            Foreground = WriterPalette.Muted,
            Trimming = UiTextTrimming.CharacterEllipsis,
        };

        _rootWindow = new StandardWindow
        {
            Title = "Broiler Writer",
            Background = WriterPalette.Canvas,
            BorderColor = WriterPalette.WindowBorder,
            ActiveBorderColor = WriterPalette.Accent,
            BorderThickness = 1,
        };
        _content = new WriterContent(
            _menu, _toolbar, _title, _editor, _formatCodesSplitter, _formatCodesView, _status);
        _rootWindow.AddChild(_content);

        SeedDocument();
        _formatCodesController = new WriterFormatCodesController(
            _editor, _formatCodesView, _session.Dispatcher);
        _session.AddRoot(_rootWindow);
        _session.SetFocus(_editor);

        _editor.SelectionChanged += (_, _) => RefreshUi();
        _editor.DocumentChanged += (_, _) => RefreshUi();
        _editor.CommandExecuted += (_, e) =>
        {
            if (e.Command != RichEditCommand.InsertText)
                _lastAction = FriendlyCommandName(e.Command);
            RefreshUi();
        };
        _menu.ItemInvoked += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Item.CommandName))
                RefreshUi();
        };
        _formatCodesController.StatusChanged += (_, _) => RefreshUi();
        _formatCodesView.ExitRequested += (_, _) => _session.SetFocus(_editor);
        _formatCodesView.SearchRequested += (_, _) =>
        {
            _lastAction = "Formatting Codes search (Ctrl+F)";
            RefreshUi();
        };
        _formatCodesSplitter.ValueChanged += (_, _) =>
        {
            _content.Invalidate(UiInvalidationKind.Arrange | UiInvalidationKind.Render);
            _lastAction = "Formatting Codes pane resized";
            RefreshUi();
        };

        RefreshUi();
    }

    internal double ViewportWidth => _host.ViewportSize.Width;

    internal double ViewportHeight => _host.ViewportSize.Height;

    internal void Resize(double width, double height, double dpr) =>
        _dispatcher.Post(() =>
        {
            if (_host.Resize(width, height, dpr))
                _rootWindow.Invalidate(UiInvalidationKind.Measure | UiInvalidationKind.Arrange | UiInvalidationKind.Render);
        });

    internal void RenderScheduledFrame()
    {
        if (_disposed)
            return;

        _dispatcher.Drain();
        _host.BeginFrame();
        _session.RenderFrame();
        PublishFrameState();
    }

    internal void PointerMove(double x, double y, int buttons, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            _buttons = (MouseButtons)buttons;
            _session.DispatchInput(UiInputEvent.FromMouseMove(new MouseMoveEvent(
                Header(PointerDevice, timestampMilliseconds),
                InputPoint.ClientDeviceIndependentPixels(x, y),
                _buttons)));
            UpdateCursor(x, y);
        });

    internal void PointerButton(double x, double y, int buttons, int domButton, bool down, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            _buttons = (MouseButtons)buttons;
            _session.DispatchInput(UiInputEvent.FromMouseButton(new MouseButtonEvent(
                Header(PointerDevice, timestampMilliseconds),
                InputPoint.ClientDeviceIndependentPixels(x, y),
                _buttons,
                MapButton(domButton),
                down ? MouseButtonTransition.Down : MouseButtonTransition.Up)));
            UpdateCursor(x, y);
        });

    internal void PointerWheel(double x, double y, int buttons, bool horizontal, double deltaNotches, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            _buttons = (MouseButtons)buttons;
            _session.DispatchInput(UiInputEvent.FromMouseWheel(new MouseWheelEvent(
                Header(PointerDevice, timestampMilliseconds),
                InputPoint.ClientDeviceIndependentPixels(x, y),
                _buttons,
                horizontal ? MouseWheelAxis.Horizontal : MouseWheelAxis.Vertical,
                deltaNotches)));
        });

    internal void KeyboardKey(string keyName, bool down, int modifiers, int nativeKeyCode, bool repeat, int location, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            var modifierState = (KeyboardModifierState)modifiers;

            if (WriterFormatCodesShortcut.IsToggle(keyName, modifierState, down, repeat))
            {
                ToggleFormattingCodes();
                return;
            }
            if (WriterFormatCodesShortcut.IsFocusCycle(keyName, modifierState, down, repeat))
            {
                CycleFormattingCodesFocus(WriterFormatCodesShortcut.IsReverseFocusCycle(modifierState));
                return;
            }

            // App-level accelerators the RichEdit itself does not own: Ctrl+S saves, Ctrl+O opens.
            if (down && !repeat && (modifierState & KeyboardModifierState.Control) != 0)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(keyName, "S")) { SaveDocument(); return; }
                if (StringComparer.OrdinalIgnoreCase.Equals(keyName, "O")) { RequestOpenDocument(); return; }
            }

            var input = new KeyboardKeyEvent(
                Header(KeyboardDevice, timestampMilliseconds),
                Broiler.Input.Keyboard.KeyboardKey.FromName(keyName),
                down ? KeyboardKeyTransition.Down : KeyboardKeyTransition.Up,
                modifierState,
                nativeKeyCode,
                ScanCode: 0,
                RepeatCount: repeat ? 2 : 1,
                IsExtended: location != 0,
                WasDown: repeat,
                Location: Enum.IsDefined((KeyboardKeyLocation)location) ? (KeyboardKeyLocation)location : KeyboardKeyLocation.Standard);
            _session.DispatchInput(UiInputEvent.FromKeyboardKey(input));
        });

    internal void TextInput(string text, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            if (string.IsNullOrEmpty(text))
                return;

            _session.DispatchInput(UiInputEvent.FromTextInput(new TextInputEvent(
                Header(TextDevice, timestampMilliseconds), text)));
        });

    internal void TextComposition(string text, int state, int selectionStart, int selectionLength, double timestampMilliseconds) =>
        _dispatcher.Post(() =>
        {
            TextCompositionState compositionState = Enum.IsDefined((TextCompositionState)state)
                ? (TextCompositionState)state
                : TextCompositionState.Updated;
            _session.DispatchInput(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                Header(TextDevice, timestampMilliseconds),
                text ?? string.Empty,
                compositionState,
                Math.Max(0, selectionStart),
                Math.Max(0, selectionLength))));
        });

    internal string ClipboardEvent(string operation, string text)
    {
        if (_disposed)
            return string.Empty;

        _host.BeginClipboardEvent(StringComparer.Ordinal.Equals(operation, "paste") ? text ?? string.Empty : null);
        RunClipboardOperation(operation);
        string output = _host.EndClipboardEvent();
        BrowserInterop.ScheduleFrame();
        return output;
    }

    internal void LoadDocument(string fileName, string base64Data) =>
        _dispatcher.Post(() =>
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64Data ?? string.Empty);
                string name = string.IsNullOrWhiteSpace(fileName) ? "Untitled" + DefaultDocumentExtension : fileName;
                DocumentReadResult result = ReadDocument(name, bytes);
                _currentDocumentName = name;
                _hasSavedName = true;
                _title.Text = name;
                _lastAction = result.Diagnostics.Count == 0
                    ? "Opened " + name
                    : "Opened " + name + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
                _editor.Document = result.Document;
                _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
                _session.SetFocus(_editor);
            }
            catch (Exception ex) when (IsDocumentException(ex))
            {
                _lastAction = "Open failed: " + ex.Message;
            }

            RefreshUi();
        });

    internal void CancelPointer(double timestampMilliseconds) =>
        _dispatcher.Post(() => CleanupPointer(timestampMilliseconds));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CleanupPointer(0);
        _formatCodesController.Dispose();
        _session.Dispose();
        _host.Dispose();
    }

    // ---- Menu -------------------------------------------------------------------------------

    private StandardMenu CreateMenu()
    {
        var dispatcher = new StandardCommandDispatcher();
        dispatcher.Add(new StandardCommand("file.new", NewDocument));
        dispatcher.Add(new StandardCommand("file.open", RequestOpenDocument));
        dispatcher.Add(new StandardCommand("file.save", SaveDocument));
        dispatcher.Add(new StandardCommand("file.save-as", SaveDocumentAs));
        dispatcher.Add(new StandardCommand("view.formatting-codes", ToggleFormattingCodes));
        AddFormatCodeCommands(dispatcher);
        dispatcher.Add(new StandardCommand("format.font", ShowFontDialog, () => _editor.GetCommandState(RichEditCommand.SetFont).IsEnabled));
        AddRichEditCommand(dispatcher, "edit.undo", RichEditCommand.Undo);
        AddRichEditCommand(dispatcher, "edit.redo", RichEditCommand.Redo);
        AddRichEditCommand(dispatcher, "edit.cut", RichEditCommand.Cut);
        AddRichEditCommand(dispatcher, "edit.copy", RichEditCommand.Copy);
        AddRichEditCommand(dispatcher, "edit.paste", RichEditCommand.Paste);
        AddRichEditCommand(dispatcher, "edit.select-all", RichEditCommand.SelectAll);
        AddRichEditCommand(dispatcher, "format.bold", RichEditCommand.Bold);
        AddRichEditCommand(dispatcher, "format.italic", RichEditCommand.Italic);
        AddRichEditCommand(dispatcher, "format.underline", RichEditCommand.Underline);
        AddRichEditCommand(dispatcher, "format.strike", RichEditCommand.Strikethrough);
        AddRichEditCommand(dispatcher, "format.clear", RichEditCommand.ClearFormatting);
        AddRichEditCommand(dispatcher, "paragraph.left", RichEditCommand.AlignLeft);
        AddRichEditCommand(dispatcher, "paragraph.center", RichEditCommand.AlignCenter);
        AddRichEditCommand(dispatcher, "paragraph.right", RichEditCommand.AlignRight);
        AddRichEditCommand(dispatcher, "paragraph.bullets", RichEditCommand.BulletList);
        AddRichEditCommand(dispatcher, "paragraph.numbered", RichEditCommand.NumberedList);
        AddRichEditCommand(dispatcher, "paragraph.indent", RichEditCommand.Indent);
        AddRichEditCommand(dispatcher, "paragraph.outdent", RichEditCommand.Outdent);

        var file = new UiMenuItem("file", "File") { AccessKey = 'F' };
        file.Children.Add(new UiMenuItem("new", "New document") { CommandName = "file.new", AccessKey = 'N' });
        file.Children.Add(new UiMenuItem("open", "Open...") { CommandName = "file.open", AccessKey = 'O' });
        file.Children.Add(new UiMenuItem("save", "Save") { CommandName = "file.save", AccessKey = 'S' });
        file.Children.Add(new UiMenuItem("save-as", "Save as...") { CommandName = "file.save-as", AccessKey = 'A' });

        var edit = new UiMenuItem("edit", "Edit") { AccessKey = 'E' };
        edit.Children.Add(RichEditItem("undo", "Undo", "edit.undo", RichEditCommand.Undo, 'U'));
        edit.Children.Add(RichEditItem("redo", "Redo", "edit.redo", RichEditCommand.Redo, 'R'));
        edit.Children.Add(RichEditItem("cut", "Cut", "edit.cut", RichEditCommand.Cut, 'T'));
        edit.Children.Add(RichEditItem("copy", "Copy", "edit.copy", RichEditCommand.Copy, 'C'));
        edit.Children.Add(RichEditItem("paste", "Paste", "edit.paste", RichEditCommand.Paste, 'P'));
        edit.Children.Add(RichEditItem("select-all", "Select all", "edit.select-all", RichEditCommand.SelectAll, 'A'));

        var format = new UiMenuItem("format", "Format") { AccessKey = 'O' };
        _fontMenuItem = new UiMenuItem("font", "Font...") { CommandName = "format.font", AccessKey = 'F' };
        format.Children.Add(_fontMenuItem);
        format.Children.Add(RichEditItem("bold", "Bold", "format.bold", RichEditCommand.Bold, 'B', checkable: true));
        format.Children.Add(RichEditItem("italic", "Italic", "format.italic", RichEditCommand.Italic, 'I', checkable: true));
        format.Children.Add(RichEditItem("underline", "Underline", "format.underline", RichEditCommand.Underline, 'U', checkable: true));
        format.Children.Add(RichEditItem("strike", "Strikethrough", "format.strike", RichEditCommand.Strikethrough, 'S', checkable: true));
        format.Children.Add(RichEditItem("clear", "Clear formatting", "format.clear", RichEditCommand.ClearFormatting, 'C'));

        var insertCode = new UiMenuItem("insert-code", "Insert Code") { AccessKey = 'D' };
        insertCode.Children.Add(new UiMenuItem("code-bold", "Bold") { CommandName = "formatcodes.insert.bold" });
        insertCode.Children.Add(new UiMenuItem("code-italic", "Italic") { CommandName = "formatcodes.insert.italic" });
        insertCode.Children.Add(new UiMenuItem("code-underline", "Underline") { CommandName = "formatcodes.insert.underline" });
        insertCode.Children.Add(new UiMenuItem("code-strike", "Strikethrough") { CommandName = "formatcodes.insert.strike" });
        insertCode.Children.Add(new UiMenuItem("code-tab", "Tab") { CommandName = "formatcodes.insert.tab" });
        insertCode.Children.Add(new UiMenuItem("code-line-break", "Soft line break") { CommandName = "formatcodes.insert.line-break" });
        insertCode.Children.Add(new UiMenuItem("code-paragraph-break", "Paragraph break") { CommandName = "formatcodes.insert.paragraph-break" });
        format.Children.Add(insertCode);
        format.Children.Add(new UiMenuItem("remove-code", "Remove selected code")
        {
            CommandName = "formatcodes.remove-code",
        });

        var paragraph = new UiMenuItem("paragraph", "Paragraph") { AccessKey = 'P' };
        paragraph.Children.Add(RichEditItem("left", "Align left", "paragraph.left", RichEditCommand.AlignLeft, 'L', checkable: true));
        paragraph.Children.Add(RichEditItem("center", "Align center", "paragraph.center", RichEditCommand.AlignCenter, 'C', checkable: true));
        paragraph.Children.Add(RichEditItem("right", "Align right", "paragraph.right", RichEditCommand.AlignRight, 'R', checkable: true));
        paragraph.Children.Add(RichEditItem("bullets", "Bullets", "paragraph.bullets", RichEditCommand.BulletList, 'B', checkable: true));
        paragraph.Children.Add(RichEditItem("numbered", "Numbered", "paragraph.numbered", RichEditCommand.NumberedList, 'N', checkable: true));
        paragraph.Children.Add(RichEditItem("indent", "Indent", "paragraph.indent", RichEditCommand.Indent, 'I'));
        paragraph.Children.Add(RichEditItem("outdent", "Outdent", "paragraph.outdent", RichEditCommand.Outdent, 'O'));
        format.Children.Add(paragraph);

        var view = new UiMenuItem("view", "View") { AccessKey = 'V' };
        _formatCodesMenuItem = new UiMenuItem("formatting-codes", "Formatting Codes")
        {
            CommandName = "view.formatting-codes",
            AccessKey = 'F',
            IsCheckable = true,
            IsChecked = true,
        };
        view.Children.Add(_formatCodesMenuItem);

        var help = new UiMenuItem("help", "Help") { AccessKey = 'H' };
        help.Children.Add(new UiMenuItem("about", "About Broiler Writer") { CommandName = "help.about", AccessKey = 'A' });
        dispatcher.Add(new StandardCommand("help.about", ShowAbout));

        var menu = new StandardMenu
        {
            PresentationMode = UiMenuPresentationMode.MenuBar,
            PreferredSize = new BSize(360, 30),
            MenuBarHeight = 30,
            ItemHeight = 28,
            PopupWidth = 210,
            Font = new BFontStyle("Segoe UI", 14),
            Background = WriterPalette.MenuSurface,
            PopupBackground = WriterPalette.MenuPopup,
            Foreground = WriterPalette.Title,
            BorderColor = WriterPalette.EditorBorder,
            SelectedBackground = WriterPalette.MenuSelected,
            CommandDispatcher = dispatcher,
        };
        menu.SetItems([file, edit, format, view, help]);
        return menu;
    }

    private void AddFormatCodeCommands(StandardCommandDispatcher dispatcher)
    {
        dispatcher.Add(new StandardCommand("formatcodes.insert.bold", () => RunFormatCodePalette(FormatCodePaletteEntry.Bold)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.italic", () => RunFormatCodePalette(FormatCodePaletteEntry.Italic)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.underline", () => RunFormatCodePalette(FormatCodePaletteEntry.Underline)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.strike", () => RunFormatCodePalette(FormatCodePaletteEntry.Strikethrough)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.tab", () => RunFormatCodePalette(FormatCodePaletteEntry.Tab)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.line-break", () => RunFormatCodePalette(FormatCodePaletteEntry.LineBreak)));
        dispatcher.Add(new StandardCommand("formatcodes.insert.paragraph-break", () => RunFormatCodePalette(FormatCodePaletteEntry.ParagraphBreak)));
        dispatcher.Add(new StandardCommand("formatcodes.remove-code", RemoveCurrentFormatCode));
    }

    private void RunFormatCodePalette(FormatCodePaletteEntry entry)
    {
        _formatCodesController.ExecutePaletteEntry(entry);
        _lastAction = _formatCodesController.Status;
        RefreshUi();
    }

    private void RemoveCurrentFormatCode()
    {
        if (_formatCodesView.CurrentToken is FormatCodeToken token)
            _formatCodesController.RemoveTokenFormatting(token);
        _lastAction = _formatCodesController.Status;
        RefreshUi();
    }

    // ---- Toolbar ----------------------------------------------------------------------------

    private StandardToolbar CreateToolbar()
    {
        var toolbar = new StandardToolbar
        {
            Title = "Document toolbar",
            PreferredSize = new BSize(0, 42),
            Orientation = UiToolbarOrientation.Horizontal,
            Padding = 5,
            Spacing = 4,
            Background = WriterPalette.ToolbarSurface,
            BorderColor = WriterPalette.MenuRule,
            SeparatorColor = WriterPalette.MenuRule,
            CornerRadius = 0,
        };

        StandardButton newButton = ToolbarAction("New", 50, NewDocument);
        StandardButton openButton = ToolbarAction("Open", 56, RequestOpenDocument);
        StandardButton saveButton = ToolbarAction("Save", 54, SaveDocument);
        StandardButton saveAsButton = ToolbarAction("Save As", 62, SaveDocumentAs);
        StandardButton undoButton = ToolbarCommand("Undo", RichEditCommand.Undo, 52);
        StandardButton redoButton = ToolbarCommand("Redo", RichEditCommand.Redo, 52);
        StandardButton fontButton = ToolbarAction("Font...", 62, ShowFontDialog);
        _fontToolbarButton = fontButton;
        StandardToggleButton boldButton = ToolbarToggle("B", RichEditCommand.Bold, 34, BFontWeight.Bold);
        StandardToggleButton italicButton = ToolbarToggle("I", RichEditCommand.Italic, 34, BFontWeight.Normal, BFontSlant.Italic);
        StandardToggleButton underlineButton = ToolbarToggle("U", RichEditCommand.Underline, 34, BFontWeight.Normal);
        StandardToggleButton strikeButton = ToolbarToggle("S", RichEditCommand.Strikethrough, 34, BFontWeight.Normal);
        StandardButton clearButton = ToolbarCommand("Clear", RichEditCommand.ClearFormatting, 54);
        StandardToggleButton leftButton = ToolbarToggle("Left", RichEditCommand.AlignLeft, 48, BFontWeight.Normal);
        StandardToggleButton centerButton = ToolbarToggle("Center", RichEditCommand.AlignCenter, 54, BFontWeight.Normal);
        StandardToggleButton rightButton = ToolbarToggle("Right", RichEditCommand.AlignRight, 48, BFontWeight.Normal);
        StandardToggleButton bulletsButton = ToolbarToggle("Bullets", RichEditCommand.BulletList, 58, BFontWeight.Normal);
        StandardToggleButton numberedButton = ToolbarToggle("Numbered", RichEditCommand.NumberedList, 70, BFontWeight.Normal);
        StandardButton indentButton = ToolbarCommand("Indent", RichEditCommand.Indent, 58);
        StandardButton outdentButton = ToolbarCommand("Outdent", RichEditCommand.Outdent, 64);

        toolbar.AddChild(newButton);
        toolbar.AddChild(openButton);
        toolbar.AddChild(saveButton);
        toolbar.AddChild(saveAsButton);
        toolbar.AddChild(undoButton);
        toolbar.AddChild(redoButton);
        toolbar.AddChild(fontButton);
        toolbar.AddChild(boldButton);
        toolbar.AddChild(italicButton);
        toolbar.AddChild(underlineButton);
        toolbar.AddChild(strikeButton);
        toolbar.AddChild(clearButton);
        toolbar.AddChild(leftButton);
        toolbar.AddChild(centerButton);
        toolbar.AddChild(rightButton);
        toolbar.AddChild(bulletsButton);
        toolbar.AddChild(numberedButton);
        toolbar.AddChild(indentButton);
        toolbar.AddChild(outdentButton);

        toolbar.SetSeparatorBefore(undoButton, true);
        toolbar.SetSeparatorBefore(fontButton, true);
        toolbar.SetSeparatorBefore(leftButton, true);
        toolbar.SetSeparatorBefore(indentButton, true);

        return toolbar;
    }

    private StandardButton ToolbarAction(string text, double width, Action action)
    {
        StandardButton button = CreateToolbarButton(text, width);
        button.Clicked += (_, _) =>
        {
            action();
            RefreshUi();
        };
        return button;
    }

    private StandardButton ToolbarCommand(string text, RichEditCommand command, double width)
    {
        StandardButton button = CreateToolbarButton(text, width);
        button.Clicked += (_, _) => RunRichEditCommand(command);
        _toolbarActionButtons.Add((button, command));
        return button;
    }

    private StandardToggleButton ToolbarToggle(
        string text,
        RichEditCommand command,
        double width,
        BFontWeight weight,
        BFontSlant slant = BFontSlant.Normal)
    {
        var button = new StandardToggleButton
        {
            Text = text,
            PreferredSize = new BSize(width, 30),
            Font = new BFontStyle("Segoe UI", 13, weight, slant),
            PaddingX = 8,
            PaddingY = 5,
            Background = WriterPalette.ToolbarButton,
            CheckedBackground = WriterPalette.ToolbarButtonActive,
            IndeterminateBackground = WriterPalette.ToolbarButtonActive,
            Foreground = WriterPalette.Title,
            BorderColor = WriterPalette.ToolbarButtonBorder,
            DisabledForeground = WriterPalette.Muted,
            HoverBackground = WriterPalette.ToolbarButtonHover,
            PressedBackground = WriterPalette.ToolbarButtonPressed,
            FocusRing = WriterPalette.Accent,
            CornerRadius = 5,
        };
        button.Clicked += (_, _) => RunRichEditCommand(command);
        _toolbarToggleButtons.Add((button, command));
        return button;
    }

    private static StandardButton CreateToolbarButton(string text, double width) =>
        new()
        {
            Text = text,
            PreferredSize = new BSize(width, 30),
            Font = new BFontStyle("Segoe UI", 13),
            PaddingX = 8,
            PaddingY = 5,
            Background = WriterPalette.ToolbarButton,
            Foreground = WriterPalette.Title,
            BorderColor = WriterPalette.ToolbarButtonBorder,
            DisabledForeground = WriterPalette.Muted,
            SecondaryHoverBackground = WriterPalette.ToolbarButtonHover,
            SecondaryPressedBackground = WriterPalette.ToolbarButtonPressed,
            FocusRing = WriterPalette.Accent,
            CornerRadius = 5,
        };

    private void AddRichEditCommand(StandardCommandDispatcher dispatcher, string name, RichEditCommand command) =>
        dispatcher.Add(new StandardCommand(name, () => RunRichEditCommand(command), () => _editor.GetCommandState(command).IsEnabled));

    private UiMenuItem RichEditItem(string id, string text, string commandName, RichEditCommand command, char accessKey, bool checkable = false)
    {
        var item = new UiMenuItem(id, text)
        {
            CommandName = commandName,
            AccessKey = accessKey,
            IsCheckable = checkable,
        };
        _richEditMenuItems.Add((item, command));
        return item;
    }

    private void RunRichEditCommand(RichEditCommand command)
    {
        bool ran = _editor.ExecuteCommand(command);
        _lastAction = ran ? FriendlyCommandName(command) : FriendlyCommandName(command) + " unavailable";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    // ---- Document operations (browser-native) -----------------------------------------------

    private void NewDocument()
    {
        _currentDocumentName = "Untitled document";
        _hasSavedName = false;
        _editor.SetPlainText(string.Empty);
        _title.Text = "Untitled document";
        _lastAction = "New document";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    private void RequestOpenDocument()
    {
        BrowserInterop.RequestOpenFile(OpenAcceptExtensions);
        _lastAction = "Open document";
        RefreshUi();
    }

    private void SaveDocument()
    {
        if (!_hasSavedName)
        {
            SaveDocumentAs();
            return;
        }

        DownloadDocument(_currentDocumentName);
    }

    private void SaveDocumentAs()
    {
        string suggested = _hasSavedName ? _currentDocumentName : "Untitled" + DefaultDocumentExtension;
        string chosen = BrowserInterop.PromptFileName(suggested);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            _lastAction = "Save cancelled";
            RefreshUi();
            return;
        }

        DownloadDocument(chosen);
    }

    private void DownloadDocument(string name)
    {
        try
        {
            string resolved = EnsureExtension(name);
            DocumentWriteResult result = WriteDocument(resolved, _editor.Document, out byte[] bytes);
            BrowserInterop.DownloadFile(resolved, Convert.ToBase64String(bytes));
            _currentDocumentName = resolved;
            _hasSavedName = true;
            _title.Text = resolved;
            _lastAction = result.Diagnostics.Count == 0
                ? "Saved " + resolved
                : "Saved " + resolved + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
        }
        catch (Exception ex) when (IsDocumentException(ex))
        {
            _lastAction = "Save failed: " + ex.Message;
        }

        _session.SetFocus(_editor);
        RefreshUi();
    }

    private DocumentReadResult ReadDocument(string name, byte[] bytes)
    {
        using var probeStream = new MemoryStream(bytes, writable: false);
        DocumentCodecMatch? match = _documentCatalog.Select(
            probeStream,
            new DocumentSourceHints(fileName: name));

        if (match is null || !match.Codec.CanRead)
            throw new NotSupportedException("No readable document codec recognized '" + Path.GetExtension(name) + "'.");

        using var readStream = new MemoryStream(bytes, writable: false);
        return match.Codec.Read(readStream);
    }

    private static DocumentWriteResult WriteDocument(string name, RichTextDocument document, out byte[] bytes)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        using var stream = new MemoryStream();
        DocumentWriteResult result = extension switch
        {
            ".rtf" => RtfWriter.Write(document, stream),
            ".docx" => DocxWriter.Write(document, stream),
            ".html" or ".htm" => HtmlWriter.Write(document, stream),
            ".md" or ".markdown" => MarkdownWriter.Write(document, stream),
            _ => throw new NotSupportedException("Unsupported save format '" + extension + "'. Use .rtf, .docx, .html, or .md."),
        };

        bytes = stream.ToArray();
        return result;
    }

    private static string EnsureExtension(string name) =>
        Path.HasExtension(name) ? name : name + DefaultDocumentExtension;

    private static bool IsDocumentException(Exception ex) =>
        ex is IOException or NotSupportedException or ArgumentException or FormatException;

    // ---- Font dialog ------------------------------------------------------------------------

    private void ShowFontDialog()
    {
        if (!_editor.GetCommandState(RichEditCommand.SetFont).IsEnabled)
        {
            _lastAction = "Font unavailable";
            RefreshUi();
            return;
        }

        var dialog = new StandardFontDialog
        {
            PreferredSize = FontDialogPreferredSize,
            SelectedFont = CurrentEditorFont(),
            SampleText = "Broiler Writer font preview",
            TitleFont = new BFontStyle("Segoe UI", 14, BFontWeight.SemiBold),
            LabelFont = new BFontStyle("Segoe UI", 13),
        };
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted)
                ApplySelectedFont(dialog.SelectedFont);
        };

        dialog.ShowFontModal(_rootWindow, GetFontDialogPlacement());
        _lastAction = "Font dialog";
        RefreshUi();
    }

    private void ApplySelectedFont(BFontStyle font)
    {
        bool ran = _editor.ExecuteCommand(RichEditCommand.SetFont, font);
        _lastAction = ran
            ? "Font: " + font.FamilyName + " " + font.SizeInPixels.ToString("0.###", CultureInfo.InvariantCulture)
            : "Font unavailable";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    private BFontStyle CurrentEditorFont()
    {
        InlineStyle style = _editor.CaretInlineStyle;
        return _editor.Font with
        {
            FamilyName = string.IsNullOrWhiteSpace(style.FontFamily) ? _editor.Font.FamilyName : style.FontFamily,
            SizeInPixels = style.FontSize is > 0 ? style.FontSize.Value : _editor.Font.SizeInPixels,
            Weight = style.Bold ? BFontWeight.Bold : _editor.Font.Weight,
            Slant = style.Italic ? BFontSlant.Italic : _editor.Font.Slant,
        };
    }

    private BRect GetFontDialogPlacement()
    {
        BSize viewport = _host.ViewportSize;
        double width = FontDialogPreferredSize.Width;
        double height = FontDialogPreferredSize.Height;
        double x = Math.Max(12, (viewport.Width - width) / 2);
        double y = Math.Max(72, (viewport.Height - height) / 2);
        return new BRect(x, y, Math.Min(width, Math.Max(320, viewport.Width - 24)), Math.Min(height, Math.Max(220, viewport.Height - 84)));
    }

    private void ShowAbout()
    {
        _lastAction = "Broiler Writer in the browser: Broiler.UI window, menu, StandardRichEdit, and the Broiler.Documents codecs.";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    // ---- Input plumbing ---------------------------------------------------------------------

    private void RunClipboardOperation(string operation)
    {
        switch (_session.FocusedElement)
        {
            case StandardFormatCodeView formatCodes:
                if (StringComparer.OrdinalIgnoreCase.Equals(operation, "copy"))
                    formatCodes.CopySelection();
                break;
            case StandardRichEdit richEdit:
                _ = operation switch
                {
                    "copy" => richEdit.ExecuteCommand(RichEditCommand.Copy),
                    "cut" => richEdit.ExecuteCommand(RichEditCommand.Cut),
                    "paste" => richEdit.ExecuteCommand(RichEditCommand.Paste),
                    _ => false,
                };
                break;
            case StandardEdit edit:
                _ = operation switch
                {
                    "copy" => edit.Copy(),
                    "cut" => edit.Cut(),
                    "paste" => edit.Paste(),
                    _ => false,
                };
                break;
        }
    }

    private InputEventHeader Header(InputDeviceId device, double timestampMilliseconds) =>
        new(device, new InputTimestamp((long)Math.Max(0, timestampMilliseconds * 1000), 1_000_000, "browser-performance"), ++_sequence);

    private void CleanupPointer(double timestampMilliseconds)
    {
        var outside = InputPoint.ClientDeviceIndependentPixels(-1, -1);
        _session.DispatchInput(UiInputEvent.FromMouseMove(new MouseMoveEvent(
            Header(PointerDevice, timestampMilliseconds), outside, _buttons, InputEventSource.Synthetic)));

        foreach ((MouseButtons flag, MouseButton button) in ButtonMap)
        {
            if ((_buttons & flag) == 0)
                continue;
            _session.DispatchInput(UiInputEvent.FromMouseButton(new MouseButtonEvent(
                Header(PointerDevice, timestampMilliseconds), outside, MouseButtons.None, button,
                MouseButtonTransition.Up, InputEventSource.Synthetic)));
        }

        _buttons = MouseButtons.None;
        if (_session.CapturedElement is UiElement captured)
            _session.ReleaseInputCapture(captured);
        _host.SetCursor(UiCursorShape.Arrow);
    }

    private void UpdateCursor(double x, double y)
    {
        UiElement? target = _session.HitTest(new BPoint(x, y));
        UiCursorShape cursor = target switch
        {
            StandardRichEdit or StandardEdit or StandardFormatCodeView => UiCursorShape.Text,
            StandardButton or StandardMenu or StandardToggleButton or StandardSplitter => UiCursorShape.Hand,
            _ => UiCursorShape.Arrow,
        };
        _host.SetCursor(cursor);
    }

    private void PublishFrameState()
    {
        UiTextCaretInfo? caret = _host.CurrentCaret;
        bool focusedIsText = _session.FocusedElement is StandardRichEdit or StandardEdit or StandardFormatCodeView;
        BrowserInterop.PublishFrame(
            _host.FrameIndex,
            caret is not null,
            caret?.Bounds.X ?? 0,
            caret?.Bounds.Y ?? 0,
            caret?.Bounds.Width ?? 0,
            caret?.Bounds.Height ?? 0,
            caret?.CaretIndex ?? 0,
            caret?.SelectionStart ?? 0,
            caret?.SelectionLength ?? 0,
            focusedIsText,
            _status.Text,
            darkTheme: false);
    }

    // ---- Status + seeding -------------------------------------------------------------------

    private void SeedDocument()
    {
        _editor.SetPlainText(
            "Broiler Writer\n" +
            "This browser build is a Broiler.UI window with a Broiler-rendered menu and StandardRichEdit document surface, presented through the direct-Canvas 2D backend.\n" +
            "Use the Edit and Format menus, or keyboard shortcuts such as Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+Z, and Ctrl+Y. Open and Save round-trip through the Broiler.Documents RTF, DOCX, HTML, and Markdown codecs.");

        RichTextPosition start = _editor.Document.Start;
        RichTextPosition end = _editor.Document.ParagraphEnd(start);
        _editor.Selection = new RichTextRange(start, end);
        _editor.ExecuteCommand(RichEditCommand.Bold);
        _editor.Selection = RichTextRange.Caret(_editor.Document.End);
        _lastAction = "Ready";
    }

    private void ToggleFormattingCodes()
    {
        _content.IsFormatCodesVisible = !_content.IsFormatCodesVisible;
        if (_formatCodesMenuItem is not null)
            _formatCodesMenuItem.IsChecked = _content.IsFormatCodesVisible;

        if (!_content.IsFormatCodesVisible &&
            (_session.FocusedElement == _formatCodesView || _session.FocusedElement == _formatCodesSplitter))
        {
            _session.SetFocus(_editor);
        }
        else if (_content.IsFormatCodesVisible)
        {
            _formatCodesController.Refresh();
        }

        _lastAction = _content.IsFormatCodesVisible
            ? "Formatting Codes shown"
            : "Formatting Codes hidden";
        RefreshUi();
    }

    private void CycleFormattingCodesFocus(bool reverse)
    {
        UiElement? focused = _session.FocusedElement;
        if (!_content.IsFormatCodesVisible)
        {
            _session.SetFocus(focused == _menu ? _editor : _menu);
            return;
        }

        UiElement next = reverse
            ? focused == _editor ? _menu : focused == _menu ? _formatCodesView : _editor
            : focused == _editor ? _formatCodesView : focused == _formatCodesView ? _menu : _editor;
        _session.SetFocus(next);
        _lastAction = "Focus moved with F6";
        RefreshUi();
    }

    private void RefreshUi()
    {
        foreach ((UiMenuItem item, RichEditCommand command) in _richEditMenuItems)
        {
            RichEditCommandState state = _editor.GetCommandState(command);
            item.IsEnabled = state.IsEnabled;
            if (item.IsCheckable)
                item.IsChecked = state.IsToggled;
        }

        bool fontEnabled = _editor.GetCommandState(RichEditCommand.SetFont).IsEnabled;
        if (_fontMenuItem is not null)
            _fontMenuItem.IsEnabled = fontEnabled;
        if (_fontToolbarButton is not null)
            _fontToolbarButton.IsEnabled = fontEnabled;

        foreach ((StandardButton button, RichEditCommand command) in _toolbarActionButtons)
            button.IsEnabled = _editor.GetCommandState(command).IsEnabled;

        foreach ((StandardToggleButton button, RichEditCommand command) in _toolbarToggleButtons)
        {
            RichEditCommandState state = _editor.GetCommandState(command);
            button.IsEnabled = state.IsEnabled;
            button.IsChecked = state.IsToggled;
        }

        if (_formatCodesMenuItem is not null)
            _formatCodesMenuItem.IsChecked = _content.IsFormatCodesVisible;
        _status.Text = BuildStatus();
        BrowserInterop.ScheduleFrame();
    }

    private string BuildStatus()
    {
        int paragraphs = _editor.Document.ParagraphCount;
        int chars = _editor.GetPlainText().Length;
        string selection = _editor.Selection.IsEmpty ? "No selection" : "Selection active";
        string style = CurrentStyleText();
        string paragraphText = paragraphs.ToString(CultureInfo.InvariantCulture) + (paragraphs == 1 ? " paragraph" : " paragraphs");
        string charText = chars.ToString(CultureInfo.InvariantCulture) + (chars == 1 ? " character" : " characters");
        string pane = _content.IsFormatCodesVisible
            ? (_formatCodesController.IsProjectionPending ? "Formatting Codes updating" : "Formatting Codes shown")
            : "Formatting Codes hidden";
        return paragraphText + " | " + charText + " | " + selection + " | " + style + " | " + pane + " | " + _lastAction;
    }

    private string CurrentStyleText()
    {
        InlineStyle style = _editor.CaretInlineStyle;
        var names = new List<string>();
        if (style.Bold) names.Add("bold");
        if (style.Italic) names.Add("italic");
        if (style.Underline) names.Add("underline");
        if (style.Strikethrough) names.Add("strike");
        if (!string.IsNullOrWhiteSpace(style.FontFamily)) names.Add(style.FontFamily);
        if (style.FontSize is > 0) names.Add(style.FontSize.Value.ToString("0.###", CultureInfo.InvariantCulture));
        return names.Count == 0 ? "plain" : string.Join(" + ", names);
    }

    private static string FriendlyCommandName(RichEditCommand command) =>
        command switch
        {
            RichEditCommand.SelectAll => "Select all",
            RichEditCommand.ClearFormatting => "Clear formatting",
            RichEditCommand.AlignLeft => "Align left",
            RichEditCommand.AlignCenter => "Align center",
            RichEditCommand.AlignRight => "Align right",
            RichEditCommand.BulletList => "Bullet list",
            RichEditCommand.NumberedList => "Numbered list",
            RichEditCommand.SetFont => "Font",
            _ => command.ToString(),
        };

    private static MouseButton MapButton(int domButton) => domButton switch
    {
        0 => MouseButton.Left,
        1 => MouseButton.Middle,
        2 => MouseButton.Right,
        3 => MouseButton.X1,
        4 => MouseButton.X2,
        _ => MouseButton.None,
    };

    private static readonly (MouseButtons Flag, MouseButton Button)[] ButtonMap =
    [
        (MouseButtons.Left, MouseButton.Left),
        (MouseButtons.Right, MouseButton.Right),
        (MouseButtons.Middle, MouseButton.Middle),
        (MouseButtons.X1, MouseButton.X1),
        (MouseButtons.X2, MouseButton.X2),
    ];

    // ---- Layout -----------------------------------------------------------------------------

    private sealed class WriterContent : UiElement
    {
        private const double Margin = 24;
        private const double TitleTop = 18;
        private const double ToolbarHeight = 42;
        private const double StatusHeight = 24;
        private const double MinWidth = 900;
        private const double MinHeight = 620;

        private readonly StandardMenu _menu;
        private readonly StandardToolbar _toolbar;
        private readonly StandardLabel _title;
        private readonly StandardRichEdit _editor;
        private readonly StandardSplitter _formatCodesSplitter;
        private readonly StandardFormatCodeView _formatCodesView;
        private readonly StandardLabel _status;
        private bool _isFormatCodesVisible = true;

        public WriterContent(
            StandardMenu menu,
            StandardToolbar toolbar,
            StandardLabel title,
            StandardRichEdit editor,
            StandardSplitter formatCodesSplitter,
            StandardFormatCodeView formatCodesView,
            StandardLabel status)
        {
            _menu = menu;
            _toolbar = toolbar;
            _title = title;
            _editor = editor;
            _formatCodesSplitter = formatCodesSplitter;
            _formatCodesView = formatCodesView;
            _status = status;

            AddChild(_menu);
            AddChild(_toolbar);
            AddChild(_title);
            AddChild(_editor);
            AddChild(_formatCodesSplitter);
            AddChild(_formatCodesView);
            AddChild(_status);
        }

        public bool IsFormatCodesVisible
        {
            get => _isFormatCodesVisible;
            set
            {
                if (_isFormatCodesVisible == value)
                    return;
                _isFormatCodesVisible = value;
                _formatCodesSplitter.Visibility = value ? UiVisibility.Visible : UiVisibility.Collapsed;
                _formatCodesView.Visibility = value ? UiVisibility.Visible : UiVisibility.Collapsed;
                Invalidate(UiInvalidationKind.Measure | UiInvalidationKind.Arrange | UiInvalidationKind.Render);
            }
        }

        protected override BSize MeasureCore(BSize availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(MinWidth, availableSize.Width);
            double height = double.IsInfinity(availableSize.Height) ? MinHeight : Math.Max(MinHeight, availableSize.Height);
            double contentWidth = Math.Max(0, width - (Margin * 2));

            _menu.Measure(new BSize(width, _menu.MenuBarHeight));
            _toolbar.Measure(new BSize(width, ToolbarHeight));
            _title.Measure(new BSize(contentWidth, double.PositiveInfinity));
            _editor.Measure(new BSize(contentWidth, Math.Max(240, height - 182)));
            if (_isFormatCodesVisible)
            {
                _formatCodesSplitter.Measure(new BSize(contentWidth, WriterFormatCodesLayout.SplitterThickness));
                _formatCodesView.Measure(new BSize(contentWidth, Math.Max(WriterFormatCodesLayout.MinimumPaneHeight, height * 0.25)));
            }
            _status.Measure(new BSize(contentWidth, StatusHeight));

            return new BSize(width, height);
        }

        protected override void ArrangeCore(BRect finalRect)
        {
            _menu.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, _menu.MenuBarHeight));
            _toolbar.Arrange(new BRect(finalRect.Left, finalRect.Top + _menu.MenuBarHeight, finalRect.Width, ToolbarHeight));

            double x = finalRect.Left + Margin;
            double y = finalRect.Top + _menu.MenuBarHeight + ToolbarHeight + TitleTop;
            double width = Math.Max(0, finalRect.Width - (Margin * 2));

            _title.Arrange(new BRect(x, y, width, _title.DesiredSize.Height));
            y += _title.DesiredSize.Height + 14;

            double statusTop = finalRect.Bottom - Margin - StatusHeight;
            double workspaceHeight = Math.Max(0, statusTop - y - 14);
            WriterFormatCodesLayoutResult layout = WriterFormatCodesLayout.Calculate(
                workspaceHeight, _formatCodesSplitter.Value, _isFormatCodesVisible);
            _editor.Arrange(new BRect(x, y, width, layout.EditorHeight));
            if (_isFormatCodesVisible)
            {
                double splitterTop = y + layout.EditorHeight;
                _formatCodesSplitter.DragExtent = Math.Max(1, workspaceHeight);
                _formatCodesSplitter.Arrange(new BRect(x, splitterTop, width, layout.SplitterHeight));
                _formatCodesView.Arrange(new BRect(
                    x, splitterTop + layout.SplitterHeight, width, layout.PaneHeight));
            }
            _status.Arrange(new BRect(x, statusTop, width, StatusHeight));
        }

        protected override void RenderCore(UiRenderContext context)
        {
            context.RenderList.FillRect(Bounds, WriterPalette.Canvas);
            context.RenderList.FillRect(new BRect(Bounds.Left, Bounds.Top, Bounds.Width, _menu.MenuBarHeight), WriterPalette.MenuSurface);
            context.RenderList.FillRect(new BRect(Bounds.Left, Bounds.Top + _menu.MenuBarHeight, Bounds.Width, 1), WriterPalette.MenuRule);
            context.RenderList.FillRect(new BRect(Bounds.Left, Bounds.Top + _menu.MenuBarHeight + ToolbarHeight, Bounds.Width, 1), WriterPalette.MenuRule);
            base.RenderCore(context);
        }
    }
}
