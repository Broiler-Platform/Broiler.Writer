using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.Input.Touch;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ColorDialog.Standard;
using Broiler.UI.FormatCodeView;
using Broiler.UI.Label.Standard;
using Broiler.UI.PrintPreview.Standard;
using Broiler.UI.RichEdit;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
using Broiler.UI.RichEdit.Standard;
#endif

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterPaginatedEditorTests
{
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
    [Fact]
    public void Bidi_Boundary_Keeps_The_Visual_Edge_Nearest_The_Hit()
    {
        const string text = "aאב";
        var document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain(text)]);
        var settings = DocumentPaginator.CreateDefaultSettings() with
        {
            IncludeSourceGeometry = true,
            VisualTextMeasurer = (display, font, _) =>
            {
                Assert.Equal(text, display);
                double width = Broiler.Graphics.Text.BTextMeasurer.MeasureAdvance(display, font);
                return [new(0, width / 3), new(width, width * 2 / 3), new(width * 2 / 3, width / 3)];
            },
        };
        using var pages = new DocumentPaginator(settings).Paginate(document, DocumentPaginator.PreferredSetup(document),
            TestContext.Current.CancellationToken);
        var index = new WriterPageLineIndex(pages, document);
        var piece = Assert.Single(Assert.Single(pages.Layout.Pages[0].Lines).Pieces);
        var line = index.GetLine(document.PositionAt(0, 1))!.Value;
        var left = new BPoint(piece.X + piece.Width * .32, line.Top + line.Height / 2);
        var right = new BPoint(piece.X + piece.Width * .98, left.Y);

        var leftHit = index.HitTest(0, left, RichEditHitTestMode.Exact)!.Value;
        var rightHit = index.HitTest(0, right, RichEditHitTestMode.Exact)!.Value;
        Assert.Equal(document.PositionAt(0, 1), leftHit.Position);
        Assert.Equal(leftHit, rightHit);
        Assert.Equal(piece.X + piece.Width / 3, index.GetCaret(leftHit.Position, leftHit.Line, 0, left.X)!.Value.Bounds.Left, 6);
        Assert.Equal(piece.X + piece.Width, index.GetCaret(rightHit.Position, rightHit.Line, 0, right.X)!.Value.Bounds.Left, 6);
        var selection = new RichTextRange(document.PositionAt(0, 1), document.PositionAt(0, 2));
        var selected = Assert.Single(index.GetSelectionBounds(0, selection, new BRect(0, 0, 1000, 1000)));
        Assert.Equal(piece.X + piece.Width * 2 / 3, selected.Left, 6);
        Assert.Equal(piece.X + piece.Width, selected.Right, 6);
    }
