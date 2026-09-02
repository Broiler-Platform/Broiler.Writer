using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Broiler.Documents;
using Broiler.Documents.FormatCodes;
using Broiler.Documents.Model;
using Broiler.Graphics;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ComboBox;
using Broiler.UI.ComboBox.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.FileDialog;
using Broiler.UI.FileDialog.Standard;
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

namespace Broiler.Writer;

internal sealed class WriterApp : IDisposable
{
    private readonly WriterUiHost _host;
    private readonly Action _requestClose;
    private readonly Action? _requestOpenDocument;
    private readonly Action<string, bool>? _requestSaveDocument;
    private readonly bool _compactMode;
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
    private readonly Action<string>? _setWindowTitle;
    private readonly Action<bool>? _setTicking;
    private readonly StandardTooltip _tooltip;
    private readonly StandardTooltipController _tooltips;
    private bool _isTicking;
    private readonly WriterDocumentFormats _documentFormats;
    private readonly DocumentCodecCatalog _documentCatalog;
    private readonly UiFileDialogFilter[] _openDocumentFileFilters;
    private readonly UiFileDialogFilter[] _saveDocumentFileFilters;
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
    private string? _currentDocumentPath;
    private string _documentName = UntitledDocumentName;
    private bool _isModified;

    /// <summary>
    /// Depth of the loads that are currently replacing the document wholesale. The editor raises
    /// DocumentChanged for a programmatic load exactly as it does for a keystroke, so without this
    /// every file would be marked modified the instant it finished opening.
    /// </summary>
    private int _replacingDocument;
    private string _lastDirectory = Environment.CurrentDirectory;

    /// <summary>
    /// The decisions made about this document's pictures. Replaced when a
    /// document is opened, and added to when one is inserted, so a save writes
    /// exactly the resources this session decided on.
    /// </summary>
    private DocumentConversionContextBuilder _resources =
        new(DocumentResourcePolicy.AllowOwnDocuments);
    private string _lastAction = "Ready";

    /// <summary>
    /// The last thing that went wrong, or null. Kept apart from <see cref="_lastAction"/> because
    /// the status line shows one and not the other: "Bold" is not worth a permanent place on it,
    /// and "Could not open report.docx" is. Deciding that by looking for words like "failed" in
    /// the action text was the first attempt, and it missed "Could not" - which is exactly the
    /// kind of thing sniffing prose gets wrong.
    /// </summary>
    private string? _problem;
    private IReadOnlyList<DocumentDiagnostic> _lastReadDiagnostics = Array.Empty<DocumentDiagnostic>();
    private string _lastReadFileName = "this document";

    private static readonly BSize FileDialogPreferredSize = new(820, 520);
    private static readonly BSize FontDialogPreferredSize = new(560, 384);
    private static readonly UiFileDialogFilter[] OpenPictureFileFilters =
    [
        new("Images", "*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp", ".png"),
        new("PNG (*.png)", "*.png", ".png"),
        new("JPEG (*.jpg, *.jpeg)", "*.jpg;*.jpeg", ".jpg"),
        new("GIF (*.gif)", "*.gif", ".gif"),
        new("Bitmap (*.bmp)", "*.bmp", ".bmp"),
    ];

    /// <summary>The widest a freshly inserted picture is placed at, in editor units.</summary>
    private const double MaxInsertedPictureWidth = 420;

    /// <summary>What a document with no file behind it is called.</summary>
    private const string UntitledDocumentName = "Untitled document";

    /// <summary>The application's own name, which the window caption ends with.</summary>
    private const string ApplicationName = "Broiler Writer";

