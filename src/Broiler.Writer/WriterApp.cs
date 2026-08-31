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
using Broiler.UI.Dialog;
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
    private readonly StandardLabel _title;
    private readonly StandardLabel _status;
    private readonly WriterDocumentFormats _documentFormats;
    private readonly DocumentCodecCatalog _documentCatalog;
    private readonly UiFileDialogFilter[] _openDocumentFileFilters;
    private readonly UiFileDialogFilter[] _saveDocumentFileFilters;
    private readonly List<(UiMenuItem Item, RichEditCommand Command)> _richEditMenuItems = [];
    private readonly List<(StandardButton Button, RichEditCommand Command)> _toolbarActionButtons = [];
    private readonly List<(StandardToggleButton Button, RichEditCommand Command)> _toolbarToggleButtons = [];
    private UiMenuItem? _fontMenuItem;
    private UiMenuItem? _formatCodesMenuItem;
    private StandardButton? _fontToolbarButton;
    private string? _currentDocumentPath;
    private string _lastDirectory = Environment.CurrentDirectory;
    private string _lastAction = "Ready";
    private IReadOnlyList<DocumentDiagnostic> _lastReadDiagnostics = Array.Empty<DocumentDiagnostic>();

    private static readonly BSize FileDialogPreferredSize = new(740, 430);
    private static readonly BSize FontDialogPreferredSize = new(520, 322);
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
        WriterDocumentFormats? documentFormats = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));
        _requestOpenDocument = requestOpenDocument;
        _requestSaveDocument = requestSaveDocument;
        _compactMode = compactMode;
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
        if (compactMode)
            _content.IsFormatCodesVisible = false;
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

    public BRenderList RenderFrame() => _session.RenderFrame();

    public void Dispatch(UiInputEvent input)
    {
        if (HandleFormattingCodesShortcut(input))
        {
            _host.RequestInvalidate();
            return;
        }

        if (_session.DispatchInput(input))
            _host.RequestInvalidate();
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
                _lastAction = DescribeRefusedOpen(Path.GetFileName(displayName), selection);
                RefreshUi();
                return false;
            }

            _currentDocumentPath = displayName;
            _title.Text = Path.GetFileName(displayName);
            _lastAction = DescribeOpen(Path.GetFileName(displayName), selection.Result);
            _editor.Document = selection.Result.Document;
            _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
            _session.SetFocus(_editor);
            RefreshUi();
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _lastAction = "Open failed: " + ex.Message;
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
                _title.Text = Path.GetFileName(displayName);
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
            _lastAction = "Save failed: " + ex.Message;
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

        var help = new UiMenuItem("help", "Help") { AccessKey = 'H' };
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

    private StandardToolbar CreateToolbar()
    {
        var toolbar = new StandardToolbar
        {
            Title = "Document toolbar",
            PreferredSize = new BSize(0, _compactMode ? 50 : 42),
            Orientation = UiToolbarOrientation.Horizontal,
            Padding = 5,
            Spacing = 4,
            Background = WriterPalette.ToolbarSurface,
            BorderColor = WriterPalette.MenuRule,
            SeparatorColor = WriterPalette.MenuRule,
            CornerRadius = 0,
        };
        StandardButton newButton = ToolbarAction("New", 50, NewDocument);
        StandardButton openButton = ToolbarAction("Open", 56, ShowOpenDialog);
        StandardButton saveButton = ToolbarAction("Save", 54, SaveDocument);
        StandardButton saveAsButton = ToolbarAction("Save As", 62, ShowSaveDialog);
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
        StandardToggleButton justifyButton = ToolbarToggle("Justify", RichEditCommand.AlignJustify, 56, BFontWeight.Normal);
        StandardToggleButton bulletsButton = ToolbarToggle("Bullets", RichEditCommand.BulletList, 58, BFontWeight.Normal);
        StandardToggleButton numberedButton = ToolbarToggle("Numbered", RichEditCommand.NumberedList, 70, BFontWeight.Normal);
        StandardButton indentButton = ToolbarCommand("Indent", RichEditCommand.Indent, 58);
        StandardButton outdentButton = ToolbarCommand("Outdent", RichEditCommand.Outdent, 64);

        toolbar.AddChild(newButton);
        toolbar.AddChild(openButton);
        toolbar.AddChild(saveButton);
        if (!_compactMode)
            toolbar.AddChild(saveAsButton);
        toolbar.AddChild(undoButton);
        if (!_compactMode)
        {
            toolbar.AddChild(redoButton);
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

        toolbar.SetSeparatorBefore(undoButton, true);
        if (!_compactMode)
        {
            toolbar.SetSeparatorBefore(fontButton, true);
            toolbar.SetSeparatorBefore(leftButton, true);
            toolbar.SetSeparatorBefore(indentButton, true);
        }

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
            PreferredSize = new BSize(width, _compactMode ? 40 : 30),
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

    private StandardButton CreateToolbarButton(string text, double width) =>
        new()
        {
            Text = text,
            PreferredSize = new BSize(width, _compactMode ? 40 : 30),
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

    private void NewDocument()
    {
        _currentDocumentPath = null;
        _editor.SetPlainText(string.Empty);
        _title.Text = "Untitled document";
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
                _lastAction = DescribeRefusedOpen(Path.GetFileName(fullPath), selection);
                RefreshUi();
                return;
            }

            _currentDocumentPath = fullPath;
            _lastDirectory = Path.GetDirectoryName(fullPath) ?? _lastDirectory;
            _title.Text = Path.GetFileName(fullPath);
            _lastAction = DescribeOpen(Path.GetFileName(fullPath), selection.Result);
            _editor.Document = selection.Result.Document;
            _editor.Selection = RichTextRange.Caret(_editor.Document.Start);
            _session.SetFocus(_editor);
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _lastAction = "Open failed: " + ex.Message;
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
            _title.Text = Path.GetFileName(fullPath);
            _lastAction = result.Diagnostics.Count == 0
                ? "Saved " + Path.GetFileName(fullPath)
                : "Saved " + Path.GetFileName(fullPath) + " with " + result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture) + " note(s)";
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            _lastAction = "Save failed: " + ex.Message;
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
            var image = new InlineImage(
                bytes,
                contentType,
                size.Width,
                size.Height,
                altText: Path.GetFileNameWithoutExtension(fullPath),
                name: Path.GetFileNameWithoutExtension(fullPath));

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

    private void ShowAbout()
    {
        _lastAction = "Broiler Writer preview: Broiler.UI window, menu, and StandardRichEdit";
        _session.SetFocus(_editor);
        RefreshUi();
    }

    private void SeedDocument()
    {
        _editor.SetPlainText(
            "Broiler Writer\n" +
            "This preview is a Broiler.UI window with a Broiler-rendered menu and StandardRichEdit document surface.\n" +
            "Use the Edit and Format menus, or keyboard shortcuts such as Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+Z, and Ctrl+Y. The editor is drawn through Broiler.Graphics rather than a native RICHEDIT control.");

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
        DocumentCodecSelection selection = _documentCatalog.SelectAndRead(
            input,
            hints: new DocumentSourceHints(fileName: fullPath));

        _lastReadDiagnostics = selection.Result.Diagnostics;
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
        DocumentWriteResult result = format.Codec.Write(document, stream);
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
        _status.Text = BuildStatus();
        _host.RequestInvalidate();
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
            RichEditCommand.AlignJustify => "Justify",
            RichEditCommand.BulletList => "Bullet list",
            RichEditCommand.NumberedList => "Numbered list",
            RichEditCommand.SetFont => "Font",
            _ => command.ToString(),
        };

    private sealed class WriterContent : UiElement
    {
        private const double Margin = 24;
        private const double TitleTop = 18;
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
            double width = double.IsInfinity(availableSize.Width) ? MinWidth : Math.Max(0, availableSize.Width);
            double height = double.IsInfinity(availableSize.Height) ? MinHeight : Math.Max(0, availableSize.Height);
            double contentWidth = Math.Max(0, width - (Margin * 2));

            _menu.Measure(new BSize(width, _menu.MenuBarHeight));
            _toolbar.Measure(new BSize(width, _toolbar.PreferredSize.Height));
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
            double toolbarHeight = _toolbar.PreferredSize.Height;
            _menu.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, _menu.MenuBarHeight));
            _toolbar.Arrange(new BRect(finalRect.Left, finalRect.Top + _menu.MenuBarHeight, finalRect.Width, toolbarHeight));

            double margin = finalRect.Width < 600 ? 12 : Margin;
            double x = finalRect.Left + margin;
            double y = finalRect.Top + _menu.MenuBarHeight + toolbarHeight + TitleTop;
            double width = Math.Max(0, finalRect.Width - (margin * 2));

            _title.Arrange(new BRect(x, y, width, _title.DesiredSize.Height));
            y += _title.DesiredSize.Height + 14;

            double statusTop = finalRect.Bottom - margin - StatusHeight;
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
            context.RenderList.FillRect(new BRect(Bounds.Left, Bounds.Top + _menu.MenuBarHeight + _toolbar.PreferredSize.Height, Bounds.Width, 1), WriterPalette.MenuRule);
            base.RenderCore(context);
        }
    }
}