#endif

    [Fact]
    public void Hyperlink_Underline_Is_Below_The_Explicit_Text_Baseline()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.formatting-codes"));
        const string label = "through";
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create(label, InlineStyle.Default with { LinkHref = "https://example.com" })
        ]);

        var frame = app.RenderFrame();
        var link = Assert.Single(frame.Commands.OfType<BRenderCommand.DrawText>(), text => text.Text.Text == label);
        var underline = Assert.Single(frame.Commands.OfType<BRenderCommand.FillRect>(), fill =>
            fill.Color == link.Text.Color && fill.Rect.Left == link.Origin.X);
        double baseline = Assert.IsType<double>(link.Text.Baseline);

        Assert.Equal(link.Text.Font.Size * 0.8, baseline, 6);
        Assert.True(underline.Rect.Top > link.Origin.Y + baseline);
    }

    [Theory]
    [InlineData("Text color...", false)]
    [InlineData("Highlight...", true)]
    public void Color_From_The_Format_Menu_Applies_To_The_Paginated_Selection(string item, bool highlight)
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Example");
        var selection = new RichTextRange(RichTextDocument.Start, app.Document.End);
        app.Editor.Selection = selection;

        ClickText(app, "Format");
        ClickText(app, item);
        var chooser = Assert.IsType<StandardColorDialog>(app.Session.ModalElement);
        Assert.Equal(selection, app.Editor.Selection);
        Assert.Null(app.Session.CapturedElement);

        var color = new BColor(0x33, 0x66, 0x99);
        chooser.SelectedColor = color;
        app.RenderFrame();
        var ok = chooser.Children.OfType<StandardButton>().Single(button => button.Text == "OK");
        double x = ok.Bounds.Left + ok.Bounds.Width / 2;
        double y = ok.Bounds.Top + ok.Bounds.Height / 2;
        Click(app, x, y);
        Release(app, x, y);

        Assert.Null(app.Session.ModalElement);
        Assert.Equal(selection, app.Editor.Selection);
        var style = app.Document.Paragraphs[0].StyleAt(0);
        Assert.Equal(color, highlight ? style.Background : style.Foreground);
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        Assert.Contains(pages.GetPage(0).Commands, command => highlight
            ? command is BRenderCommand.FillRect fill && fill.Color == color
            : command is BRenderCommand.DrawText text && text.Text.Color == color);
    }

    [Fact]
    public void Dismissing_A_Menu_Over_The_Page_Does_Not_Start_A_Selection()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Example");
        var selection = new RichTextRange(RichTextDocument.Start, app.Document.End);
        app.Editor.Selection = selection;

        ClickText(app, "Format");
        Assert.True(app.Menu.IsOpen);
        var page = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single().PageBounds;
        double x = page.Right - 10;
        double y = Math.Max(page.Top, app.EditorSurface.Bounds.Top) + 40;
        Assert.False(app.Menu.OverlayBounds.Contains(new BPoint(x, y)));

        Click(app, x, y);
        Assert.False(app.Menu.IsOpen);
        Assert.Equal(selection, app.Editor.Selection);
        Assert.Null(app.Session.CapturedElement);
    }

    [Fact]
    public void Right_Click_On_Page_Opens_Menu_And_Select_All_Edits_The_Shared_Selection()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Example");
        var frame = app.RenderFrame();
        var paper = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single().PageBounds;
        var text = WriterRenderCoordinates.ScreenText(frame).Single(command =>
            command.Text.Text == "Example" && paper.Contains(command.Origin));
        var point = InputPoint.ClientDeviceIndependentPixels(text.Origin.X + 1, text.Origin.Y + 5);
        app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(), point,
            MouseButtons.Right, MouseButton.Right, MouseButtonTransition.Down)));

        var menu = Assert.Single(app.EditorSurface.GetSemanticNode().Children, node => node.Role == UiSemanticRole.Menu);
        Assert.False(menu.Children.Single(row => row.Name == "Copy").State.HasFlag(UiSemanticState.Enabled));
        Assert.False(menu.Children.Single(row => row.Name == "Paste").State.HasFlag(UiSemanticState.Enabled));
        app.RenderFrame();
        var selectAll = menu.Children.Single(row => row.Name == "Select All");
        Click(app, selectAll.Bounds.Left + 10, selectAll.Bounds.Top + 10);
        Assert.DoesNotContain(app.EditorSurface.GetSemanticNode().Children, node =>
            node.Role == UiSemanticRole.Menu && node.State.HasFlag(UiSemanticState.Expanded));
        Assert.Equal(new RichTextRange(RichTextDocument.Start, app.Document.End), app.Editor.Selection);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        Assert.Null(app.Session.CapturedElement);
    }

    [Fact]
    public void Page_Context_Menu_Paints_Over_The_Selection_And_Copies_It()
    {
        using var renderer = new BImageRenderer();
        string? copied = null;
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { },
            setClipboardText: text => copied = text, getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Example");
        var selection = new RichTextRange(RichTextDocument.Start, app.Document.End);
        app.Editor.Selection = selection;
        var selectionColor = BColor.FromArgb(0x55377BD1);
        var selected = app.RenderFrame().Commands.OfType<BRenderCommand.FillRect>().First(fill => fill.Color == selectionColor).Rect;
        var point = InputPoint.ClientDeviceIndependentPixels(selected.Left + 1, selected.Top + 1);
        app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(), point,
            MouseButtons.Right, MouseButton.Right, MouseButtonTransition.Down)));

        Assert.Equal(selection, app.Editor.Selection);
        var commands = app.RenderFrame().Commands.ToArray();
        var menu = Assert.Single(app.EditorSurface.GetSemanticNode().Children, node => node.Role == UiSemanticRole.Menu);
        var copy = menu.Children.Single(row => row.Name == "Copy");
        Assert.True(copy.State.HasFlag(UiSemanticState.Enabled));
        Assert.False(menu.Bounds.Intersect(selected).IsEmpty);
        int selectionDraw = Array.FindLastIndex(commands, command => command is BRenderCommand.FillRect fill && fill.Color == selectionColor);
        int copyDraw = Array.FindIndex(commands, command => command is BRenderCommand.DrawText { Text.Text: "Copy" });
        Assert.True(selectionDraw >= 0 && copyDraw > selectionDraw);

        Click(app, copy.Bounds.Left + 10, copy.Bounds.Top + 10);
        Assert.Equal("Example", copied);
        Assert.Equal(selection, app.Editor.Selection);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        Assert.Null(app.Session.CapturedElement);

        app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(), point,
            MouseButtons.Right, MouseButton.Right, MouseButtonTransition.Down)));
        app.RenderFrame();
        var reopened = Assert.Single(app.EditorSurface.GetSemanticNode().Children, node => node.Role == UiSemanticRole.Menu);
        double outsideX = reopened.Bounds.Right + 5;
        Assert.True(app.EditorSurface.Bounds.Contains(new BPoint(outsideX, selected.Top + 1)));
        Click(app, outsideX, selected.Top + 1);
        Assert.Equal(selection, app.Editor.Selection);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        Assert.Null(app.Session.CapturedElement);
    }

