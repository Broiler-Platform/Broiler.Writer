using System;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.PrintPreview.Standard;
using Broiler.UI.PrintPreview;
using Broiler.UI.RichEdit;
using Broiler.Graphics.Color;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Text;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.Input.Text;
using Broiler.Input.Touch;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.Menu;
using Broiler.UI.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterPageLayout
{
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
    internal static LayoutVisualTextMeasurer? VisualTextMeasurer { get; set; }
#endif

    internal static DocumentPaginator CreatePaginator(string fontFamily, bool editorGeometry = false, bool drawNonPrintingShapes = false) =>
        new(DocumentPaginator.CreateDefaultSettings() with
        {
            DefaultFontFamily = fontFamily,
            DrawNonPrintingShapes = drawNonPrintingShapes,
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
            IncludeSourceGeometry = editorGeometry,
            VisualTextMeasurer = editorGeometry ? VisualTextMeasurer : null,
#endif
        });

    internal static PaginatedDocument Paginate(RichTextDocument document, string fontFamily) =>
        CreatePaginator(fontFamily, editorGeometry: true).Paginate(document, DocumentPaginator.PreferredSetup(document));

    internal static void AddPaginationMenu(StandardCommandDispatcher dispatcher, UiMenuItem view, PageLayoutContent editor, Action refresh)
    {
        var item = new UiMenuItem("pagination", "Pagination")
        {
            CommandName = "view.pagination",
            IsCheckable = true,
            IsChecked = editor.IsPaginationEnabled,
        };
        view.Children.Add(item);
        dispatcher.Add(new StandardCommand("view.pagination", () =>
        {
            editor.SetPaginationEnabled(!editor.IsPaginationEnabled);
            item.IsChecked = editor.IsPaginationEnabled;
            refresh();
        }));
    }

    internal static PageLayoutContent CreateEditor(StandardRichEdit editor) =>
        new(Paginate(editor.Document, editor.Font.FamilyName), () => { }, _ => { },
            editor, editor.Font.FamilyName, null, embedded: true);

    internal static void Show(UiWindow owner, BSize viewport, UiRichEdit editor, string fontFamily,
        Action<string>? reportPageText = null, bool editGraphics = false)
    {
        var dialog = StandardPageLayoutDialog.Create(canClose: !OperatingSystem.IsBrowser());
        var content = new PageLayoutContent(Paginate(editor.Document, fontFamily), () => dialog.Accept(),
            selection =>
            {
                if (dialog.Accept())
                {
                    editor.Selection = selection;
                    if (editor is StandardRichEdit rich)
                        rich.RevealCaret();
                    editor.Session?.SetFocus(editor);
                }
            }, editor, fontFamily, reportPageText);
        dialog.AddChild(content);
        dialog.ResultCompleted += (_, _) => reportPageText?.Invoke("");
        try
        {
            StandardPageLayoutDialog.Show(dialog, owner, viewport);
            content.FocusPreview();
            content.InitializeGraphics(owner, viewport);
            if (editGraphics) content.BeginGraphics();
        }
        catch
        {
            dialog.Dispose();
            throw;
        }
    }

    internal sealed partial class PageLayoutContent : UiElement, IUiTextEditor
    {
        private PaginatedDocument _pages;
        private readonly StandardPrintPreview _preview;
        private readonly StandardButton _previous;
        private readonly StandardButton _next;
        private readonly StandardButton _close;
        private readonly StandardButton? _go;
        private int? _bodyParagraph;
        private WriterPageOccurrence? _bodyLine;
        private RichTextRange? _pageSelection;
        private readonly StandardButton _edit;
        private readonly StandardButton _type;
        private readonly UiRichEdit _body;
        private readonly string _fontFamily;
        private readonly Action<string>? _reportPageText;
        private RichTextDocument _snapshot;
        private WriterPageLineIndex _pageGeometry;
        // The same source offset can appear at the end of one line and the start of another.
        private (RichTextPosition Focus, RichEditLineId Line, double? InlineCoordinate)? _caretLine;
        private StandardRichEdit? _draft;
        private int _draftPage;
        private bool _revealDraftCaret;
        private bool _shiftDown;
        private bool _typing;
        private InputEventHeader? _compositionHeader;
        private readonly bool _embedded;
        private readonly EngineHost? _engineHost;
        private bool _syncing;
        private double _scrollY;
        private double _scrollX;
        private readonly StandardLabel? _pageCaption;
        private readonly BRenderList _previewCommands = new();
        internal UiElement? StatusNavigation { get; }
        private RichTextPosition? _dragAnchor;
        private bool _revealEmbeddedCaret;
        internal event EventHandler? PictureSelectionChanged;
        internal bool IsPaginationEnabled { get; private set; } = true;

        internal void SetPaginationEnabled(bool enabled)
        {
            if (!_embedded || IsPaginationEnabled == enabled) return;
            ClosePageContextMenu();
            CancelComposition();
            _caretLine = null;
            _dragAnchor = null;
            Session?.ReleaseInputCapture(this);
            IsPaginationEnabled = enabled;
            Focusable = enabled;
            _engineHost!.ShowEditor = !enabled;
            var visibility = enabled ? UiVisibility.Visible : UiVisibility.Collapsed;
            _preview.Visibility = visibility;
            StatusNavigation!.Visibility = visibility;
            if (enabled) SyncTyping();
            Invalidate(UiInvalidationKind.Measure | UiInvalidationKind.Arrange | UiInvalidationKind.Render | UiInvalidationKind.Semantic);
            FocusEditor();
        }

        // Keep the editing engine attached for clipboard and text services. Only
        // the paginated surface paints it; the continuous layout is never shown.
        private sealed class EngineHost(StandardRichEdit editor) : UiElement
        {
            internal void AttachEditor() { AddChild(editor); SetHiddenFromAccessibility(editor, true); }
            internal bool ShowEditor { set => SetHiddenFromAccessibility(editor, !value); }
            protected override void RenderCore(UiRenderContext context)
            {
                if (!editor.IsHiddenFromAccessibility) base.RenderCore(context);
            }
        }

        internal void FocusEditor()
        {
            if (!IsPaginationEnabled)
            {
                Session?.SetFocus(_body);
                if (_body is StandardRichEdit editor) editor.RevealCaret();
                return;
            }
            SyncTyping();
            _pageSelection = _body.Selection;
            _typing = true;
            Focusable = true;
            Session?.SetFocus(this);
        }

        internal void SetZoom(double zoom)
        {
            _preview.ZoomMode = UiPrintPreviewZoom.Custom;
            _preview.Zoom = zoom * 96 / 72;
            Invalidate(UiInvalidationKind.Arrange | UiInvalidationKind.Render);
        }

        private bool ScrollViewport(UiInputEvent input)
        {
            bool horizontal = input.KeyModifiers.HasFlag(KeyboardModifierState.Shift) || input.WheelAxis == MouseWheelAxis.Horizontal;
            double height = Bounds.Height;
            if (horizontal)
                _scrollX = Math.Clamp(_scrollX - input.WheelDeltaNotches * 48, 0, Math.Max(0, _preview.Bounds.Width - Bounds.Width));
            else
            {
                double limit = Math.Max(0, _preview.Bounds.Height - height);
                double next = _scrollY - input.WheelDeltaNotches * 48;
                if (next > limit && _preview.PageIndex + 1 < _preview.PageCount) { _preview.NextPage(); _scrollY = 0; }
                else if (next < 0 && _preview.PageIndex > 0) { _preview.PreviousPage(); _scrollY = limit; }
                else _scrollY = Math.Clamp(next, 0, limit);
            }
            Invalidate(UiInvalidationKind.Arrange | UiInvalidationKind.Render);
            return true;
        }

        private void BodyChanged(object? sender, EventArgs args)
        {
            if (_syncing || !IsPaginationEnabled) return;
            _syncing = true;
            try { SyncTyping(); }
            finally { _syncing = false; }
        }

        internal PageLayoutContent(PaginatedDocument pages, Action close, Action<RichTextRange> jump,
            UiRichEdit body, string fontFamily, Action<string>? reportPageText, bool embedded = false)
        {
            _pages = pages;
            _preview = new StandardPrintPreview { Pages = pages };
            _previous = Button("Previous", () => _preview.PreviousPage());
            _next = Button("Next", () => _preview.NextPage());
            _body = body;
            _snapshot = body.Document;
            _pageGeometry = new WriterPageLineIndex(pages, _snapshot);
            _fontFamily = fontFamily;
            _reportPageText = reportPageText;
            _embedded = embedded;
            if (embedded && body is StandardRichEdit engine)
            {
                _engineHost = new EngineHost(engine);
                _engineHost.AttachEditor();
                AddChild(_engineHost);
                _typing = Focusable = true;
                _pageSelection = body.Selection;
                body.DocumentChanged += BodyChanged;
                body.SelectionChanged += BodyChanged;
            }
            _edit = Button("Edit text", EditOrApply);
            _type = Button("Type on page", ToggleTyping);
            _close = Button("Close", () =>
            {
                if (_draft is null)
                    close();
                else
                    EndDraft();
            });
            CreateGraphicsButtons();
            _go = Button("Go to body", () =>
            {
                if (ReferenceEquals(_snapshot, _body.Document) && PageSelection() is { } selected)
                    jump(selected);
            });
            _preview.ToolTipText = "Click text to place the caret. Shift-click to select text.";
            AddChild(_preview);
            if (!embedded)
            {
                AddChild(_previous);
                AddChild(_next);
            }
            if (_go is not null)
                AddChild(_go);
            AddChild(_edit);
            AddChild(_type);
            AddChild(_close);
            if (embedded)
            {
                InitializeLegacyPageContextMenu();
                _preview.ShowCaption = false;
                _pageCaption = new StandardLabel { Font = new BFontStyle("Segoe UI", 12), Foreground = WriterPalette.Title };
                StatusNavigation = new StandardPageNavigation(_previous, _next, _pageCaption);
                foreach (var button in new[] { _close, _go, _edit, _type, _objects, _objectProperties })
                    if (button is not null) button.Visibility = UiVisibility.Collapsed;
            }
            _preview.PageIndexChanged += (_, _) =>
            {
                if (_draft is not null && _preview.PageIndex != _draftPage)
                    _preview.PageIndex = _draftPage;
                ReportPageText();
                UpdateNavigation();
                Invalidate(UiInvalidationKind.Semantic);
            };
            UpdateNavigation();
            if (embedded) SetZoom(1);
        }

        internal void FocusPreview()
        {
            _preview.Session?.SetFocus(_preview);
            ReportPageText();
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (Session is { } session)
                session.InputDispatched += OnSessionInputDispatched;
        }

        protected override void OnDetached()
        {
            ClosePageContextMenu();
            CancelComposition();
            if (Session is { } session)
            {
                session.InputDispatched -= OnSessionInputDispatched;
                if (session.Host is IUiTextInputHost textHost)
                    textHost.ClearCaret(this);
            }
            base.OnDetached();
        }

        private void OnSessionInputDispatched(object? sender, UiInputEvent input)
        {
            if (input.Kind == UiInputEventKind.KeyboardKey)
                _shiftDown = (input.KeyModifiers & SelectionShiftModifiers) != 0 ||
                    input.KeyName is "Shift" or "ShiftLeft" or "ShiftRight" && input.KeyTransition == KeyboardKeyTransition.Down;
            if (_embedded) return;
            if (HandleGraphicsPointer(input)) return;
            if (_draft is not null || Session?.FocusedElement != _preview)
                return;
            if (input.Kind == UiInputEventKind.KeyboardKey && input.KeyTransition == KeyboardKeyTransition.Down &&
                input.KeyName is "Space" or " ")
            {
                _go?.Click();
                return;
            }
            if (IsSelectionPress(input) ||
                input.Kind == UiInputEventKind.TouchContact && input.TouchContactState == TouchContactState.Pressed)
                SelectOnPage(input);
        }

        private static bool IsSelectionPress(UiInputEvent input) =>
            input.Kind == UiInputEventKind.PointerButton && input.MouseButton == MouseButton.Left &&
            input.MouseButtonTransition == MouseButtonTransition.Down;

        private bool SelectOnPage(UiInputEvent input)
        {
            if (_preview.PageCount == 0 || !_preview.PageBounds.Contains(input.Position))
                return false;

            _caretLine = null;
            BPoint position = PagePoint(input.Position);
            var layoutPage = _pages.Layout.Pages[_preview.PageIndex];
            var floating = layoutPage.Shapes.Where(shape => shape.Image is not null && shape.Bounds.Contains(position))
                .OrderByDescending(shape => !shape.BehindText).ThenByDescending(shape => shape.ZOrder).FirstOrDefault();
            if (floating is not null && _graphicsSources.TryGetValue(floating.ZOrder, out var source))
            {
                WriterGraphics.Select(_body, source);
                _pageSelection = RichTextRange.Caret(_snapshot.PositionAt(Math.Clamp(source.ParagraphIndex, 0, _snapshot.ParagraphCount - 1), 0));
                _body.Selection = _pageSelection.Value;
                if (_typing) FocusTyping();
                PictureSelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
            }
            else if (WriterPageOccurrences.PictureAt(layoutPage, _snapshot, position) is { } picture)
            {
                WriterGraphics.Select(_body, null);
                CancelComposition();
                _pageSelection = picture.Range;
                _body.Selection = picture.Range;
                PictureSelectionChanged?.Invoke(this, EventArgs.Empty);
                if (_typing) FocusTyping();
                Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
            }
            else if (_pageGeometry.HitTest(_preview.PageIndex, position,
                _embedded ? RichEditHitTestMode.Nearest : RichEditHitTestMode.Exact) is { } hit)
            {
                WriterGraphics.Select(_body, null);
                RichTextPosition focus = hit.Position;
                RememberCaretLine(focus, hit.Line, position.X);
                _pageSelection = (_shiftDown || (input.KeyModifiers & SelectionShiftModifiers) != 0) && PageSelection() is { } previous
                    ? new RichTextRange(previous.Anchor, focus) : RichTextRange.Caret(focus);
                _body.Selection = _pageSelection.Value;
                PictureSelectionChanged?.Invoke(this, EventArgs.Empty);
                if (_typing)
                {
                    CancelComposition();
                    FocusTyping();
                    if (_embedded)
                    {
                        _dragAnchor = _pageSelection.Value.Anchor;
                        Session?.CaptureInput(this);
                    }
                }
                _go!.IsEnabled = true;
                Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
            }
            return true;
        }

        private const KeyboardModifierState SelectionShiftModifiers =
            KeyboardModifierState.Shift | KeyboardModifierState.LeftShift | KeyboardModifierState.RightShift;

        private RichTextRange? PageSelection() => _pageSelection ?? (_bodyLine is { } line
            ? RichTextRange.Caret(_snapshot.PositionAt(line.Paragraph, line.Offset)) : null);

        private void ToggleTyping()
        {
            _typing = !_typing;
            Focusable = _typing;
            _type.Text = _typing ? "Finish typing" : "Type on page";
            if (_typing)
            {
                _pageSelection = PageSelection() ?? RichTextRange.Caret(RichTextDocument.Start);
                FocusTyping();
            }
            else
            {
                CancelComposition();
                if (Session?.Host is IUiTextInputHost host)
                    host.ClearCaret(this);
                FocusPreview();
            }
            Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
        }

        private void FocusTyping()
        {
            _body.Selection = _pageSelection!.Value;
            Session?.SetFocus(this);
        }

        // The embedded preview paints the page. The editor owns its input so
        // menus and modal windows can consume a press before it reaches the page.
        protected override bool ShouldHitTestChildren(BPoint point) =>
            !_embedded || !IsPaginationEnabled;

        protected override bool OnInput(UiInputEvent input)
        {
            if (_embedded && !IsPaginationEnabled) return base.OnInput(input);
#if BROILER_UI_HOSTED_RICH_EDIT_MENU
            if (_embedded && HandleHostedPageContextMenu(input)) return true;
#endif
            if (_embedded && Bounds.Contains(input.Position))
            {
                if (input.Kind == UiInputEventKind.PointerWheel)
                    return ScrollViewport(input);
                if (IsSelectionPress(input))
                {
                    if (SelectOnPage(input))
                        return true;
                    Session?.SetFocus(this);
                }
            }
            if (input.Kind == UiInputEventKind.PointerButton &&
                input.MouseButton == MouseButton.Right && input.MouseButtonTransition == MouseButtonTransition.Down &&
                OpenPageContextMenu(input.Position))
                return true;
            if (_embedded && WriterGraphics.Selected(_body) is not null &&
                input.Kind is UiInputEventKind.TextInput or UiInputEventKind.TextComposition or UiInputEventKind.KeyboardKey)
            {
                WriterGraphics.Select(_body, null);
                PictureSelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            if (_embedded && _dragAnchor is { } anchor)
            {
                if (input.Kind == UiInputEventKind.PointerButton && input.MouseButtonTransition == MouseButtonTransition.Up)
                {
                    _dragAnchor = null;
                    Session?.ReleaseInputCapture(this);
                    return true;
                }
                if (input.Kind == UiInputEventKind.PointerMove && Session?.CapturedElement == this && _preview.PageCount > 0)
                {
                    var point = PagePoint(input.Position);
                    if (_pageGeometry.HitTest(_preview.PageIndex, point, RichEditHitTestMode.Nearest) is { } hit)
                    {
                        RememberCaretLine(hit.Position, hit.Line, point.X);
                        _body.Selection = new RichTextRange(anchor, hit.Position);
                    }
                    return true;
                }
            }
            if (_graphicsMode && HandleGraphicsKey(input)) return true;
            if (!_typing || Session?.FocusedElement != this)
                return base.OnInput(input);
            if (input.Kind is not (UiInputEventKind.KeyboardKey or UiInputEventKind.TextInput or UiInputEventKind.TextComposition))
                return base.OnInput(input);
            if (WriterTables.HandleTab(_body, input))
            {
                SyncTyping();
                Session?.SetFocus(this);
                return true;
            }
            if (input.Kind == UiInputEventKind.KeyboardKey && input.KeyName is "PageUp" or "PageDown")
            {
                if (input.KeyTransition == KeyboardKeyTransition.Down)
                    _preview.PageIndex += input.KeyName == "PageUp" ? -1 : 1;
                return true;
            }
            if (input.Kind == UiInputEventKind.KeyboardKey &&
                input.KeyName is "Home" or "End" or "Up" or "Down")
            {
                const KeyboardModifierState shift = KeyboardModifierState.Shift |
                    KeyboardModifierState.LeftShift | KeyboardModifierState.RightShift;
                if ((input.KeyModifiers & ~shift) == 0)
                {
                    if (input.KeyTransition == KeyboardKeyTransition.Down && _compositionHeader is null)
                        MovePageCaret(input.KeyName, (input.KeyModifiers & shift) != 0);
                    return true;
                }
            }
            bool handled = _body.DispatchInput(input);
            if (input.Kind == UiInputEventKind.TextComposition)
                _compositionHeader = input.CompositionState is TextCompositionState.Started or TextCompositionState.Updated
                    ? input.Header : null;
            SyncTyping();
            return handled || base.OnInput(input);
        }

        private void CancelComposition()
        {
            if (_compositionHeader is not { } header)
                return;
            _compositionHeader = null;
            if (!_body.IsDisposed)
                _body.DispatchInput(UiInputEvent.FromTextComposition(new TextCompositionEvent(header, "", TextCompositionState.Cancelled)));
        }

        private void RememberCaretLine(RichTextPosition focus, RichEditLineId? line, double? inlineCoordinate = null) =>
            _caretLine = line is { } id ? (focus, id, inlineCoordinate) : null;

        private RichEditLineId? PreferredLine(RichTextPosition focus) =>
            _caretLine is { } caret && caret.Focus == focus ? caret.Line : null;

        private RichEditLineGeometry? LineForCaret(RichTextPosition focus) =>
            _pageGeometry.GetLine(focus, PreferredLine(focus), _preview.PageIndex);

        private RichEditCaretGeometry? CaretGeometry(RichTextPosition focus) =>
            _pageGeometry.GetCaret(focus, PreferredLine(focus), _preview.PageIndex,
                _caretLine is { } caret && caret.Focus == focus ? caret.InlineCoordinate : null);

        private void MovePageCaret(string key, bool extend)
        {
            var range = _body.Selection;
            if (LineForCaret(range.Focus) is not { } source)
                return;

            var target = source;
            double? targetInline = null;
            RichTextPosition position = key is "Home" or "Up" ? source.Start : source.End;
            if (key is "Up" or "Down" && _pageGeometry.GetRelativeLine(source.Id, key == "Up" ? -1 : 1) is { } adjacent)
            {
                target = adjacent;
                double caretX = CaretGeometry(range.Focus)?.InlineCoordinate ?? source.InlineStart;
                double relativeX = Math.Max(0, caretX - source.InlineStart);
                targetInline = target.InlineStart + relativeX;
                position = _pageGeometry.GetPositionInLine(target.Id, targetInline.Value) ?? target.Start;
            }

            var selection = extend ? new RichTextRange(range.Anchor, position) : RichTextRange.Caret(position);
            RememberCaretLine(position, target.Id, targetInline);
            _pageSelection = selection;
            _body.Selection = selection;
            RevealOnPage(position);
            Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
        }

        private IUiTextEditor? TextEditor => _typing ? _body as IUiTextEditor : null;
        public UiTextEditorMetrics GetTextEditorMetrics() => TextEditor?.GetTextEditorMetrics() ?? default;
        public string GetTextEditorRange(int start, int maxLength) => TextEditor?.GetTextEditorRange(start, maxLength) ?? "";
        public bool DeleteSurroundingText(int beforeLength, int afterLength) =>
            ApplyTextService(editor => editor.DeleteSurroundingText(beforeLength, afterLength));
        public bool SetEditorSelection(int start, int end) => ApplyTextService(editor => editor.SetEditorSelection(start, end));
        public bool SetComposingRegion(int start, int end) => ApplyTextService(editor => editor.SetComposingRegion(start, end));
        public bool PerformEditorAction(UiTextEditorAction action) => ApplyTextService(editor => editor.PerformEditorAction(action));

        private bool ApplyTextService(Func<IUiTextEditor, bool> apply)
        {
            if (TextEditor is not { } editor || !apply(editor))
                return false;
            SyncTyping();
            return true;
        }

        private void SyncTyping()
        {
            RichTextRange selection = _body.Selection;
            if (!ReferenceEquals(_snapshot, _body.Document))
                RefreshPages(selection);
            else if (_pageSelection != selection)
            {
                if (_caretLine is { } caret && caret.Focus != selection.Focus)
                    _caretLine = null;
                _pageSelection = selection;
                if (_embedded && selection.IsEmpty && selection.Focus == RichTextDocument.Start)
                    _scrollX = _scrollY = 0;
                RevealOnPage(selection.Focus);
            }
            Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
        }

        private void RefreshPages(RichTextRange selection)
        {
            PaginatedDocument updated;
            if (_embedded)
            {
                var scene = WriterGraphics.CreateScene(_body.Document, _fontFamily);
                updated = scene.Pages;
                _graphicsSources = scene.Sources;
            }
            else updated = Paginate(_body.Document, _fontFamily);
            PaginatedDocument previous = _pages;
            _snapshot = _body.Document;
            _pageSelection = selection;
            _pages = updated;
            _pageGeometry = new WriterPageLineIndex(updated, _snapshot);
            _caretLine = null;
            _preview.Pages = updated;
            RevealOnPage(selection.Focus);
            ReportPageText();
            previous.Dispose();
        }

        private void RevealOnPage(RichTextPosition focus)
        {
            _revealEmbeddedCaret = false;
            if (LineForCaret(focus) is { } line)
            {
                _preview.PageIndex = line.Id.Fragment;
                if (_embedded)
                {
                    if (Bounds.Height <= 0)
                    {
                        _revealEmbeddedCaret = true;
                        return;
                    }
                    BRect caret = CaretGeometry(focus)?.Bounds ?? BRect.Empty;
                    double scale = _preview.Zoom;
                    double top = (caret.IsEmpty ? line.Top : caret.Top) * scale + _preview.PagePadding;
                    double bottom = (caret.IsEmpty ? line.Top + line.Height : caret.Bottom) * scale + _preview.PagePadding;
                    double height = Math.Max(1, Bounds.Height);
                    if (top < _scrollY) _scrollY = Math.Max(0, top - 8);
                    else if (bottom > _scrollY + height) _scrollY = Math.Max(0, bottom - height + 8);
                    if (!caret.IsEmpty)
                    {
                        double left = caret.Left * scale + _preview.PagePadding;
                        if (left < _scrollX) _scrollX = Math.Max(0, left - 8);
                        else if (left + 1 > _scrollX + Bounds.Width) _scrollX = Math.Max(0, left + 9 - Bounds.Width);
                    }
                    Invalidate(UiInvalidationKind.Arrange);
                }
            }
        }

        protected override void RenderCore(UiRenderContext context)
        {
            if (_embedded && !IsPaginationEnabled)
            {
                base.RenderCore(context);
                return;
            }
            if (_graphicsMode) SyncGraphics();
            if (_embedded) context.RenderList.PushClip(Bounds);
            foreach (UiElement child in Children)
            {
                if (child == _preview)
                    RenderPreview(context);
                else
                    child.Render(context);
            }
            if (_embedded) context.RenderList.PopClip();
            if (_graphicsMode) context.Defer(PaintGraphics);
            if (!_graphicsMode && _draft is null && _pageSelection is { } range && _preview.PageCount > 0)
                PaintSelection(context, range);
#if BROILER_UI_HOSTED_RICH_EDIT_MENU
            if (_embedded && _body is StandardRichEdit editor)
                editor.RenderContextMenuOverlay(context, this);
#endif
        }

        private void RenderPreview(UiRenderContext context)
        {
            _previewCommands.Clear();
            _preview.Render(new UiRenderContext(_previewCommands, context.Session, context.Host));
            foreach (BRenderCommand command in _previewCommands.Commands)
            {
                // Pagination positions text and decorations using a baseline at 0.8 em
                // from the text origin. DirectWrite otherwise uses the font's default ascent.
                context.RenderList.Append(command is BRenderCommand.DrawText { Text.Baseline: null } text
                    ? text with { Text = text.Text with { Baseline = text.Text.Font.Size * 0.8 } }
                    : command);
            }
        }

        protected override UiSemanticNode GetSemanticNodeCore()
        {
            UiSemanticNode node = base.GetSemanticNodeCore();
            if (_embedded && !IsPaginationEnabled) return node;
#if BROILER_UI_HOSTED_RICH_EDIT_MENU
            if (_embedded && _body is StandardRichEdit editor && editor.GetContextMenuSemanticNode(this) is { } menu)
                node = node with { Children = [.. node.Children, menu] };
#endif
            if (_draft is not null || _preview.PageCount == 0)
                return node;
            BRect paper = _preview.PageBounds.IsEmpty ? _preview.Bounds : _preview.PageBounds;
            var page = _pages.Layout.Pages[_preview.PageIndex];
            var stories = WriterPageOccurrences.ReadableLines(page).ToLookup(line => line.Story);
            var text = new UiSemanticNode(UiSemanticRole.Group, $"Rendered page {page.Number} of {_preview.PageCount}", paper,
                UiSemanticState.Visible | UiSemanticState.ReadOnly,
                [.. new[] { WriterPageStory.Header, WriterPageStory.Body, WriterPageStory.Note, WriterPageStory.Footer }
                    .Where(story => stories.Contains(story))
                    .Select(story => new UiSemanticNode(UiSemanticRole.Label,
                        string.Join("\n", stories[story].Select(line => line.Text)), paper,
                        UiSemanticState.Visible | UiSemanticState.ReadOnly, [])
                    {
                        Description = story switch
                        {
                            WriterPageStory.Header => "Page header",
                            WriterPageStory.Footer => "Page footer",
                            WriterPageStory.Note => "Page notes",
                            _ => "Page body",
                        },
                    })]);
            return node with
            {
                Role = _typing ? UiSemanticRole.RichEdit : UiSemanticRole.Group,
                Name = _typing ? "Page Layout editor" : _graphicsMode ? "Page Layout objects" : "Page Layout content",
                State = _typing ? node.State | UiSemanticState.Enabled |
                    (Session?.FocusedElement == this ? UiSemanticState.Focused : UiSemanticState.None) : node.State,
                TextInfo = _typing ? _body.GetSemanticNode().TextInfo : null,
                Children = [text, .. GraphicsSemantics(), .. node.Children],
            };
        }

        private void ReportPageText()
        {
            if (_reportPageText is null)
                return;
            if (_draft is not null || _preview.PageCount == 0)
            {
                _reportPageText("");
                return;
            }
            LayoutPage page = _pages.Layout.Pages[_preview.PageIndex];
            string text = string.Join("\n", WriterPageOccurrences.ReadableLines(page)
                .OrderBy(line => line.Story switch
                {
                    WriterPageStory.Header => 0,
                    WriterPageStory.Body => 1,
                    WriterPageStory.Note => 2,
                    _ => 3,
                }).Select(line => line.Text));
            _reportPageText($"Page {page.Number} of {_preview.PageCount}\n{text}");
        }

        private BRect PageClip(BRect viewport)
        {
            BRect visible = viewport.Intersect(_preview.PageBounds);
            if (visible.IsEmpty)
                return BRect.Empty;
            BPoint start = PagePoint(visible.Location);
            BPoint end = PagePoint(new BPoint(visible.Right, visible.Bottom));
            return new BRect(start.X, start.Y, end.X - start.X, end.Y - start.Y);
        }

        private void PaintSelection(UiRenderContext context, RichTextRange range)
        {
            BRect paper = _preview.PageBounds;
            if (paper.IsEmpty)
                return;
            var page = _pages.GetPage(_preview.PageIndex);
            double x = paper.Width / page.Size.Width;
            double y = paper.Height / page.Size.Height;
            var viewport = new BRect(Bounds.Left, Bounds.Top, Bounds.Width, Math.Max(0, Bounds.Height - (_embedded ? 0 : 36)));
            BRect clip = PageClip(viewport);
            int fragment = _preview.PageIndex;
            context.RenderList.PushClip(viewport);
            if (_body.SecondarySelection is { IsEmpty: false } secondary)
                foreach (var box in _pageGeometry.GetSelectionBounds(fragment, secondary, clip))
                    context.RenderList.FillRect(new BRect(paper.Left + box.Left * x, paper.Top + box.Top * y,
                        Math.Max(1, box.Width * x), box.Height * y), BColor.FromArgb(0x55FFD166));
            if (WriterGraphics.Selected(_body) is { } selected)
            {
                var shape = PageObjects().FirstOrDefault(item => ReferenceEquals(item.Source, selected));
                if (shape.Source is not null)
                    context.RenderList.StrokeRect(new BRect(paper.Left + shape.Placed.Bounds.Left * x,
                        paper.Top + shape.Placed.Bounds.Top * y, shape.Placed.Bounds.Width * x, shape.Placed.Bounds.Height * y),
                        WriterPalette.Accent, 2);
            }
            BRect caret = CaretGeometry(range.Focus) is { } geometry && geometry.Line.Fragment == fragment
                ? geometry.Bounds : BRect.Empty;
            if (range.IsEmpty)
            {
                if (!caret.IsEmpty)
                    context.RenderList.FillRect(new BRect(paper.Left + caret.Left * x, paper.Top + caret.Top * y,
                        1, caret.Height * y), WriterPalette.Accent);
            }
            else
            {
                bool outline = WriterGraphics.InlineAt(_body) is { } picture && picture.Range == range;
                foreach (BRect box in _pageGeometry.GetSelectionBounds(fragment, range, clip))
                {
                    var bounds = new BRect(paper.Left + box.Left * x, paper.Top + box.Top * y,
                        Math.Max(1, box.Width * x), box.Height * y);
                    context.RenderList.FillRect(bounds, BColor.FromArgb(0x55377BD1));
                    if (outline)
                        context.RenderList.StrokeRect(bounds, WriterPalette.Accent, 2);
                }
            }
            if (_typing && Session?.FocusedElement == this && context.Host is IUiTextInputHost textHost)
            {
                if (!caret.IsEmpty)
                {
                    var metrics = GetTextEditorMetrics();
                    var screenCaret = new BRect(paper.Left + caret.Left * x, paper.Top + caret.Top * y, 1, caret.Height * y);
                    textHost.PublishCaret(new UiTextCaretInfo(this, screenCaret, metrics.SelectionEnd, metrics.SelectionStart,
                        metrics.SelectionEnd - metrics.SelectionStart, metrics.HasComposingRegion));
                    if (_body is StandardRichEdit { CompositionText.Length: > 0 } composing)
                        PaintComposition(context, paper, screenCaret, composing);
                }
                else
                    textHost.ClearCaret(this);
            }
            context.RenderList.PopClip();
        }

        private static void PaintComposition(UiRenderContext context, BRect paper, BRect caret, StandardRichEdit editor)
        {
            BFontStyle font = editor.Font with { Size = Math.Clamp(caret.Height, 12, 18) };
            double width = Math.Min(paper.Width, BTextMeasurer.MeasureAdvance(editor.CompositionText, font) + 8);
            double height = Math.Min(paper.Height, BTextMeasurer.GetLineHeight(font) + 6);
            double left = Math.Clamp(caret.Left, paper.Left, paper.Right - width);
            double top = Math.Clamp(caret.Bottom, paper.Top, paper.Bottom - height);
            var box = new BRect(left, top, width, height);
            context.RenderList.PushClip(box);
            context.RenderList.FillRect(box, editor.Background);
            context.RenderList.DrawText(new BTextRun(editor.CompositionText, font, editor.Foreground), new BPoint(left + 4, top + 2));
            context.RenderList.FillRect(new BRect(left + 3, box.Bottom - 2, Math.Max(0, width - 6), 1), WriterPalette.Accent);
            context.RenderList.PopClip();
        }

        private void EditOrApply()
        {
            if (_draft is { } draft)
            {
                if (!ReferenceEquals(_snapshot, _body.Document))
                    return;
                var selection = draft.Selection;
                bool changed = _body.ApplyDocumentEdit(_snapshot, draft.Document, selection);
                EndDraft();
                if (changed)
                    RefreshPages(selection);

                return;
            }

            if (!_edit.IsEnabled || !ReferenceEquals(_snapshot, _body.Document))
                return;
            if (_typing)
                ToggleTyping();
            _draftPage = _preview.PageIndex;
            _draft = new StandardRichEdit
            {
                Document = _snapshot,
                PaddingX = 12,
                PaddingY = 10,
                Background = BColor.White,
                BorderColor = WriterPalette.Accent,
                BorderThickness = 1,
                AccessibleName = "Page Layout document editor",
                ToolTipText = "Apply updates Page Layout. Discard closes the editor without applying changes.",
            };
            if (_body is StandardRichEdit standard)
                _draft.Font = standard.Font;
            _draft.Selection = PageSelection() ?? RichTextRange.Caret(RichTextDocument.Start);
            _revealDraftCaret = true;
            AddChild(_draft);
            ReportPageText();
            _edit.Text = "Apply";
            _close.Text = "Discard";
            UpdateNavigation();
            Invalidate(UiInvalidationKind.Measure | UiInvalidationKind.Arrange);
            Session?.SetFocus(_draft);
        }

        private void EndDraft()
        {
            var draft = _draft!;
            _draft = null;
            RemoveChild(draft);
            draft.Dispose();
            _edit.Text = "Edit text";
            _close.Text = "Close";
            UpdateNavigation();
            FocusPreview();
        }


        protected override BSize MeasureCore(BSize availableSize)
        {
            if (_embedded && !IsPaginationEnabled)
            {
                _body.Measure(availableSize);
                return availableSize;
            }
            if (_embedded)
            {
                _preview.Measure(availableSize);
                return availableSize;
            }
            _preview.Measure(new BSize(availableSize.Width, Math.Max(0, availableSize.Height - 36)));
            BSize button = new(Math.Min(110, availableSize.Width / (_go is null ? 3 : 8)), 30);
            _previous.Measure(button);
            _next.Measure(button);
            _close.Measure(button);
            _go?.Measure(button);
            _edit.Measure(button);
            _type.Measure(button);
            _draft?.Measure(availableSize);
            _objects.Measure(button);
            _objectProperties.Measure(button);
            return availableSize;
        }

        protected override void ArrangeCore(BRect bounds)
        {
            if (_embedded && !IsPaginationEnabled)
            {
                _engineHost!.Arrange(bounds);
                _body.Arrange(bounds);
                return;
            }
            if (_embedded)
            {
                if (_revealEmbeddedCaret) RevealOnPage(_body.Selection.Focus);
                _engineHost!.Arrange(BRect.Empty);
                // The engine still needs a stable width for nonvisual commands.
                _body.Arrange(new BRect(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)));
                if (_preview.PageCount == 0)
                {
                    _preview.Arrange(bounds);
                    return;
                }

                var size = _pages.GetPage(_preview.PageIndex).Size;
                double height = Math.Max(bounds.Height, size.Height * _preview.Zoom + 2 * _preview.PagePadding + 24);
                double previewWidth = Math.Max(bounds.Width, size.Width * _preview.Zoom + 2 * _preview.PagePadding);
                _scrollY = Math.Clamp(_scrollY, 0, height - bounds.Height);
                _scrollX = Math.Clamp(_scrollX, 0, previewWidth - bounds.Width);
                _preview.Arrange(new BRect(bounds.Left - _scrollX, bounds.Top - _scrollY, previewWidth, height));
                return;
            }
            _preview.Arrange(new BRect(bounds.Left, bounds.Top, bounds.Width, Math.Max(0, bounds.Height - 36)));
            double width = Math.Min(110, bounds.Width / (_go is null ? 3 : 8));
            double y = Math.Max(bounds.Top, bounds.Bottom - 30);
            _previous.Arrange(new BRect(bounds.Left, y, width, 30));
            _next.Arrange(new BRect(bounds.Left + width, y, width, 30));
            _go?.Arrange(new BRect(bounds.Left + 2 * width, y, width, 30));
            _edit.Arrange(new BRect(bounds.Left + 3 * width, y, width, 30));
            _type.Arrange(new BRect(bounds.Left + 4 * width, y, width, 30));
            _objects.Arrange(new BRect(bounds.Left + 5 * width, y, width, 30));
            _objectProperties.Arrange(new BRect(bounds.Left + 6 * width, y, width, 30));
            if (_draft is not null)
            {
                BRect paper = _preview.PageBounds;
                double draftWidth = Math.Min(Math.Max(420, paper.Width), Math.Max(1, bounds.Width - 16));
                double draftHeight = Math.Max(1, y - bounds.Top - 16);
                double left = bounds.Left + (bounds.Width - draftWidth) / 2;
                double top = bounds.Top + 8;
                _draft.Arrange(new BRect(left, top, draftWidth, draftHeight));
                if (_revealDraftCaret)
                {
                    _revealDraftCaret = false;
                    _draft.RevealCaret();
                }
            }
            _close.Arrange(new BRect(bounds.Right - width, y, width, 30));
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing && _embedded)
            {
                _body.DocumentChanged -= BodyChanged;
                _body.SelectionChanged -= BodyChanged;
            }
            base.Dispose(disposing);
            if (disposing)
                _pages.Dispose();
        }

        private void UpdateNavigation()
        {
            _previous.IsEnabled = _preview.PageIndex > 0;
            _next.IsEnabled = _preview.PageIndex + 1 < _preview.PageCount;
            if (_pageCaption is not null) _pageCaption.Text = $"Page {_preview.PageIndex + 1} of {_preview.PageCount}";
            if (_draft is not null)
            {
                _previous.IsEnabled = _next.IsEnabled = _go!.IsEnabled = false;
                _type.IsEnabled = false;
                return;
            }
            WriterPageOccurrence? first = _preview.PageCount == 0 ? null : WriterPageOccurrences.Get(_pages.Layout.Pages[_preview.PageIndex])
                .FirstOrDefault(occurrence => occurrence.Story == WriterPageStory.Body);
            _objects.IsEnabled = _draft is null && !_typing;
            _objectProperties.IsEnabled = _graphicsMode && !_body.IsReadOnly && WriterGraphics.Selected(_body) is { } shape && WriterGraphics.Editable(shape);
            _bodyLine = first;
            _bodyParagraph = first?.Paragraph;
            _edit.IsEnabled = !_graphicsMode && !_body.IsReadOnly && _body.IsEnabled;
            _type.IsEnabled = _edit.IsEnabled && _body is IUiTextEditor;
            if (_go is not null)
                _go.IsEnabled = _bodyParagraph.HasValue || _pageSelection is not null;
        }

        private static StandardButton Button(string text, Action click)
        {
            var button = new StandardButton { Text = text };
            button.Clicked += (_, _) => click();
            return button;
        }
    }
}
