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
using Broiler.UI.ComboBox;
using Broiler.UI.ComboBox.Standard;
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
using Broiler.UI.Tooltip.Standard;
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
    private static readonly BSize FontDialogPreferredSize = new(560, 384);

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
    private readonly StandardLabel _paneHeader;
    private readonly StandardLabel _status;
    private readonly StandardLabel _statusState;
    private string _documentName = UntitledDocumentName;
    private bool _isModified;
    private int _replacingDocument;
    private string? _problem;
    private readonly StandardTooltip _tooltip;
    private readonly StandardTooltipController _tooltips;
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
    private readonly List<(UiMenuItem Item, double Zoom)> _zoomMenuItems = [];
    private StandardComboBox? _zoomCombo;
    private double _zoom = WriterZoom.Default;
    private bool _isControlHeld;
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
        // The document's name is the page title now, where a browser expects to find it, so the
        // heading that used to repeat it inside the window is gone and the workspace starts
        // directly under the toolbar.
        _paneHeader = new StandardLabel
        {
            Text = "Formatting Codes",
            Font = new BFontStyle("Segoe UI", 12, BFontWeight.SemiBold),
            Foreground = WriterPalette.Muted,
        };
        _status = new StandardLabel
        {
            Text = string.Empty,
            Font = new BFontStyle("Segoe UI", 13),
            Foreground = WriterPalette.Muted,
            Trimming = UiTextTrimming.CharacterEllipsis,
        };
        _statusState = new StandardLabel
        {
            Text = string.Empty,
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
        // Icon buttons say nothing about themselves, so resting on one has to name the command.
        _tooltip = new StandardTooltip
        {
            Font = new BFontStyle("Segoe UI", 12),
            Background = WriterPalette.MenuPopup,
            Foreground = WriterPalette.Title,
            BorderColor = WriterPalette.EditorBorder,
        };
        _tooltips = new StandardTooltipController(_tooltip);
        _content = new WriterContent(
            _menu,
            _toolbar,
            _editor,
            _formatCodesSplitter,
            _paneHeader,
            _formatCodesView,
            _status,
            _statusState);
        _rootWindow.AddChild(_content);
        _rootWindow.OpenOwnedWindow(_tooltip, new BRect(0, 0, 1, 1));
        _tooltip.Deactivate();

        SeedDocument();
        UpdateWindowTitle();
        _formatCodesController = new WriterFormatCodesController(
            _editor, _formatCodesView, _session.Dispatcher);
        _session.AddRoot(_rootWindow);
        _session.SetFocus(_editor);

        _editor.SelectionChanged += (_, _) => RefreshUi();
        _editor.DocumentChanged += (_, _) =>
        {
            MarkModified();
            RefreshUi();
        };
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
        // A tooltip counting out its delay needs frames to notice the clock, and an animation
        // frame is what a browser has instead of a timer. Only while one is waiting.
        if (_tooltips.Tick() || _tooltips.IsWaiting)
            BrowserInterop.ScheduleFrame();

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
            if (_tooltips.PointerMoved(new BPoint(x, y)))
                BrowserInterop.ScheduleFrame();
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

            // Ctrl and the wheel zooms rather than scrolls, and is answered
            // before the session sees it. The page listener already refuses the
            // browser's own zoom for this canvas, so the gesture is ours to take.
            WriterZoomStep wheelStep = horizontal
                ? WriterZoomStep.None
                : WriterZoom.StepForWheel(_isControlHeld, deltaNotches);
            if (wheelStep != WriterZoomStep.None)
            {
                StepZoom(wheelStep);
                return;
            }

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
            _isControlHeld = (modifierState & KeyboardModifierState.Control) != KeyboardModifierState.None;

            WriterZoomStep zoomStep = WriterZoom.StepFor(keyName, nativeKeyCode, modifierState, down);
            if (zoomStep != WriterZoomStep.None)
            {
                StepZoom(zoomStep);
                return;
            }

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
                SetDocumentName(name, modified: false);
                _lastAction = result.Diagnostics.Count == 0
                    ? "Opened " + name
                    : "Opened " + name + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
                ReplaceDocument(() =>
                {
                    _editor.Document = result.Document;
                    _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
                });
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
        dispatcher.Add(new StandardCommand("view.zoom.in", () => StepZoom(WriterZoomStep.In)));
        dispatcher.Add(new StandardCommand("view.zoom.out", () => StepZoom(WriterZoomStep.Out)));
        dispatcher.Add(new StandardCommand("view.zoom.reset", () => StepZoom(WriterZoomStep.Reset)));
        AddZoomLevelCommands(dispatcher);
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
        view.Children.Add(new UiMenuItem("zoom-in", "Zoom in") { CommandName = "view.zoom.in", AccessKey = 'I' });
        view.Children.Add(new UiMenuItem("zoom-out", "Zoom out") { CommandName = "view.zoom.out", AccessKey = 'O' });
        view.Children.Add(new UiMenuItem("zoom-reset", "Actual size") { CommandName = "view.zoom.reset", AccessKey = 'A' });
        view.Children.Add(CreateZoomMenu());

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

    /// <summary>
    /// One command per level on the ladder, named after the percentage it
    /// selects, so the menu and the toolbar reach the zoom the same way the
    /// desktop head does.
    /// </summary>
    private void AddZoomLevelCommands(StandardCommandDispatcher dispatcher)
    {
        foreach (double level in WriterZoom.Levels)
        {
            double zoom = level;
            dispatcher.Add(new StandardCommand(ZoomCommandName(zoom), () => ApplyZoom(zoom)));
        }
    }

    private static string ZoomCommandName(double zoom) =>
        "view.zoom." + Math.Round(zoom * 100).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The ladder as a checkable submenu, which is also where the current level is stated.</summary>
    private UiMenuItem CreateZoomMenu()
    {
        var zoom = new UiMenuItem("zoom", "Zoom") { AccessKey = 'Z' };
        foreach (double level in WriterZoom.Levels)
        {
            var item = new UiMenuItem(
                "zoom-" + Math.Round(level * 100).ToString("0", CultureInfo.InvariantCulture),
                WriterZoom.Describe(level))
            {
                CommandName = ZoomCommandName(level),
                IsCheckable = true,
                IsChecked = WriterZoom.Same(level, _zoom),
            };
            _zoomMenuItems.Add((item, level));
            zoom.Children.Add(item);
        }

        return zoom;
    }

    /// <summary>The toolbar's zoom picker: it shows the level and drops down the whole ladder.</summary>
    private StandardComboBox CreateZoomCombo()
    {
        var combo = new StandardComboBox
        {
            PreferredSize = new BSize(74, 30),
            MaxDropDownItems = WriterZoom.Levels.Count,
            ItemHeight = 26,
            Font = new BFontStyle("Segoe UI", 13),
            Background = WriterPalette.ToolbarButton,
            Foreground = WriterPalette.Title,
            BorderColor = WriterPalette.ToolbarButtonBorder,
            PopupBackground = WriterPalette.MenuPopup,
            SelectedBackground = WriterPalette.MenuSelected,
            FocusRing = WriterPalette.Accent,
            CornerRadius = 5,
        };

        var items = new List<UiComboBoxItem>(WriterZoom.Levels.Count);
        foreach (double level in WriterZoom.Levels)
            items.Add(new UiComboBoxItem(ZoomCommandName(level), WriterZoom.Describe(level)));
        combo.SetItems(items);
        combo.SelectIndex(Math.Max(0, WriterZoom.IndexOf(_zoom)));

        // Only a choice that differs is acted on: RefreshUi drives the selection
        // back from the zoom, and answering that would report a zoom the user
        // never asked for.
        combo.SelectionChanged += (_, e) =>
        {
            if ((uint)e.NewIndex >= (uint)WriterZoom.Levels.Count)
                return;

            double level = WriterZoom.Levels[e.NewIndex];
            if (!WriterZoom.Same(level, _zoom))
                ApplyZoom(level);
        };
        return combo;
    }

    /// <summary>
    /// Reads the document at <paramref name="zoom"/>. The editor is where the
    /// number lives; the menu, the picker and the status line are told from here.
    /// </summary>
    private void ApplyZoom(double zoom)
    {
        double resolved = WriterZoom.Normalize(zoom);
        bool changed = !WriterZoom.Same(resolved, _zoom);
        _zoom = resolved;
        _editor.Zoom = resolved;
        _lastAction = changed
            ? "Zoom " + WriterZoom.Describe(resolved)
            : "Zoom already " + WriterZoom.Describe(resolved);
        RefreshUi();
    }

    private void StepZoom(WriterZoomStep step) => ApplyZoom(WriterZoom.Apply(_zoom, step));

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

    /// <summary>The side of an icon button, and of the icon inside it.</summary>
    private const double IconButtonExtent = 30;

    private const double IconExtent = 16;

    private StandardToolbar CreateToolbar()
    {
        var toolbar = new StandardToolbar
        {
            Title = "Document toolbar",
            PreferredSize = new BSize(0, 40),
            Orientation = UiToolbarOrientation.Horizontal,
            Padding = 5,
            Spacing = 3,

            // Groups are opened with space rather than with rules. Six drawn lines across the bar
            // would be the heaviest thing in the window.
            GroupExtent = 9,
            Background = WriterPalette.ToolbarSurface,
            BorderColor = WriterPalette.MenuRule,
            SeparatorColor = WriterPalette.MenuRule,
            CornerRadius = 0,

            // The bar is wider than the window on every head, so what does not
            // fit goes behind the chevron rather than off the edge.
            Foreground = WriterPalette.Title,
            PopupBackground = WriterPalette.MenuPopup,
            Font = new BFontStyle("Segoe UI", 15),
        };

        StandardButton newButton = ToolbarAction("New", "New document (Ctrl+N)", NewDocument, WriterIcons.NewDocument);
        StandardButton openButton = ToolbarAction("Open", "Open... (Ctrl+O)", RequestOpenDocument, WriterIcons.OpenDocument);
        StandardButton saveButton = ToolbarAction("Save", "Save (Ctrl+S)", SaveDocument, WriterIcons.Save);
        StandardButton saveAsButton = ToolbarAction("Save as", "Save as...", SaveDocumentAs, WriterIcons.SaveAs);
        StandardButton undoButton = ToolbarCommand("Undo", "Undo (Ctrl+Z)", RichEditCommand.Undo, WriterIcons.Undo);
        StandardButton redoButton = ToolbarCommand("Redo", "Redo (Ctrl+Y)", RichEditCommand.Redo, WriterIcons.Redo);

        // Font and the zoom level keep their words: a typeface name and a percentage are values,
        // not commands, and no picture states either of them. B, I, U and S stay letters for the
        // same reason in reverse - a letterform is the one icon a font draws better than geometry.
        StandardButton fontButton = ToolbarText("Font...", "Font...", 62, ShowFontDialog);
        _fontToolbarButton = fontButton;
        StandardToggleButton boldButton = ToolbarToggle("B", "Bold (Ctrl+B)", RichEditCommand.Bold, BFontWeight.Bold);
        StandardToggleButton italicButton = ToolbarToggle("I", "Italic (Ctrl+I)", RichEditCommand.Italic, BFontWeight.Normal, BFontSlant.Italic);
        StandardToggleButton underlineButton = ToolbarToggle("U", "Underline (Ctrl+U)", RichEditCommand.Underline, BFontWeight.Normal);
        StandardToggleButton strikeButton = ToolbarToggle("S", "Strikethrough", RichEditCommand.Strikethrough, BFontWeight.Normal);
        StandardButton clearButton = ToolbarCommand("Clear", "Clear formatting", RichEditCommand.ClearFormatting, WriterIcons.ClearFormatting);
        StandardToggleButton leftButton = ToolbarToggle("Left", "Align left (Ctrl+L)", RichEditCommand.AlignLeft, BFontWeight.Normal, icon: WriterIcons.AlignLeft);
        StandardToggleButton centerButton = ToolbarToggle("Center", "Align center (Ctrl+E)", RichEditCommand.AlignCenter, BFontWeight.Normal, icon: WriterIcons.AlignCenter);
        StandardToggleButton rightButton = ToolbarToggle("Right", "Align right (Ctrl+R)", RichEditCommand.AlignRight, BFontWeight.Normal, icon: WriterIcons.AlignRight);
        StandardToggleButton bulletsButton = ToolbarToggle("Bullets", "Bullet list", RichEditCommand.BulletList, BFontWeight.Normal, icon: WriterIcons.BulletList);
        StandardToggleButton numberedButton = ToolbarToggle("Numbered", "Numbered list", RichEditCommand.NumberedList, BFontWeight.Normal, icon: WriterIcons.NumberedList);
        StandardButton indentButton = ToolbarCommand("Indent", "Indent", RichEditCommand.Indent, WriterIcons.Indent);
        StandardButton outdentButton = ToolbarCommand("Outdent", "Outdent", RichEditCommand.Outdent, WriterIcons.Outdent);

        // Ahead of the formatting groups rather than after them: this toolbar is
        // already wider than the window it opens in, and a control at the end is
        // one the user never sees.
        StandardButton zoomOutButton = ToolbarText("-", "Zoom out (Ctrl+minus)", IconButtonExtent, () => StepZoom(WriterZoomStep.Out));
        StandardButton zoomInButton = ToolbarText("+", "Zoom in (Ctrl+plus)", IconButtonExtent, () => StepZoom(WriterZoomStep.In));
        StandardComboBox zoomCombo = CreateZoomCombo();
        _zoomCombo = zoomCombo;

        toolbar.AddChild(newButton);
        toolbar.AddChild(openButton);
        toolbar.AddChild(saveButton);
        toolbar.AddChild(saveAsButton);
        toolbar.AddChild(undoButton);
        toolbar.AddChild(redoButton);
        toolbar.AddChild(zoomOutButton);
        toolbar.AddChild(zoomCombo);
        toolbar.AddChild(zoomInButton);
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

        // File | history | zoom | character | paragraph | lists | indent, all gaps and no rules.
        toolbar.SetBreakBefore(undoButton, UiToolbarBreak.Gap);
        toolbar.SetBreakBefore(zoomOutButton, UiToolbarBreak.Gap);
        toolbar.SetBreakBefore(fontButton, UiToolbarBreak.Gap);
        toolbar.SetBreakBefore(leftButton, UiToolbarBreak.Gap);
        toolbar.SetBreakBefore(bulletsButton, UiToolbarBreak.Gap);
        toolbar.SetBreakBefore(indentButton, UiToolbarBreak.Gap);

        return toolbar;
    }

    /// <summary>An icon button that runs an application action.</summary>
    private StandardButton ToolbarAction(
        string text,
        string tip,
        Action action,
        Action<BRenderList, BRect, BColor> icon)
    {
        StandardButton button = CreateToolbarButton(text, tip, IconButtonExtent, icon);
        button.Clicked += (_, _) =>
        {
            action();
            RefreshUi();
        };
        return button;
    }

    /// <summary>A button that keeps its caption, for the values no picture states.</summary>
    private StandardButton ToolbarText(string text, string tip, double width, Action action)
    {
        StandardButton button = CreateToolbarButton(text, tip, width, icon: null);
        button.Clicked += (_, _) =>
        {
            action();
            RefreshUi();
        };
        return button;
    }

    private StandardButton ToolbarCommand(
        string text,
        string tip,
        RichEditCommand command,
        Action<BRenderList, BRect, BColor> icon)
    {
        StandardButton button = CreateToolbarButton(text, tip, IconButtonExtent, icon);
        button.Clicked += (_, _) => RunRichEditCommand(command);
        _toolbarActionButtons.Add((button, command));
        return button;
    }

    private StandardToggleButton ToolbarToggle(
        string text,
        string tip,
        RichEditCommand command,
        BFontWeight weight,
        BFontSlant slant = BFontSlant.Normal,
        Action<BRenderList, BRect, BColor>? icon = null)
    {
        var button = new StandardToggleButton
        {
            Text = text,
            ToolTipText = tip,
            PreferredSize = new BSize(IconButtonExtent, IconButtonExtent),
            Font = new BFontStyle("Segoe UI", 13, weight, slant),
            PaddingX = 7,
            PaddingY = 5,
            IconPainter = icon,
            IconExtent = IconExtent,
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

    private static StandardButton CreateToolbarButton(
        string text,
        string tip,
        double width,
        Action<BRenderList, BRect, BColor>? icon) =>
        new()
        {
            // The caption stays set even when an icon is what gets drawn: it is the name the
            // button reports to a screen reader, and the name the overflow drop-down finds it by.
            Text = text,
            ToolTipText = tip,
            PreferredSize = new BSize(width, IconButtonExtent),
            Font = new BFontStyle("Segoe UI", 13),
            PaddingX = 7,
            PaddingY = 5,
            IconPainter = icon,
            IconExtent = IconExtent,
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
        ReplaceDocument(() => _editor.SetPlainText(string.Empty));
        SetDocumentName(UntitledDocumentName, modified: false);
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
            SetDocumentName(resolved, modified: false);
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
            Underline = _editor.GetCommandState(RichEditCommand.Underline).IsToggled,
            Strikethrough = _editor.GetCommandState(RichEditCommand.Strikethrough).IsToggled,
            SampleText = "Broiler Writer font preview",
            TitleFont = new BFontStyle("Segoe UI", 14, BFontWeight.SemiBold),
            LabelFont = new BFontStyle("Segoe UI", 13),
        };
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted)
                ApplySelectedFont(dialog.SelectedFont, dialog.Underline, dialog.Strikethrough);
        };

        dialog.ShowFontModal(_rootWindow, GetFontDialogPlacement());
        _lastAction = "Font dialog";
        RefreshUi();
    }

    private void ApplySelectedFont(BFontStyle font, bool underline, bool strikethrough)
    {
        bool ran = _editor.ExecuteCommand(RichEditCommand.SetFont, font);
        ran |= ApplyDecoration(RichEditCommand.Underline, underline);
        ran |= ApplyDecoration(RichEditCommand.Strikethrough, strikethrough);
        _lastAction = ran
            ? "Font: " + font.FamilyName + " " + font.SizeInPixels.ToString("0.###", CultureInfo.InvariantCulture)
            : "Font unavailable";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    /// <summary>
    /// Puts one decoration into the state the dialog settled on. The editor's commands toggle
    /// rather than set, so this asks what it is first and only runs the command when the two
    /// disagree — running it unconditionally would turn off the underline the dialog was asked to
    /// leave on.
    /// </summary>
    private bool ApplyDecoration(RichEditCommand command, bool wanted)
    {
        RichEditCommandState state = _editor.GetCommandState(command);
        if (!state.IsEnabled || state.IsToggled == wanted)
            return false;

        return _editor.ExecuteCommand(command);
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
            _status.Text + " · " + _statusState.Text,
            darkTheme: false);
    }

    // ---- Status + seeding -------------------------------------------------------------------

    private void SeedDocument()
    {
        ReplaceDocument(() => _editor.SetPlainText(
            "Broiler Writer\n" +
            "This browser build is a Broiler.UI window with a Broiler-rendered menu and StandardRichEdit document surface, presented through the direct-Canvas 2D backend.\n" +
            "Use the Edit and Format menus, or keyboard shortcuts such as Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+Z, and Ctrl+Y. Open and Save round-trip through the Broiler.Documents RTF, DOCX, HTML, and Markdown codecs."));

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

        foreach ((UiMenuItem item, double level) in _zoomMenuItems)
            item.IsChecked = WriterZoom.Same(level, _zoom);

        int zoomIndex = WriterZoom.IndexOf(_zoom);
        if (_zoomCombo is not null && zoomIndex >= 0)
            _zoomCombo.SelectIndex(zoomIndex);

        _status.Text = BuildStatusFacts();
        _statusState.Text = BuildStatusState();
        BrowserInterop.ScheduleFrame();
    }

    /// <summary>What the status line says on the left: what is in the document.</summary>
    private string BuildStatusFacts()
    {
        int paragraphs = _editor.Document.ParagraphCount;
        int chars = _editor.GetPlainText().Length;
        string paragraphText = paragraphs.ToString(CultureInfo.InvariantCulture) + (paragraphs == 1 ? " paragraph" : " paragraphs");
        string charText = chars.ToString(CultureInfo.InvariantCulture) + (chars == 1 ? " character" : " characters");
        string facts = paragraphText + " · " + charText;
        return _editor.Selection.IsEmpty ? facts : facts + " · selection";
    }

    /// <summary>
    /// What it says on the right: the state you are working in, and anything that went wrong.
    /// </summary>
    private string BuildStatusState()
    {
        string state = CurrentStyleText() + " · " + WriterZoom.Describe(_zoom);
        return _problem is null ? state : _problem + " · " + state;
    }

    /// <summary>What a document with no file behind it is called.</summary>
    private const string UntitledDocumentName = "Untitled document";

    /// <summary>The application's own name, which the page title ends with.</summary>
    private const string ApplicationName = "Broiler Writer";

    /// <summary>
    /// Names the document, and says whether it has unsaved changes. In a browser the page title is
    /// the caption, so that is where it goes.
    /// </summary>
    private void SetDocumentName(string? name, bool modified)
    {
        _problem = null;
        _documentName = string.IsNullOrWhiteSpace(name) ? UntitledDocumentName : name;
        _isModified = modified;
        UpdateWindowTitle();
    }

    private void MarkModified()
    {
        // A load raises DocumentChanged exactly as a keystroke does.
        if (_replacingDocument > 0 || _isModified)
            return;

        _isModified = true;
        UpdateWindowTitle();
    }

    private void ReplaceDocument(Action replace)
    {
        _replacingDocument++;
        try
        {
            replace();
        }
        finally
        {
            _replacingDocument--;
        }
    }

    /// <summary>The page title: the document, then the application, with a dot for unsaved changes.</summary>
    internal string WindowTitle =>
        (_isModified ? "• " : string.Empty) + _documentName + " — " + ApplicationName;

    private void UpdateWindowTitle()
    {
        _rootWindow.Title = WindowTitle;
        BrowserInterop.SetTitle(WindowTitle);
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
    /// <summary>
    /// The window's contents: menu, toolbar, document, the Formatting Codes panel, and the status
    /// line.
    /// </summary>
    /// <remarks>
    /// The document's name used to sit here as a large heading above the editor. It has moved to
    /// the title bar, which is where an application says what is open; repeating it inside the
    /// window cost a row of vertical space and said nothing the caption did not. The workspace
    /// starts directly under the toolbar now.
    ///
    /// Formatting Codes is a panel rather than a second editor that happens to be underneath one.
    /// It has a header naming it and a splitter drawn as its top edge, so it reads as something
    /// attached to the window rather than as a document that lost its frame - and when it is
    /// hidden, its header and splitter go with it and the document takes the whole space.
    /// </remarks>
    private sealed class WriterContent : UiElement
    {
        private const double Margin = 24;
        private const double ToolbarHeight = 40;
        private const double WorkspaceTop = 14;
        private const double StatusHeight = 22;
        private const double StatusGap = 12;
        private const double PaneHeaderHeight = 20;
        private const double MinWidth = 900;
        private const double MinHeight = 620;

        private readonly StandardMenu _menu;
        private readonly StandardToolbar _toolbar;
        private readonly StandardRichEdit _editor;
        private readonly StandardSplitter _formatCodesSplitter;
        private readonly StandardLabel _paneHeader;
        private readonly StandardFormatCodeView _formatCodesView;
        private readonly StandardLabel _status;
        private readonly StandardLabel _statusState;
        private bool _isFormatCodesVisible = true;

        public WriterContent(
            StandardMenu menu,
            StandardToolbar toolbar,
            StandardRichEdit editor,
            StandardSplitter formatCodesSplitter,
            StandardLabel paneHeader,
            StandardFormatCodeView formatCodesView,
            StandardLabel status,
            StandardLabel statusState)
        {
            _menu = menu;
            _toolbar = toolbar;
            _editor = editor;
            _formatCodesSplitter = formatCodesSplitter;
            _paneHeader = paneHeader;
            _formatCodesView = formatCodesView;
            _status = status;
            _statusState = statusState;

            AddChild(_menu);
            AddChild(_toolbar);
            AddChild(_editor);
            AddChild(_formatCodesSplitter);
            AddChild(_paneHeader);
            AddChild(_formatCodesView);
            AddChild(_status);
            AddChild(_statusState);
        }

        public bool IsFormatCodesVisible
        {
            get => _isFormatCodesVisible;
            set
            {
                if (_isFormatCodesVisible == value)
                    return;
                _isFormatCodesVisible = value;
                UiVisibility visibility = value ? UiVisibility.Visible : UiVisibility.Collapsed;
                _formatCodesSplitter.Visibility = visibility;
                _paneHeader.Visibility = visibility;
                _formatCodesView.Visibility = visibility;
                Invalidate(UiInvalidationKind.Measure | UiInvalidationKind.Arrange | UiInvalidationKind.Render);
            }
        }

        /// <summary>The height the pane's own chrome takes above the code view, or zero when hidden.</summary>
        private double PaneChromeHeight =>
            _isFormatCodesVisible ? WriterFormatCodesLayout.SplitterThickness + PaneHeaderHeight : 0;

        protected override BSize MeasureCore(BSize availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(0, availableSize.Width);
            double height = double.IsInfinity(availableSize.Height) ? MinHeight : Math.Max(0, availableSize.Height);
            double contentWidth = Math.Max(0, width - (Margin * 2));

            _menu.Measure(new BSize(width, _menu.MenuBarHeight));
            _toolbar.Measure(new BSize(width, ToolbarHeight));
            _editor.Measure(new BSize(contentWidth, Math.Max(240, height - ChromeHeight())));
            if (_isFormatCodesVisible)
            {
                _formatCodesSplitter.Measure(new BSize(contentWidth, WriterFormatCodesLayout.SplitterThickness));
                _paneHeader.Measure(new BSize(contentWidth, PaneHeaderHeight));
                _formatCodesView.Measure(new BSize(contentWidth, Math.Max(WriterFormatCodesLayout.MinimumPaneHeight, height * 0.25)));
            }
            _status.Measure(new BSize(contentWidth, StatusHeight));
            _statusState.Measure(new BSize(contentWidth, StatusHeight));

            return new BSize(width, height);
        }

        /// <summary>
        /// Everything above and below the workspace. Kept in one place because the editor's measure
        /// and the arrange pass have to agree about it, and they used to agree by both containing
        /// the number 182.
        /// </summary>
        private double ChromeHeight() =>
            _menu.MenuBarHeight + ToolbarHeight + WorkspaceTop + StatusGap + StatusHeight + (Margin * 2);

        protected override void ArrangeCore(BRect finalRect)
        {
            const double toolbarHeight = ToolbarHeight;
            _menu.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, _menu.MenuBarHeight));
            _toolbar.Arrange(new BRect(finalRect.Left, finalRect.Top + _menu.MenuBarHeight, finalRect.Width, toolbarHeight));

            double margin = Margin;
            double x = finalRect.Left + margin;
            double y = finalRect.Top + _menu.MenuBarHeight + toolbarHeight + WorkspaceTop;
            double width = Math.Max(0, finalRect.Width - (margin * 2));

            double statusTop = finalRect.Bottom - margin - StatusHeight;
            double workspaceHeight = Math.Max(0, statusTop - y - StatusGap);

            // The pane's header and splitter come out of the workspace before it is split, so the
            // ratio the splitter reports still means what it says about the two text surfaces.
            double splitHeight = Math.Max(0, workspaceHeight - PaneChromeHeight);
            WriterFormatCodesLayoutResult layout = WriterFormatCodesLayout.Calculate(
                splitHeight, _formatCodesSplitter.Value, _isFormatCodesVisible);

            _editor.Arrange(new BRect(x, y, width, layout.EditorHeight));
            if (_isFormatCodesVisible)
            {
                double splitterTop = y + layout.EditorHeight;
                _formatCodesSplitter.DragExtent = Math.Max(1, splitHeight);
                _formatCodesSplitter.Arrange(new BRect(x, splitterTop, width, layout.SplitterHeight));

                double headerTop = splitterTop + layout.SplitterHeight;
                _paneHeader.Arrange(new BRect(x, headerTop, width, PaneHeaderHeight));
                _formatCodesView.Arrange(new BRect(x, headerTop + PaneHeaderHeight, width, layout.PaneHeight));
            }

            // Facts on the left, state on the right. The right-hand label is placed by its own
            // width rather than aligned, because a label does not align itself.
            double stateWidth = Math.Min(width, _statusState.DesiredSize.Width);
            _status.Arrange(new BRect(x, statusTop, Math.Max(0, width - stateWidth - 16), StatusHeight));
            _statusState.Arrange(new BRect(x + width - stateWidth, statusTop, stateWidth, StatusHeight));
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