#if BROILER_UI_HOSTED_RICH_EDIT_MENU
    [Fact]
    public void Keyboard_Page_Context_Menu_Keeps_Focus_And_Releases_Capture_On_Dismissal()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Example");
        var selection = new RichTextRange(RichTextDocument.Start, app.Document.End);
        app.Editor.Selection = selection;
        app.RenderFrame();

        Key(app, "F10", KeyboardModifierState.Shift);
        Assert.True(app.Editor.IsContextMenuOpen);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        Assert.Same(app.EditorSurface, app.Session.CapturedElement);
        Key(app, "Escape");
        Assert.False(app.Editor.IsContextMenuOpen);
        Assert.Null(app.Session.CapturedElement);
        Assert.Equal(selection, app.Editor.Selection);

        Key(app, "ContextMenu");
        Assert.True(app.Editor.IsContextMenuOpen);
        app.Session.SetFocus(app.Menu);
        Assert.False(app.Editor.IsContextMenuOpen);
        Assert.Null(app.Session.CapturedElement);

        app.Session.SetFocus(app.EditorSurface);
        Key(app, "ContextMenu");
        Assert.True(app.Editor.IsContextMenuOpen);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.pagination"));
        Assert.False(app.Editor.IsContextMenuOpen);
        Assert.Null(app.Session.CapturedElement);
        Assert.Same(app.Editor, app.Session.FocusedElement);
        Assert.Equal(selection, app.Editor.Selection);
    }