    /// <param name="documentFormats">
    /// The formats this Writer offers. Null composes
    /// <see cref="WriterDocumentFormats.CreateDefault"/> — the RTF, DOCX, HTML and
    /// Markdown set every head has always had. A head that carries more (the
    /// desktop heads register the PDF codec for opening) passes its own set here,
    /// which is what keeps that codec out of the heads that did not ask for it.
    /// </param>
    public WriterApp(
        WriterUiHost host,
        Action requestClose,
        Action? requestOpenDocument = null,
        Action<string, bool>? requestSaveDocument = null,
        bool compactMode = false,
        WriterDocumentFormats? documentFormats = null,
        Action<string>? setWindowTitle = null,
        Action<bool>? setTicking = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));
        _requestOpenDocument = requestOpenDocument;
        _requestSaveDocument = requestSaveDocument;
        _compactMode = compactMode;
        _setWindowTitle = setWindowTitle;
        _setTicking = setTicking;
        _documentFormats = documentFormats ?? WriterDocumentFormats.CreateDefault();
        _documentCatalog = _documentFormats.CreateOpenCatalog();
        _openDocumentFileFilters = _documentFormats.CreateOpenFilters();
        _saveDocumentFileFilters = _documentFormats.CreateSaveFilters();
        _session = new StandardUiSessionBuilder()
            .WithDispatcher(new ImmediateUiDispatcher())
            .Build(_host);

        _editor = new StandardRichEdit
        {
            PreferredSize = new BSize(760, 520),
            PlaceholderText = "Start writing in Broiler Writer...",
            Font = new BFontStyle("Segoe UI", 17),
            Background = WriterPalette.Page,
            BorderColor = WriterPalette.EditorBorder,
            FocusRing = WriterPalette.Accent,

            // A hairline when the editor is not focused, and the accent only when it is. The
            // border used to be two pixels of blue whatever was happening, which made the frame
            // the loudest element in the window and left focus with nothing to announce itself
            // with. The page reads as paper against the darker canvas now, so the frame can be
            // almost nothing.
            BorderThickness = 1,
            FocusRingThickness = 1.6,
            CornerRadius = 4,
            PaddingX = 18,
            PaddingY = 16,
        };
        _formatCodesView = new StandardFormatCodeView
        {
            PreferredSize = new BSize(760, 160),
            Font = new BFontStyle("Cascadia Mono", 14),
            Background = WriterPalette.FormatCodesSurface,
            Foreground = WriterPalette.Title,
            InlineCodeForeground = WriterPalette.FormatCodesInline,
            ParagraphCodeForeground = WriterPalette.FormatCodesParagraph,
            StructureCodeForeground = WriterPalette.FormatCodesStructure,
            EscapeForeground = WriterPalette.FormatCodesEscape,
            PendingForeground = WriterPalette.FormatCodesPending,
            BorderColor = WriterPalette.EditorBorder,
            FocusRing = WriterPalette.Accent,
            BorderThickness = 1,
            FocusRingThickness = 1.6,
            CornerRadius = 4,
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
        // The document's name used to be a heading inside the window. It is in the title bar now,
        // where every other application puts it, so the workspace starts directly under the
        // toolbar and the name is not said twice.
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
        // Icon buttons say nothing about themselves, so the shortcut and the name have to be
        // reachable by resting on one. The tooltip is an owned window of the root, drawn over
        // everything else, and deactivated because it is a label rather than somewhere to go.
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
        if (compactMode)
            _content.IsFormatCodesVisible = false;
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

    public UiSession Session => _session;

    /// <summary>The document currently in the editor.</summary>
    internal RichTextDocument Document => _editor.Document;

    /// <summary>
    /// The diagnostics of the most recent read, kept so a document that opens
    /// unexpectedly blank can be explained rather than guessed at.
    /// </summary>
    internal IReadOnlyList<DocumentDiagnostic> LastReadDiagnostics => _lastReadDiagnostics;

    /// <summary>The formats this Writer was composed with.</summary>
    internal WriterDocumentFormats DocumentFormats => _documentFormats;

    /// <summary>The most recent status-bar line, which is where a refused open is reported.</summary>
    internal string LastAction => _lastAction;

    /// <summary>The size the document is being read at, which nothing but the view depends on.</summary>
    internal double Zoom => _zoom;

    /// <summary>The menu bar, so a test can reach a command surface the user reaches by opening a menu.</summary>
    internal StandardMenu Menu => _menu;

    /// <summary>The toolbar, so a test can check that nothing on it is drawn past its edge.</summary>
    internal StandardToolbar Toolbar => _toolbar;

    /// <summary>The document surface, so a test can type into it and read back where it sits.</summary>
    internal StandardRichEdit Editor => _editor;

    public BRenderList RenderFrame() => _session.RenderFrame();

    public void Dispatch(UiInputEvent input)
    {
        TrackModifiers(input);
        if (HandleZoomShortcut(input))
        {
            _host.RequestInvalidate();
            return;
        }

        if (HandleFormattingCodesShortcut(input))
        {
            _host.RequestInvalidate();
            return;
        }

        if (_session.DispatchInput(input))
            _host.RequestInvalidate();

        UpdateTooltip(input);
    }

    /// <summary>
    /// Lets a pending tooltip's delay advance. A host that only draws when something changed has
    /// to call this while <see cref="WantsTick"/> is true, or the delay never elapses.
    /// </summary>
    public void AnimationTick()
    {
        if (_tooltips.Tick())
            _host.RequestInvalidate();

        SyncTicking();
    }

    /// <summary>Whether anything is currently waiting on the clock rather than on input.</summary>
    public bool WantsTick => _tooltips.IsWaiting;

    /// <summary>
    /// Points the tooltip at whatever the pointer is resting on. Anything that is not a pointer
    /// move dismisses it: a click, a keystroke or a menu opening all mean the user has moved on.
    /// </summary>
    private void UpdateTooltip(UiInputEvent input)
    {
        bool changed = input.Kind == UiInputEventKind.PointerMove
            ? _tooltips.PointerMoved(input.Position)
            : _tooltips.Dismiss();

        SyncTicking();
        if (changed)
            _host.RequestInvalidate();
    }

    /// <summary>Tells the head to start or stop ticking, and only when the answer changes.</summary>
    private void SyncTicking()
    {
        bool wanted = _tooltips.IsWaiting;
        if (wanted == _isTicking)
            return;

        _isTicking = wanted;
        _setTicking?.Invoke(wanted);
    }

    public void Invalidate() => _host.RequestInvalidate();

    public bool LoadDocument(Stream stream, string displayName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        displayName = string.IsNullOrWhiteSpace(displayName) ? "Untitled.rtf" : displayName;

        try
        {
            // The input replays the probed prefix ahead of the rest, so a stream
            // the host cannot rewind is still probed and read exactly once.
            using DocumentInput input = DocumentInput.FromStream(stream);
            DocumentCodecSelection selection = ReadDocument(displayName, input);
            if (!MayReplaceDocument(selection.Result))
            {
                _problem = _lastAction = DescribeRefusedOpen(Path.GetFileName(displayName), selection);
                RefreshUi();
                return false;
            }

            _currentDocumentPath = displayName;
            SetDocumentName(Path.GetFileName(displayName), modified: false);
            _lastAction = DescribeOpen(Path.GetFileName(displayName), selection.Result);
            ReplaceDocument(() =>
            {
                _editor.Document = selection.Result.Document;
                _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
            });
            _session.SetFocus(_editor);
            RefreshUi();
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _problem = _lastAction = "Open failed: " + ex.Message;
            RefreshUi();
            return false;
        }
    }

    public bool WriteDocument(Stream stream, string displayName, bool updateIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        displayName = string.IsNullOrWhiteSpace(displayName) ? "Untitled.rtf" : displayName;

        try
        {
            DocumentWriteResult result = WriteDocument(displayName, _editor.Document, out byte[] bytes);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            if (updateIdentity)
            {
                _currentDocumentPath = displayName;
                SetDocumentName(Path.GetFileName(displayName), modified: false);
                _lastAction = result.Diagnostics.Count == 0
                    ? "Saved " + Path.GetFileName(displayName)
                    : "Saved " + Path.GetFileName(displayName) + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
                _session.SetFocus(_editor);
                RefreshUi();
            }
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _problem = _lastAction = "Save failed: " + ex.Message;
            RefreshUi();
            return false;
        }
    }

    public void Dispose()
    {
        _formatCodesController.Dispose();
        _session.Dispose();
    }

    private StandardMenu CreateMenu()
    {
        var dispatcher = new StandardCommandDispatcher();
        dispatcher.Add(new StandardCommand("file.new", NewDocument));
        dispatcher.Add(new StandardCommand("file.open", ShowOpenDialog));
        dispatcher.Add(new StandardCommand("file.save", SaveDocument));
        dispatcher.Add(new StandardCommand("file.save-as", ShowSaveDialog));
        dispatcher.Add(new StandardCommand("file.exit", _requestClose));
        dispatcher.Add(new StandardCommand("insert.picture", ShowInsertPictureDialog));
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
        AddRichEditCommand(dispatcher, "paragraph.justify", RichEditCommand.AlignJustify);
        AddRichEditCommand(dispatcher, "paragraph.bullets", RichEditCommand.BulletList);
        AddRichEditCommand(dispatcher, "paragraph.numbered", RichEditCommand.NumberedList);
        AddRichEditCommand(dispatcher, "paragraph.indent", RichEditCommand.Indent);
        AddRichEditCommand(dispatcher, "paragraph.outdent", RichEditCommand.Outdent);

        var file = new UiMenuItem("file", "File") { AccessKey = 'F' };
        file.Children.Add(new UiMenuItem("new", "New document") { CommandName = "file.new", AccessKey = 'N' });
        file.Children.Add(new UiMenuItem("open", "Open...") { CommandName = "file.open", AccessKey = 'O' });
        file.Children.Add(new UiMenuItem("save", "Save") { CommandName = "file.save", AccessKey = 'S' });
        file.Children.Add(new UiMenuItem("save-as", "Save as...") { CommandName = "file.save-as", AccessKey = 'A' });
        file.Children.Add(new UiMenuItem("exit", "Exit") { CommandName = "file.exit", AccessKey = 'X' });

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

        format.Children.Add(new UiMenuItem("picture", "Insert picture...")
        {
            CommandName = "insert.picture",
            AccessKey = 'R',
        });

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
        paragraph.Children.Add(RichEditItem("justify", "Justify", "paragraph.justify", RichEditCommand.AlignJustify, 'J', checkable: true));
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
        help.Children.Add(new UiMenuItem("notes", "Notes for this document...") { CommandName = "help.notes", AccessKey = 'N' });
        dispatcher.Add(new StandardCommand("help.notes", ShowDocumentNotes, () => _lastReadDiagnostics.Count > 0));
        help.Children.Add(new UiMenuItem("about", "About Broiler Writer") { CommandName = "help.about", AccessKey = 'A' });
        dispatcher.Add(new StandardCommand("help.about", ShowAbout));

        var menu = new StandardMenu
        {
            PresentationMode = UiMenuPresentationMode.MenuBar,
            PreferredSize = new BSize(360, _compactMode ? 40 : 30),
            MenuBarHeight = _compactMode ? 40 : 30,
            ItemHeight = _compactMode ? 44 : 28,
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
    /// selects. The menu and the toolbar both go through these rather than
    /// setting the zoom themselves, so every way of choosing 150% is the same
    /// way.
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

    /// <summary>
    /// The ladder as a checkable submenu. It offers the levels and says which one
    /// the document is being read at, which is the half of it a menu can do that
    /// Zoom in and Zoom out cannot.
    /// </summary>
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

    /// <summary>
    /// The toolbar's zoom picker. It shows the level the document is being read
    /// at and drops down the whole ladder, which is the one control that answers
    /// "what am I looking at" without opening a menu.
    /// </summary>
    private StandardComboBox CreateZoomCombo()
    {
        var combo = new StandardComboBox
        {
            PreferredSize = new BSize(74, _compactMode ? 40 : 30),
            MaxDropDownItems = WriterZoom.Levels.Count,
            ItemHeight = _compactMode ? 40 : 26,
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
    /// number lives; the menu, the picker and the status line are told from here,
    /// so none of them can disagree with what is on screen.
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

    /// <summary>
    /// The side of an icon button. Square, because there is no caption to make it wider, and a
    /// row of squares is what stops the bar reading as a row of form buttons.
    /// </summary>
    private double IconButtonExtent => _compactMode ? 40 : 30;

    /// <summary>The icon inside that button. A touch bar gets a larger one along with a larger button.</summary>
    private double IconExtent => _compactMode ? 22 : 16;

    private StandardToolbar CreateToolbar()
    {
        var toolbar = new StandardToolbar
        {
            Title = "Document toolbar",
            PreferredSize = new BSize(0, _compactMode ? 50 : 40),
            Orientation = UiToolbarOrientation.Horizontal,
            Padding = 5,
            Spacing = 3,

            // Groups are opened with space rather than with rules. The bar has seven of them, and
            // seven drawn lines across it would be the heaviest thing in the window.
            GroupExtent = _compactMode ? 12 : 9,
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
        StandardButton openButton = ToolbarAction("Open", "Open... (Ctrl+O)", ShowOpenDialog, WriterIcons.OpenDocument);
        StandardButton saveButton = ToolbarAction("Save", "Save (Ctrl+S)", SaveDocument, WriterIcons.Save);
        StandardButton saveAsButton = ToolbarAction("Save as", "Save as...", ShowSaveDialog, WriterIcons.SaveAs);
        StandardButton undoButton = ToolbarCommand("Undo", "Undo (Ctrl+Z)", RichEditCommand.Undo, WriterIcons.Undo);
        StandardButton redoButton = ToolbarCommand("Redo", "Redo (Ctrl+Y)", RichEditCommand.Redo, WriterIcons.Redo);

        // Font and the zoom level keep their words: a typeface name and a percentage are values,
        // not commands, and no picture states either of them.
        StandardButton fontButton = ToolbarText("Font...", "Font...", 62, ShowFontDialog);
        _fontToolbarButton = fontButton;

        // B, I, U and S stay letters too. A letterform is the one icon a font draws better than
        // geometry does, and everybody already reads these four.
        StandardToggleButton boldButton = ToolbarToggle("B", "Bold (Ctrl+B)", RichEditCommand.Bold, BFontWeight.Bold);
        StandardToggleButton italicButton = ToolbarToggle("I", "Italic (Ctrl+I)", RichEditCommand.Italic, BFontWeight.Normal, BFontSlant.Italic);
        StandardToggleButton underlineButton = ToolbarToggle("U", "Underline (Ctrl+U)", RichEditCommand.Underline, BFontWeight.Normal);
        StandardToggleButton strikeButton = ToolbarToggle("S", "Strikethrough", RichEditCommand.Strikethrough, BFontWeight.Normal);
        StandardButton clearButton = ToolbarCommand("Clear", "Clear formatting", RichEditCommand.ClearFormatting, WriterIcons.ClearFormatting);
        StandardToggleButton leftButton = ToolbarToggle("Left", "Align left (Ctrl+L)", RichEditCommand.AlignLeft, BFontWeight.Normal, icon: WriterIcons.AlignLeft);
        StandardToggleButton centerButton = ToolbarToggle("Center", "Align center (Ctrl+E)", RichEditCommand.AlignCenter, BFontWeight.Normal, icon: WriterIcons.AlignCenter);
        StandardToggleButton rightButton = ToolbarToggle("Right", "Align right (Ctrl+R)", RichEditCommand.AlignRight, BFontWeight.Normal, icon: WriterIcons.AlignRight);
        StandardToggleButton justifyButton = ToolbarToggle("Justify", "Justify (Ctrl+J)", RichEditCommand.AlignJustify, BFontWeight.Normal, icon: WriterIcons.AlignJustify);
        StandardToggleButton bulletsButton = ToolbarToggle("Bullets", "Bullet list", RichEditCommand.BulletList, BFontWeight.Normal, icon: WriterIcons.BulletList);
        StandardToggleButton numberedButton = ToolbarToggle("Numbered", "Numbered list", RichEditCommand.NumberedList, BFontWeight.Normal, icon: WriterIcons.NumberedList);
        StandardButton indentButton = ToolbarCommand("Indent", "Indent", RichEditCommand.Indent, WriterIcons.Indent);
        StandardButton outdentButton = ToolbarCommand("Outdent", "Outdent", RichEditCommand.Outdent, WriterIcons.Outdent);

        // Ahead of the formatting groups rather than after them: this toolbar is
        // already wider than the window it opens in, and a control at the end is
        // one the user never sees. The compact toolbar drops the group entirely
        // and reaches zoom through the View menu, as it does alignment and lists.
        StandardButton zoomOutButton = ToolbarText("-", "Zoom out (Ctrl+minus)", IconButtonExtent, () => StepZoom(WriterZoomStep.Out));
        StandardButton zoomInButton = ToolbarText("+", "Zoom in (Ctrl+plus)", IconButtonExtent, () => StepZoom(WriterZoomStep.In));
        StandardComboBox zoomCombo = CreateZoomCombo();
        _zoomCombo = zoomCombo;

        toolbar.AddChild(newButton);
        toolbar.AddChild(openButton);
        toolbar.AddChild(saveButton);
        if (!_compactMode)
            toolbar.AddChild(saveAsButton);
        toolbar.AddChild(undoButton);
        if (!_compactMode)
        {
            toolbar.AddChild(redoButton);
            toolbar.AddChild(zoomOutButton);
            toolbar.AddChild(zoomCombo);
            toolbar.AddChild(zoomInButton);
            toolbar.AddChild(fontButton);
        }
        toolbar.AddChild(boldButton);
        toolbar.AddChild(italicButton);
        toolbar.AddChild(underlineButton);
        if (!_compactMode)
        {
            toolbar.AddChild(strikeButton);
            toolbar.AddChild(clearButton);
            toolbar.AddChild(leftButton);
            toolbar.AddChild(centerButton);
            toolbar.AddChild(rightButton);
            toolbar.AddChild(justifyButton);
            toolbar.AddChild(bulletsButton);
            toolbar.AddChild(numberedButton);
            toolbar.AddChild(indentButton);
            toolbar.AddChild(outdentButton);
        }

        // File | history | zoom | character | paragraph | lists | indent. Every break is a gap and
        // none is a rule: seven drawn lines across the bar would be the heaviest thing in the
        // window, and the space alone is enough to find the groups by. The guards below have to
        // match the AddChild guards exactly - SetBreakBefore throws for a child the bar does not
        // have, and on the compact bar only Android would ever find out.
        toolbar.SetBreakBefore(undoButton, UiToolbarBreak.Gap);
        if (_compactMode)
        {
            // The compact bar has no Font button, so the character group has to open at B.
            toolbar.SetBreakBefore(boldButton, UiToolbarBreak.Gap);
        }
        else
        {
            toolbar.SetBreakBefore(zoomOutButton, UiToolbarBreak.Gap);
            toolbar.SetBreakBefore(fontButton, UiToolbarBreak.Gap);
            toolbar.SetBreakBefore(leftButton, UiToolbarBreak.Gap);
            toolbar.SetBreakBefore(bulletsButton, UiToolbarBreak.Gap);
            toolbar.SetBreakBefore(indentButton, UiToolbarBreak.Gap);
        }

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

    private StandardButton CreateToolbarButton(
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

    private void NewDocument()
    {
        _currentDocumentPath = null;
        ReplaceDocument(() => _editor.SetPlainText(string.Empty));
        SetDocumentName(UntitledDocumentName, modified: false);
        _lastAction = "New document";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    private void ShowOpenDialog()
    {
        if (_requestOpenDocument is not null)
        {
            _requestOpenDocument();
            _lastAction = "Open document";
            RefreshUi();
            return;
        }

        var dialog = new StandardFileDialog
        {
            Mode = UiFileDialogMode.Open,
            CurrentDirectory = GetDialogDirectory(),
            FileName = _currentDocumentPath is null ? string.Empty : Path.GetFileName(_currentDocumentPath),
            PreferredSize = FileDialogPreferredSize,
        };
        dialog.SetFileTypeFilters(_openDocumentFileFilters);
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted && !string.IsNullOrWhiteSpace(e.Result.Value))
                OpenDocument(e.Result.Value);
        };

        dialog.ShowOpenModal(_rootWindow, GetDialogPlacement());
        _lastAction = "Open document";
        RefreshUi();
    }

    private void SaveDocument()
    {
        if (_requestSaveDocument is not null)
        {
            _requestSaveDocument(SuggestedDocumentName(), false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentDocumentPath))
        {
            ShowSaveDialog();
            return;
        }

        SaveDocumentAs(_currentDocumentPath);
    }

    private void ShowSaveDialog()
    {
        if (_requestSaveDocument is not null)
        {
            _requestSaveDocument(SuggestedDocumentName(), true);
            _lastAction = "Save document as";
            RefreshUi();
            return;
        }

        string fileName = _currentDocumentPath is null
            ? "Untitled"
            : Path.GetFileNameWithoutExtension(_currentDocumentPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "Untitled";

        var dialog = new StandardFileDialog
        {
            Mode = UiFileDialogMode.Save,
            CurrentDirectory = GetDialogDirectory(),
            FileName = fileName,
            PreferredSize = FileDialogPreferredSize,
        };
        dialog.SetFileTypeFilters(
            _saveDocumentFileFilters,
            GetFileTypeFilterIndex(_saveDocumentFileFilters, _currentDocumentPath));
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted && !string.IsNullOrWhiteSpace(e.Result.Value))
                SaveDocumentAs(e.Result.Value);
        };

        dialog.ShowSaveModal(_rootWindow, GetDialogPlacement());
        _lastAction = "Save document as";
        RefreshUi();
    }

    private void OpenDocument(string path)
    {
        try
        {
            string fullPath = ResolveDocumentPath(path);

            // Streamed rather than File.ReadAllBytes: the read's own limits decide
            // how much of a file is allowed into memory, so an oversized document
            // is refused instead of being materialized and then measured.
            using FileStream file = File.OpenRead(fullPath);
            using DocumentInput input = DocumentInput.FromStream(file);
            DocumentCodecSelection selection = ReadDocument(fullPath, input);
            if (!MayReplaceDocument(selection.Result))
            {
                _problem = _lastAction = DescribeRefusedOpen(Path.GetFileName(fullPath), selection);
                RefreshUi();
                return;
            }

            _currentDocumentPath = fullPath;
            _lastDirectory = Path.GetDirectoryName(fullPath) ?? _lastDirectory;
            SetDocumentName(Path.GetFileName(fullPath), modified: false);
            _lastAction = DescribeOpen(Path.GetFileName(fullPath), selection.Result);
            ReplaceDocument(() =>
            {
                _editor.Document = selection.Result.Document;
                _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
            });
            _session.SetFocus(_editor);
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _problem = _lastAction = "Open failed: " + ex.Message;
        }

        RefreshUi();
    }

    private void SaveDocumentAs(string path)
    {
        try
        {
            string fullPath = ResolveDocumentPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            DocumentWriteResult result = WriteDocument(fullPath, _editor.Document, out byte[] bytes);
            File.WriteAllBytes(fullPath, bytes);
            _currentDocumentPath = fullPath;
            _lastDirectory = Path.GetDirectoryName(fullPath) ?? _lastDirectory;
            SetDocumentName(Path.GetFileName(fullPath), modified: false);
            _lastAction = result.Diagnostics.Count == 0
                ? "Saved " + Path.GetFileName(fullPath)
                : "Saved " + Path.GetFileName(fullPath) + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
            if (result.Diagnostics.Count > 0)
                _problem = _lastAction;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _problem = _lastAction = "Save failed: " + ex.Message;
        }

        _session.SetFocus(_editor);
        RefreshUi();
    }

    private void ShowInsertPictureDialog()
    {
        var dialog = new StandardFileDialog
        {
            Mode = UiFileDialogMode.Open,
            CurrentDirectory = GetDialogDirectory(),
            PreferredSize = FileDialogPreferredSize,
        };
        dialog.SetFileTypeFilters(OpenPictureFileFilters);
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted && !string.IsNullOrWhiteSpace(e.Result.Value))
                InsertPicture(e.Result.Value);
        };

        dialog.ShowOpenModal(_rootWindow, GetDialogPlacement());
        _lastAction = "Insert picture";
        RefreshUi();
    }

    /// <summary>
    /// Reads an image file and inserts it at the caret as a single inline image
    /// character. The size comes from the decoded pixels, scaled down so a photo
    /// straight from a camera does not arrive several thousand units wide.
    /// </summary>
    internal bool InsertPicture(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            byte[] bytes = File.ReadAllBytes(fullPath);
            string? contentType = WriterImageFormats.ContentTypeFor(fullPath, bytes);
            if (contentType is null)
            {
                _lastAction = "Insert picture failed: unsupported image format";
                RefreshUi();
                return false;
            }

            _lastDirectory = Path.GetDirectoryName(fullPath) ?? _lastDirectory;
            BSize size = WriterImageFormats.MeasureDisplaySize(bytes, MaxInsertedPictureWidth);
            // A picture the user picked from their own disk is theirs, and
            // admitting it here is what lets a later save write it out.
            InlineImage image = _resources.AdmitImage(
                new InlineImage(
                    bytes,
                    contentType,
                    size.Width,
                    size.Height,
                    altText: Path.GetFileNameWithoutExtension(fullPath),
                    name: Path.GetFileNameWithoutExtension(fullPath)),
                DocumentResourceProvenance.CallerSupplied,
                DocumentResourceDisposition.Embedded);

            // The rich-paste primitive is the only insertion path that carries a
            // style of its own; InsertText takes the caret's style, which has no
            // image on it.
            bool inserted = _editor.InsertDocument(RichTextDocument.FromParagraphs(
            [
                RichTextParagraph.Create(
                    InlineImage.PlaceholderText,
                    _editor.CaretInlineStyle with { Image = image }),
            ]));
            _lastAction = inserted
                ? "Inserted picture " + Path.GetFileName(fullPath)
                : "Insert picture unavailable";
            _session.SetFocus(_editor);
            RefreshUi();
            return inserted;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _lastAction = "Insert picture failed: " + ex.Message;
            RefreshUi();
            return false;
        }
    }

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

    /// <summary>
    /// Shows what the last read reported. The status bar can say a document
    /// opened with notes; this is where the user finds out what they were.
    /// </summary>
    private void ShowDocumentNotes()
    {
        if (_lastReadDiagnostics.Count == 0)
        {
            _lastAction = "No notes for this document";
            RefreshUi();
            return;
        }

        StandardDialog dialog = WriterNotes.CreateDialog(_lastReadFileName, _lastReadDiagnostics);
        dialog.ShowModal(_rootWindow, GetFontDialogPlacement());
        _lastAction = "Notes for " + _lastReadFileName;
        RefreshUi();
    }

    private void ShowAbout()
    {
        _lastAction = "Broiler Writer preview: Broiler.UI window, menu, and StandardRichEdit";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    private void SeedDocument()
    {
        ReplaceDocument(() => _editor.SetPlainText(
            "Broiler Writer\n" +
            "This preview is a Broiler.UI window with a Broiler-rendered menu and StandardRichEdit document surface.\n" +
            "Use the Edit and Format menus, or keyboard shortcuts such as Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+Z, and Ctrl+Y. The editor is drawn through Broiler.Graphics rather than a native RICHEDIT control."));

        RichTextPosition start = _editor.Document.Start;
        RichTextPosition end = _editor.Document.ParagraphEnd(start);
        _editor.Selection = new RichTextRange(start, end);
        _editor.ExecuteCommand(RichEditCommand.Bold);
        _editor.Selection = RichTextRange.Caret(_editor.Document.End);
        _lastAction = "Ready";
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

    private BRect GetDialogPlacement()
    {
        BSize viewport = _host.ViewportSize;
        double width = FileDialogPreferredSize.Width;
        double height = FileDialogPreferredSize.Height;
        double x = Math.Max(12, (viewport.Width - width) / 2);
        double y = Math.Max(42, (viewport.Height - height) / 2);
        return new BRect(x, y, Math.Min(width, Math.Max(280, viewport.Width - 24)), Math.Min(height, Math.Max(180, viewport.Height - 64)));
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

    private string GetDialogDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_currentDocumentPath))
        {
            string? directory = Path.GetDirectoryName(_currentDocumentPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return directory;
        }

        return Directory.Exists(_lastDirectory) ? _lastDirectory : Environment.CurrentDirectory;
    }

    private string SuggestedDocumentName()
    {
        string name = Path.GetFileName(_currentDocumentPath ?? string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "Untitled.rtf" : name;
    }

    private static int GetFileTypeFilterIndex(IReadOnlyList<UiFileDialogFilter> filters, string? path)
    {
        string extension = Path.GetExtension(path ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
            return 0;

        for (int i = 0; i < filters.Count; i++)
        {
            if (FilterIncludesExtension(filters[i], extension))
                return i;
        }

        return 0;
    }

    private static bool FilterIncludesExtension(UiFileDialogFilter filter, string extension)
    {
        foreach (string pattern in filter.Pattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (StringComparer.Ordinal.Equals(pattern, "*") || StringComparer.Ordinal.Equals(pattern, "*.*"))
                return true;

            if (pattern.StartsWith("*.", StringComparison.Ordinal) &&
                string.Equals(pattern[1..], extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string ResolveDocumentPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return Path.HasExtension(fullPath) ? fullPath : fullPath + _documentFormats.DefaultExtension;
    }

    /// <summary>
    /// Selects a codec for <paramref name="input"/> and reads it through the one
    /// authoritative catalog path, so the bytes the probe saw are the bytes the
    /// codec reads and a source is never buffered twice to make that true.
    /// </summary>
    private DocumentCodecSelection ReadDocument(string fullPath, DocumentInput input)
    {
        // The Writer opens files its user chose, so reading their pictures and
        // saving them again is what was asked for. The policy is stated here
        // rather than inherited, and a host with a different relationship to its
        // input would state a different one.
        DocumentCodecSelection selection = _documentCatalog.SelectAndRead(
            input,
            new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments),
            new DocumentSourceHints(fileName: fullPath));

        // The decisions this read made travel with the document until it is
        // replaced, so a picture that came out of the file can go back into one.
        _resources = DocumentConversionContextBuilder.Continuing(
            selection.Result.Resources,
            DocumentResourcePolicy.AllowOwnDocuments);

        _lastReadDiagnostics = selection.Result.Diagnostics;
        _lastReadFileName = Path.GetFileName(fullPath);
        LogReadDiagnostics(selection.Codec?.Name ?? "no codec", fullPath, selection.Result);
        return selection;
    }

    /// <summary>
    /// Whether a read may replace what is in the editor.
    /// </summary>
    /// <remarks>
    /// Two reads must never land in the editor. A <see cref="DocumentResultStatus.Rejected"/>
    /// one carries a placeholder rather than content, and committing it would
    /// throw away the open document in exchange for nothing. So would a read that
    /// recovered no text at all and knows it is incomplete — a scanned PDF with no
    /// text layer is the shape that produces it — where "empty" is a report about
    /// the reader, not about the file. A read that produced text is committed even
    /// when it is <see cref="DocumentResultStatus.Partial"/>; the status line says
    /// so rather than passing it off as a clean open.
    /// </remarks>
    internal static bool MayReplaceDocument(DocumentReadResult result) =>
        result.IsUsable &&
        (result.Document.PlainText.Length > 0 || result.Status == DocumentResultStatus.Success);

    /// <summary>Why a read was refused, for the status bar.</summary>
    internal static string DescribeRefusedOpen(string fileName, DocumentCodecSelection selection)
    {
        string reason = FirstProblem(selection.Result) ?? (selection.Codec is null
            ? "no registered format recognized it"
            : "the " + selection.Codec.Name + " reader recovered no content from it");

        return "Could not open " + fileName + ": " + reason.TrimEnd('.') + ". The open document is unchanged.";
    }

    private static string? FirstProblem(DocumentReadResult result)
    {
        foreach (DocumentDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity != DocumentDiagnosticSeverity.Info)
                return diagnostic.Message;
        }

        return null;
    }

    /// <summary>
    /// Writes the read diagnostics to stderr when
    /// <c>BROILER_WRITER_DOCUMENT_LOG</c> is set. Off by default: the status bar
    /// carries the summary, and this is the switch for chasing one bad file.
    /// </summary>
    private static void LogReadDiagnostics(string codecName, string fullPath, DocumentReadResult result)
    {
        if (!IsDocumentLoggingEnabled)
            return;

        Console.Error.WriteLine(
            "[writer] read " + Path.GetFileName(fullPath) + " via " + codecName + ": " +
            result.Document.ParagraphCount.ToString(CultureInfo.InvariantCulture) + " paragraph(s), " +
            result.Document.PlainText.Length.ToString(CultureInfo.InvariantCulture) + " character(s), " +
            result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " diagnostic(s)");
        foreach (DocumentDiagnostic diagnostic in result.Diagnostics)
            Console.Error.WriteLine("[writer]   " + diagnostic);
    }

    private static bool IsDocumentLoggingEnabled =>
        Environment.GetEnvironmentVariable("BROILER_WRITER_DOCUMENT_LOG") is "1" or "true" or "TRUE" or "on";

    /// <summary>
    /// Builds the status-bar text for a completed open. Info-level notes are
    /// routine and stay out of the count; a read that produced no text at all is
    /// called out, because "nothing happened" is the one outcome the user cannot
    /// otherwise tell apart from an empty file.
    /// </summary>
    internal static string DescribeOpen(string fileName, DocumentReadResult result)
    {
        string text = "Opened " + fileName;
        if (result.Document.PlainText.Length == 0)
            text += " (no readable content)";

        int notes = 0;
        foreach (DocumentDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity != DocumentDiagnosticSeverity.Info)
                notes++;
        }

        if (notes > 0)
            text += " with " + notes.ToString(CultureInfo.InvariantCulture) + " note(s)";

        // A partial read is a different outcome from a clean one, not a clean one
        // with footnotes, so it is never reported as undifferentiated success.
        return result.Status == DocumentResultStatus.Partial
            ? text + "; parts of it were skipped or approximated"
            : text;
    }

    /// <summary>
    /// Writes through the codec the host registered for the target extension.
    /// A format registered for opening only has no entry here, so a save cannot
    /// reach a writer the host did not enable — not through the Save dialog,
    /// whose filters come from the same set, and not through a typed filename
    /// either.
    /// </summary>
    private DocumentWriteResult WriteDocument(
        string fullPath,
        RichTextDocument document,
        out byte[] bytes)
    {
        string extension = Path.GetExtension(fullPath);
        WriterDocumentFormat format = _documentFormats.FindForSave(extension) ??
            throw new NotSupportedException(
                "Unsupported save format '" + extension + "'. Use " +
                _documentFormats.DescribeSaveExtensions() + ".");

        using var stream = new MemoryStream();
        DocumentWriteResult result = format.Codec.Write(
            document,
            stream,
            new DocumentWriteOptions(resources: _resources.Build()));
        bytes = stream.ToArray();
        return result;
    }

    private static bool IsFileOperationException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

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

    /// <summary>
    /// Remembers whether Ctrl is down. A wheel notch is where that has to be
    /// known and not every head fills a pointer event's modifiers in, but every
    /// head delivers the key events that raise and drop the flag.
    /// </summary>
    private void TrackModifiers(UiInputEvent input)
    {
        if (input.Kind == UiInputEventKind.KeyboardKey)
            _isControlHeld = (input.KeyModifiers & KeyboardModifierState.Control) != KeyboardModifierState.None;
    }

    /// <summary>
    /// The zoom gestures the editor does not own: Ctrl with plus, minus or zero,
    /// and Ctrl with the wheel. They are answered before the session sees them,
    /// or Ctrl and the wheel would scroll the document it was asked to resize.
    /// </summary>
    private bool HandleZoomShortcut(UiInputEvent input)
    {
        WriterZoomStep step = input.Kind switch
        {
            UiInputEventKind.KeyboardKey => WriterZoom.StepFor(
                input.KeyName,
                input.NativeKeyCode,
                input.KeyModifiers,
                input.KeyTransition == KeyboardKeyTransition.Down),
            UiInputEventKind.PointerWheel => WriterZoom.StepForWheel(
                _isControlHeld || (input.KeyModifiers & KeyboardModifierState.Control) != KeyboardModifierState.None,
                input.WheelDeltaNotches),
            _ => WriterZoomStep.None,
        };

        if (step == WriterZoomStep.None)
            return false;

        StepZoom(step);
        return true;
    }

    private bool HandleFormattingCodesShortcut(UiInputEvent input)
    {
        if (input.Kind != UiInputEventKind.KeyboardKey)
            return false;

        bool isDown = input.KeyTransition == KeyboardKeyTransition.Down;
        string keyName = input.KeyName ?? string.Empty;
        if (WriterFormatCodesShortcut.IsToggle(keyName, input.KeyModifiers, isDown, isRepeat: false))
        {
            ToggleFormattingCodes();
            return true;
        }

        if (!WriterFormatCodesShortcut.IsFocusCycle(keyName, input.KeyModifiers, isDown, isRepeat: false))
            return false;

        CycleFormattingCodesFocus(WriterFormatCodesShortcut.IsReverseFocusCycle(input.KeyModifiers));
        return true;
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
        _host.RequestInvalidate();
    }

    /// <summary>
    /// What the status line says on the left: what is in the document. These are facts about the
    /// document that do not change while you look at them, which is what makes them worth a fixed
    /// place to look.
    /// </summary>
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
    /// What it says on the right: the state you are working in.
    /// </summary>
    /// <remarks>
    /// The line used to carry seven pipe-separated segments, three of which restated something
    /// already on screen - whether the Formatting Codes pane was shown, which the pane itself
    /// answers by being there, and the last command run, which was often just "Open document" and
    /// is not a status at all. What is left is the formatting under the caret and the zoom, both
    /// of which are things you cannot otherwise see. A refused open or a failed save still needs
    /// saying, so an action that reports a problem takes the slot until the next one replaces it.
    /// </remarks>
    private string BuildStatusState()
    {
        string state = CurrentStyleText() + " · " + WriterZoom.Describe(_zoom);
        return _problem is null ? state : _problem + " · " + state;
    }

    /// <summary>
    /// Names the document, and says whether it has unsaved changes. Everything the user sees of a
    /// document's identity comes through here.
    /// </summary>
    private void SetDocumentName(string? name, bool modified)
    {
        // Naming a document means an open or a save just succeeded, which retires the last problem.
        _problem = null;
        _documentName = string.IsNullOrWhiteSpace(name) ? UntitledDocumentName : name;
        _isModified = modified;
        UpdateWindowTitle();
    }

    /// <summary>
    /// Records that the document has been edited since it was last opened or saved.
    /// </summary>
    private void MarkModified()
    {
        // A load raises DocumentChanged exactly as a keystroke does, so without this a file would
        // be modified the instant it finished opening.
        if (_replacingDocument > 0 || _isModified)
            return;

        _isModified = true;
        UpdateWindowTitle();
    }

    /// <summary>
    /// Replaces the document without that counting as an edit.
    /// </summary>
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

    /// <summary>
    /// The window caption: the document, then the application. A leading dot marks unsaved
    /// changes - quieter than an asterisk, and it reads as a mark rather than as punctuation that
    /// got into the file name.
    /// </summary>
    internal string WindowTitle =>
        (_isModified ? "• " : string.Empty) + _documentName + " — " + ApplicationName;

    private void UpdateWindowTitle()
    {
        string title = WindowTitle;
        _rootWindow.Title = title;
        _setWindowTitle?.Invoke(title);
    }

    /// <summary>
    /// The application's mark, at the size a window manager asked for. Drawn from the same
    /// geometry as the toolbar icons, in the Writer's own colours.
    /// </summary>
    internal static BPixelBuffer CreateAppIcon(int size) =>
        WriterIcons.RenderAppIcon(size, WriterPalette.Page, WriterPalette.Accent, WriterPalette.Title);

    /// <summary>The name of the open document, with no decoration.</summary>
    internal string DocumentName => _documentName;

    /// <summary>Whether the document has been edited since it was last opened or saved.</summary>
    internal bool IsModified => _isModified;

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
            RichEditCommand.AlignJustify => "Justify",
            RichEditCommand.BulletList => "Bullet list",
            RichEditCommand.NumberedList => "Numbered list",
            RichEditCommand.SetFont => "Font",
            _ => command.ToString(),
        };

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
            _toolbar.Measure(new BSize(width, _toolbar.PreferredSize.Height));
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
            _menu.MenuBarHeight + _toolbar.PreferredSize.Height + WorkspaceTop + StatusGap + StatusHeight + (Margin * 2);

        protected override void ArrangeCore(BRect finalRect)
        {
            double toolbarHeight = _toolbar.PreferredSize.Height;
            _menu.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, _menu.MenuBarHeight));
            _toolbar.Arrange(new BRect(finalRect.Left, finalRect.Top + _menu.MenuBarHeight, finalRect.Width, toolbarHeight));

            double margin = finalRect.Width < 600 ? 12 : Margin;
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
            context.RenderList.FillRect(new BRect(Bounds.Left, Bounds.Top + _menu.MenuBarHeight + _toolbar.PreferredSize.Height, Bounds.Width, 1), WriterPalette.MenuRule);
            base.RenderCore(context);
        }
    }
}