#endif

    [Fact]
    public void View_Menu_Toggles_Pagination_Without_Changing_Document_Or_Selection()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("First"),
            RichTextParagraph.Plain("Second").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        var original = app.Document;
        var selection = app.Editor.Selection;
        var item = app.Menu.Items.Single(menu => menu.Id == "view").Children.Single(menu => menu.Id == "pagination");
        Assert.True(item.IsChecked);

        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.pagination"));
        Assert.False(app.IsPaginationEnabled);
        Assert.False(item.IsChecked);
        Assert.Same(app.Editor, app.Session.FocusedElement);
        Assert.Same(original, app.Document);
        Assert.Equal(selection, app.Editor.Selection);
        var text = WriterRenderCoordinates.ScreenText(app.RenderFrame()).Select(command => command.Text.Text).ToArray();
        Assert.Contains("First", text);
        Assert.Contains("Second", text);
        Assert.DoesNotContain("Page 2 of 2", text);
        Assert.Contains(WriterViewDiagnostics.Collect(app.Document, isPaginated: app.IsPaginationEnabled), note => note.Code == "writer.view.pagination");

        app.Dispatch(UiInputEvent.FromTextInput(new Broiler.Input.Text.TextInputEvent(Header(), "!")));
        Assert.EndsWith("Second!", app.Document.PlainText);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("view.pagination"));
        Assert.True(app.IsPaginationEnabled);
        Assert.True(item.IsChecked);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        app.RenderFrame();
        Assert.Equal(2, app.EditorSurface.Children.OfType<StandardPrintPreview>().Single().PageCount);
        Assert.True(app.Editor.ExecuteCommand(Broiler.UI.RichEdit.RichEditCommand.Undo));
        Assert.Same(original, app.Document);
    }

    [Fact]
    public void Home_And_End_Follow_The_Page_Wrap_And_Keep_The_Caret_On_The_Same_Visual_Line()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        var document = RichTextDocument.FromPlainText(new string('m', 36))
            .WithPageGeometry(PageGeometry.A4 with
            {
                Width = 660, MarginLeft = 30, MarginRight = 30, ColumnCount = 4, ColumnGap = 16,
            });
        app.Editor.Document = document;
        app.Editor.Selection = RichTextRange.Caret(document.PositionAt(0, 1));
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        var lines = WriterPageOccurrences.Get(pages.Layout.Pages[0])
            .Where(line => line.Story == WriterPageStory.Body).ToArray();
        Assert.True(lines.Length >= 3);
        var start = document.PositionAt(0, lines[1].Offset);
        var end = document.PositionAt(0, lines[2].Offset);
        double firstLineTop = Assert.IsType<UiTextCaretInfo>(host.LastCaret).Bounds.Top;

        // The source position at a wrap is both one line's end and the next line's start.
        Key(app, "End");
        Assert.Equal(RichTextRange.Caret(start), app.Editor.Selection);
        app.RenderFrame();
        Assert.Equal(firstLineTop, Assert.IsType<UiTextCaretInfo>(host.LastCaret).Bounds.Top);
        Key(app, "Home");
        Assert.Equal(RichTextRange.Caret(RichTextDocument.Start), app.Editor.Selection);

        // A pointer hit at the same wrap must also retain the first line for the IME and Home.
        BRect paper = preview.PageBounds;
        BSize size = pages.GetPage(0).Size;
        BRect firstLine = lines[0].Bounds;
        double clickX = paper.Left + (firstLine.Right - firstLine.Width * .01) * paper.Width / size.Width;
        double clickY = paper.Top + (firstLine.Top + firstLine.Height / 2) * paper.Height / size.Height;
        Click(app, clickX, clickY);
        Release(app, clickX, clickY);
        Assert.Equal(RichTextRange.Caret(start), app.Editor.Selection);
        app.RenderFrame();
        Assert.Equal(firstLineTop, Assert.IsType<UiTextCaretInfo>(host.LastCaret).Bounds.Top);
        Key(app, "Home");
        Assert.Equal(RichTextRange.Caret(RichTextDocument.Start), app.Editor.Selection);

        Key(app, "Down", KeyboardModifierState.LeftShift);
        Assert.Equal(new RichTextRange(RichTextDocument.Start, start), app.Editor.Selection);
        Key(app, "End", KeyboardModifierState.Shift);
        Assert.Equal(new RichTextRange(RichTextDocument.Start, end), app.Editor.Selection);
        Key(app, "Home");
        Assert.Equal(RichTextRange.Caret(start), app.Editor.Selection);
        Key(app, "End", KeyboardModifierState.Control);
        Assert.Equal(RichTextRange.Caret(document.End), app.Editor.Selection);

        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.pagination"));
        app.Editor.Selection = RichTextRange.Caret(document.PositionAt(0, 1));
        app.RenderFrame();
        Key(app, "End");
        Assert.Equal(RichTextRange.Caret(document.End), app.Editor.Selection);
    }

    [Fact]
    public void Vertical_Navigation_Reveals_The_Caret_Across_Pages_Without_Rebuilding_Or_Editing()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Same"),
            RichTextParagraph.Plain("Same").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        app.Editor.Document = document;
        var first = document.PositionAt(0, 2);
        var second = document.PositionAt(1, 2);
        app.Editor.Selection = RichTextRange.Caret(first);
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        var pages = preview.Pages;
        Assert.False(app.Editor.GetCommandState(RichEditCommand.Undo).IsEnabled);

        Key(app, "Down", KeyboardModifierState.Shift);
        Assert.Equal(new RichTextRange(first, second), app.Editor.Selection);
        Assert.Equal(1, preview.PageIndex);
        app.RenderFrame();
        var caret = Assert.IsType<UiTextCaretInfo>(host.LastCaret);
        Assert.True(preview.PageBounds.Contains(caret.Bounds.Location));
        Assert.True(app.EditorSurface.Bounds.Contains(caret.Bounds.Location));

        Key(app, "Down");
        Assert.Equal(RichTextRange.Caret(document.End), app.Editor.Selection);
        Key(app, "Down");
        Assert.Equal(RichTextRange.Caret(document.End), app.Editor.Selection);
        Key(app, "Up");
        Assert.Equal(RichTextRange.Caret(document.PositionAt(0, 4)), app.Editor.Selection);
        Assert.Equal(0, preview.PageIndex);
        Key(app, "Up");
        Assert.Equal(RichTextRange.Caret(RichTextDocument.Start), app.Editor.Selection);

        Assert.Same(pages, preview.Pages);
        Assert.Same(document, app.Document);
        Assert.False(app.Editor.GetCommandState(RichEditCommand.Undo).IsEnabled);
    }

#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
    [Fact]
    public void Paginated_Symbol_Text_Can_Be_Clicked_And_Selected_At_Its_Source_Offsets()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("ab", InlineStyle.Default with { FontFamily = "Symbol" }),
        ]);
        app.Editor.Document = document;
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        var line = Assert.Single(pages.Layout.Pages[0].Lines);
        var piece = Assert.Single(line.Pieces);
        Assert.Equal("αβ", piece.Text);
        var paper = preview.PageBounds;
        var size = pages.GetPage(0).Size;
        var first = piece.SourceClusters[0];
        double x = paper.Left + (piece.X + first.EndAdvance * .75) * paper.Width / size.Width;
        double y = paper.Top + (line.Top + line.Height / 2) * paper.Height / size.Height;
        Click(app, x, y);
        Release(app, x, y);
        var afterFirst = document.PositionAt(0, 1);
        Assert.Equal(RichTextRange.Caret(afterFirst), app.Editor.Selection);
        app.RenderFrame();
        var caret = Assert.IsType<UiTextCaretInfo>(host.LastCaret);
        Assert.Equal(paper.Left + (piece.X + first.EndAdvance) * paper.Width / size.Width, caret.Bounds.Left, 5);
        Key(app, "End", KeyboardModifierState.Shift);
        Assert.Equal(new RichTextRange(afterFirst, document.End), app.Editor.Selection);
        Assert.Contains(app.RenderFrame().Commands.OfType<BRenderCommand.FillRect>(), fill =>
            fill.Color == BColor.FromArgb(0x55377BD1) && fill.Rect.Width > 1);
    }
#endif

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1.5, false)]
    public void Clicking_An_Inline_Picture_Selects_It_And_Its_Code(bool compact, double zoom, bool secondPage)
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { }, compactMode: compact);
        using var pixels = new BBitmap(30, 20);
        pixels.Clear(BColor.FromArgb(0xFFFF0000));
        var image = new InlineImage(pixels.Encode(), "image/png", 90, 60, "Red sample", "Sample picture");
        var pictureParagraph = RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image });
        app.Editor.Document = RichTextDocument.FromParagraphs(secondPage
            ? [RichTextParagraph.Plain("Previous page"), pictureParagraph.WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true })]
            : [pictureParagraph]);
        int paragraph = secondPage ? 1 : 0;
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(paragraph, 0));
        if (compact) Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.formatting-codes"));
        if (zoom != 1) Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.zoom.150"));
        var frame = app.RenderFrame();
        var bounds = WriterRenderCoordinates.ImageBounds(frame, Assert.Single(frame.Commands.OfType<BRenderCommand.DrawImage>()));
        Assert.True(app.EditorSurface.Bounds.Contains(new BPoint(bounds.Left + bounds.Width * .8, bounds.Top + bounds.Height / 2)),
            $"Picture {bounds} is outside editor {app.EditorSurface.Bounds}");
        Click(app, bounds.Left + bounds.Width * .8, bounds.Top + bounds.Height / 2);

        Assert.Equal(new RichTextRange(app.Document.PositionAt(paragraph, 0), app.Document.PositionAt(paragraph, 1)), app.Editor.Selection);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        var view = Descendants(app.Session.Roots.Single()).OfType<UiFormatCodeView>().Single();
        Assert.Equal("[Image]", view.Text[Math.Min(view.SelectionAnchor, view.SelectionFocus)..Math.Max(view.SelectionAnchor, view.SelectionFocus)]);
        var header = Descendants(app.Session.Roots.Single()).OfType<StandardLabel>().Single(label => label.Text.StartsWith("Formatting Codes ·"));
        Assert.Contains("90 × 60 pt", header.Text);
        Assert.Contains("image/png", header.Text);
        Assert.Contains("Red sample", header.Text);
        frame = app.RenderFrame();
        Assert.Contains(frame.Commands.OfType<BRenderCommand.StrokeRect>(), stroke => stroke.Color == WriterPalette.Accent && stroke.Thickness == 2);

        if (Environment.GetEnvironmentVariable("WRITER_TEST_SNAPSHOT") is { Length: > 0 } path && !compact && !secondPage)
        {
            using var bitmap = renderer.RenderToImage(frame, BSurfaceDescriptor.Default(new BSize(1200, 800)), BFrameContext.Default);
            File.WriteAllBytes(path, bitmap.Encode());
        }
    }

    [Fact]
    public void Floating_Pictures_Can_Be_Selected_In_The_Main_Editor()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        using var pixels = new BBitmap(30, 20);
        pixels.Clear(BColor.FromArgb(0xFFFF0000));
        var image = new InlineImage(pixels.Encode(), "image/png", 90, 60, "Floating sample");
        var shape = new DocumentShape(0, 40, 20, 90, 60, image: image);
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("Anchor")]).WithShapes([shape]);
        var frame = app.RenderFrame();
        var bounds = WriterRenderCoordinates.ImageBounds(frame, Assert.Single(frame.Commands.OfType<BRenderCommand.DrawImage>()));
        Click(app, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Assert.Same(shape, WriterGraphics.Selected(app.Editor));
        var header = Descendants(app.Session.Roots.Single()).OfType<StandardLabel>().Single(label => label.Text.StartsWith("Formatting Codes ·"));
        Assert.Contains("Floating Image", header.Text);
        Assert.Contains("Floating sample", header.Text);
        Assert.Contains(app.RenderFrame().Commands.OfType<BRenderCommand.StrokeRect>(), stroke => stroke.Color == WriterPalette.Accent && stroke.Thickness == 2);
    }

    [Fact]
    public void Main_Editor_Types_Into_The_Document_And_Preserves_Drag_Selection()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Before");
        var frame = app.RenderFrame();
        var text = WriterRenderCoordinates.ScreenText(frame).First(command => command.Text.Text == "Before");
        Click(app, text.Origin.X + 1, text.Origin.Y + 5);
        app.Dispatch(UiInputEvent.FromMouseMove(new MouseMoveEvent(Header(),
            InputPoint.ClientDeviceIndependentPixels(text.Origin.X + 30, text.Origin.Y + 5), MouseButtons.Left)));
        app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(),
            InputPoint.ClientDeviceIndependentPixels(text.Origin.X + 30, text.Origin.Y + 5), MouseButtons.None, MouseButton.Left, MouseButtonTransition.Up)));
        Assert.False(app.Editor.Selection.IsEmpty);
        app.Dispatch(UiInputEvent.FromTextInput(new Broiler.Input.Text.TextInputEvent(Header(), "X")));
        Assert.StartsWith("X", app.Document.PlainText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Compact_Touch_Tap_Places_The_Caret_And_Releases_Capture(bool paginated)
    {
        using var renderer = new BImageRenderer();
        using var host = new WriterUiHost(() => new BSize(480, 760), () => 1, () => { }, _ => { },
            getRenderer: () => renderer);
        using var app = new WriterApp(host, () => { }, compactMode: true);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.formatting-codes"));
        if (!paginated)
            Assert.True(app.Menu.CommandDispatcher.TryExecute("view.pagination"));
        app.Editor.SetPlainText("Touch");
        app.Editor.Selection = RichTextRange.Caret(RichTextDocument.Start);
        var text = WriterRenderCoordinates.ScreenText(app.RenderFrame()).First(command => command.Text.Text == "Touch");
        var point = new BPoint(text.Origin.X + 80, text.Origin.Y + 5);
        Assert.True(app.EditorSurface.Bounds.Contains(point));
        foreach (var state in new[] { TouchContactState.Pressed, TouchContactState.Released })
            app.Dispatch(UiInputEvent.FromTouchContact(new TouchContactEvent(Header(), 42,
                InputPoint.ClientDeviceIndependentPixels(point.X, point.Y), state, 1, InputEventSource.Synthetic)));

        Assert.Equal(RichTextRange.Caret(app.Document.End), app.Editor.Selection);
        Assert.Same(app.EditorSurface, app.Session.FocusedElement);
        Assert.Null(app.Session.CapturedElement);
    }

    [Fact]
    public void Main_Editor_Paginates_Without_Opening_Page_Layout_And_Repaginates_After_Editing()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("First page\nSecond page");
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("formatcodes.insert.page-break"));
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        Assert.Null(app.Session.ModalElement);
        Assert.Equal(2, preview.PageCount);
        Assert.Equal(1, preview.PageIndex);
        using var printed = WriterPageLayout.Paginate(app.Document, app.Editor.Font.FamilyName);
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        Assert.Equal(printed.Layout.Pages.SelectMany(WriterPageOccurrences.Get), pages.Layout.Pages.SelectMany(WriterPageOccurrences.Get));
        Assert.True(app.Editor.ExecuteCommand(Broiler.UI.RichEdit.RichEditCommand.Undo));
        Assert.Equal(1, preview.PageCount);
    }

    [Fact]
    public void Long_Documents_Automatically_Flow_Onto_Multiple_Pages()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText(string.Join("\n", Enumerable.Range(1, 150).Select(index => $"Paragraph {index}")));
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        using var printed = WriterPageLayout.Paginate(app.Document, app.Editor.Font.FamilyName);
        Assert.True(preview.PageCount > 1);
        Assert.Equal(printed.PageCount, preview.PageCount);
    }

    [Fact]
    public void Wheel_Scrolls_Inside_A_Page_Before_Turning_The_Page()
    {
        using var renderer = new BImageRenderer();
        using var host = Host(renderer);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("First\nSecond");
        app.RenderFrame();
        var preview = app.EditorSurface.Children.OfType<StandardPrintPreview>().Single();
        double before = preview.PageBounds.Top;
        app.Dispatch(UiInputEvent.FromMouseWheel(new MouseWheelEvent(Header(),
            InputPoint.ClientDeviceIndependentPixels(app.EditorSurface.Bounds.Left + 100, app.EditorSurface.Bounds.Top + 100),
            MouseButtons.None, MouseWheelAxis.Vertical, -2)));
        app.RenderFrame();
        Assert.Equal(0, preview.PageIndex);
        Assert.True(preview.PageBounds.Top < before);
    }

    private static WriterUiHost Host(BImageRenderer renderer) => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { }, getRenderer: () => renderer);
    private static InputEventHeader Header() => new(InputDeviceId.FromOpaqueValue("paginated-editor-test"), new InputTimestamp(1, 1000, "test"), 1);
    private static void Key(WriterApp app, string name, KeyboardModifierState modifiers = KeyboardModifierState.None) =>
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(Header(), KeyboardKey.FromName(name),
            KeyboardKeyTransition.Down, modifiers, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
    private static void Click(WriterApp app, double x, double y) => app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(),
        InputPoint.ClientDeviceIndependentPixels(x, y), MouseButtons.Left, MouseButton.Left, MouseButtonTransition.Down)));
    private static void Release(WriterApp app, double x, double y) => app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Header(),
        InputPoint.ClientDeviceIndependentPixels(x, y), MouseButtons.None, MouseButton.Left, MouseButtonTransition.Up)));
    private static void ClickText(WriterApp app, string label)
    {
        var text = WriterRenderCoordinates.ScreenText(app.RenderFrame()).Single(command => command.Text.Text == label);
        Click(app, text.Origin.X + 2, text.Origin.Y + 5);
        Release(app, text.Origin.X + 2, text.Origin.Y + 5);
    }
    private static IEnumerable<UiElement> Descendants(UiElement element) => element.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
