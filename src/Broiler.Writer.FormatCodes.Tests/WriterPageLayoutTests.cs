using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.PrintPreview.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterPageLayoutTests
{
    [Fact]
    public void Page_Layout_Navigates_The_Same_Paginated_Snapshot_As_Print()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        RichTextDocument document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("First page"),
            RichTextParagraph.Plain("Second page").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        app.Editor.Document = document;
        Run(app, "view.page-layout");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        Assert.Equal(2, preview.PageCount);
        Assert.Same(document, app.Document);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Next").Click();
        Assert.Equal(1, preview.PageIndex);
        using var print = WriterPageLayout.CreatePaginator(app.Editor.Font.FamilyName)
            .Paginate(document, Broiler.Documents.Pagination.DocumentPaginator.PreferredSetup(document), TestContext.Current.CancellationToken);
        Assert.Equal(Text(print.GetPage(1).Commands), Text(preview.Pages!.GetPage(1).Commands));
        dialog.Cancel();
        Assert.Same(document, app.Document);
    }

    [Fact]
    public void Page_Typing_Exposes_Rendered_Text_And_Paints_Uncommitted_Ime_Composition()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var original = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("Body text")])
            .WithRunningContent(RunningContent.Empty.WithHeader(PageSelection.Default,
                [WriterFormattingDialog.ParseRunningBand("Page {page}")]));
        app.Editor.Document = original;
        Run(app, "view.page-layout");
        var content = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children);
        app.RenderFrame();
        UiSemanticNode displayed = Assert.Single(content.GetSemanticNode().Children,
            node => node.Name == "Rendered page 1 of 1");
        Assert.Contains(displayed.Children, node => node.Name == "Page 1" && node.Description == "Page header");
        Assert.Contains(displayed.Children, node => node.Name == "Body text" && node.Description == "Page body");

        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Type on page").Click();
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("page-ime"), new InputTimestamp(1, 1000, "test"), 1);
        Compose("かな", Broiler.Input.Text.TextCompositionState.Updated);
        Assert.Same(original, app.Document);
        Assert.Contains("かな", Text(app.RenderFrame().Commands));
        Assert.True(host.LastCaret?.IsCompositionActive);
        UiSemanticNode editor = content.GetSemanticNode();
        Assert.Equal(UiSemanticRole.RichEdit, editor.Role);
        Assert.True(editor.TextInfo?.IsEditable);
        Assert.True(editor.TextInfo?.IsCompositionActive);

        Compose("", Broiler.Input.Text.TextCompositionState.Cancelled);
        Assert.DoesNotContain("かな", Text(app.RenderFrame().Commands));
        Assert.Same(original, app.Document);
        Compose("漢字", Broiler.Input.Text.TextCompositionState.Updated);
        Compose("漢字", Broiler.Input.Text.TextCompositionState.Committed);
        Assert.Equal("漢字Body text", app.Document.PlainText);
        app.RenderFrame();
        Assert.False(host.LastCaret?.IsCompositionActive);
        Assert.Equal("漢字Body text", content.GetSemanticNode().TextInfo?.Value);

        void Compose(string text, Broiler.Input.Text.TextCompositionState state) =>
            app.Dispatch(UiInputEvent.FromTextComposition(new Broiler.Input.Text.TextCompositionEvent(header, text, state)));
    }

    [Fact]
    public void Shift_Click_Extends_Character_Selection_Across_Rendered_Pages()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Wider", InlineStyle.Default with { Capitalization = TextCapitalization.AllCaps }),
            RichTextParagraph.Plain("Narrow").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        app.Editor.Document = document;
        Run(app, "view.page-layout");
        var content = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        var pages = Assert.IsType<Broiler.Documents.Pagination.PaginatedDocument>(preview.Pages);
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("page-select"), new InputTimestamp(1, 1000, "test"), 1);
        Click(0, 2);
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header, KeyboardKey.FromName("Shift"),
            KeyboardKeyTransition.Down, KeyboardModifierState.Shift, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
        Click(1, 3);
        Assert.Same(document, app.Document);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Go to body").Click();
        var selected = new RichTextRange(document.PositionAt(0, 2), document.PositionAt(1, 3));
        Assert.Equal(selected, app.Editor.Selection);
        Assert.Equal("der\nNar", document.Slice(selected).PlainText);

        void Click(int pageNumber, int offset)
        {
            preview.PageIndex = pageNumber;
            app.RenderFrame();
            var line = Assert.Single(pages.Layout.Pages[pageNumber].Lines, line => line.ParagraphIndex == pageNumber);
            var piece = Assert.Single(line.Pieces, piece => piece.SourceOffset == 0);
            double fraction = Broiler.Graphics.Text.BTextMeasurer.MeasureAdvance(piece.Text[..offset], piece.Font) /
                Broiler.Graphics.Text.BTextMeasurer.MeasureAdvance(piece.Text, piece.Font);
            BRect paper = preview.PageBounds;
            BSize size = pages.GetPage(pageNumber).Size;
            double x = paper.Left + (piece.X + piece.Width * fraction) * paper.Width / size.Width;
            double y = paper.Top + (line.Top + line.Height / 2) * paper.Height / size.Height;
            app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(header,
                InputPoint.ClientDeviceIndependentPixels(x, y), MouseButtons.Left, MouseButton.Left, MouseButtonTransition.Down)));
        }
    }

    [Fact]
    public void Type_On_Page_Uses_Shared_Editing_And_Undo_Without_Leaving_The_Preview()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("One"),
            RichTextParagraph.Plain("Two").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        var original = app.Document;
        Run(app, "view.page-layout");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        preview.PageIndex = 1;
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Type on page").Click();
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("page-type"), new InputTimestamp(1, 1000, "test"), 1);
        Key("Up", KeyboardModifierState.Shift);
        Assert.Equal(0, preview.PageIndex);
        Assert.Equal(new RichTextRange(original.PositionAt(1, 0), original.PositionAt(0, 0)), app.Editor.Selection);
        Key("Down");
        Assert.Equal(1, preview.PageIndex);
        app.Dispatch(UiInputEvent.FromTextInput(new Broiler.Input.Text.TextInputEvent(header, "Changed ")));
        Assert.Equal("One\nChanged Two", app.Document.PlainText);
        Assert.Same(dialog, app.Session.ModalElement);
        Assert.Equal(1, preview.PageIndex);
        Assert.Contains("Changed Two", string.Join("", Text(preview.Pages!.GetPage(1).Commands)));
        Key("Z", KeyboardModifierState.Control);
        Assert.Same(original, app.Document);
        Assert.Same(dialog, app.Session.ModalElement);

        void Key(string name, KeyboardModifierState modifiers = KeyboardModifierState.None) =>
            app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header, KeyboardKey.FromName(name),
                KeyboardKeyTransition.Down, modifiers, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
                IsExtended: false, WasDown: false)));
    }

    [Fact]
    public void Editing_Across_Paragraphs_In_Page_Layout_Repaginates_And_Commits_One_Undo_Step()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("First page", InlineStyle.Default with { Bold = true }),
            RichTextParagraph.Plain("Second page").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]).WithTables([new DocumentTable(0, 1, [new TableRow([new TableCell(0, 1, 0)])], [150])])
            .WithSections([new DocumentSection(0, 2, 1, 0)]);
        var original = app.Document;
        Run(app, "view.page-layout");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        var before = preview.Pages;
        app.RenderFrame();
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Edit text").Click();
        var draft = Assert.Single(content.Children.OfType<Broiler.UI.RichEdit.Standard.StandardRichEdit>());
        Assert.Same(draft, app.Session.FocusedElement);
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("page-edit"), new InputTimestamp(1, 1000, "test"), 1);
        app.Dispatch(UiInputEvent.FromTextInput(new Broiler.Input.Text.TextInputEvent(header, "Edited ")));
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header, KeyboardKey.FromName("End"),
            KeyboardKeyTransition.Down, KeyboardModifierState.Control, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header, KeyboardKey.FromName("Enter"),
            KeyboardKeyTransition.Down, KeyboardModifierState.None, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
        app.Dispatch(UiInputEvent.FromTextInput(new Broiler.Input.Text.TextInputEvent(header, "New paragraph")));
        Assert.Same(original, app.Document);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
        Assert.Equal("Edited First page\nSecond page\nNew paragraph", app.Document.PlainText);
        Assert.True(app.Document.Paragraphs[0].StyleAt("Edited ".Length).Bold);
        Assert.True(app.Document.Paragraphs[1].Style.PageBreakBefore);
        Assert.Equal(1, Assert.Single(app.Document.Tables).ParagraphCount);
        Assert.Equal(3, Assert.Single(app.Document.Sections).ParagraphCount);
        Assert.NotSame(before, preview.Pages);
        Assert.Contains("Edited First page", string.Join("", Text(preview.Pages!.GetPage(0).Commands)));
        Assert.Equal(1, preview.PageIndex);
        Assert.Contains("New paragraph", string.Join("", Text(preview.Pages.GetPage(1).Commands)));
        dialog.Cancel();
        Run(app, "edit.undo");
        Assert.Same(original, app.Document);
    }

    [Fact]
    public void Page_And_Section_Commands_Preserve_Editor_Content_And_Undo()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("One\nTwo\nThree");
        Run(app, "page.setup");
        Submit(app, "600, 800", "landscape", "42, 48, 64, 60", "18, 18", "1, 0");
        Assert.Equal(800, app.Document.PageGeometry!.Width);
        var body = app.Document;
        app.Editor.Selection = RichTextRange.Caret(body.PositionAt(1, 0));
        Run(app, "page.section-break");
        Submit(app, "nextPage", "2, 18", "7", "upperRoman");
        Assert.Equal(2, app.Document.Sections.Count);
        Assert.Equal(1, app.Document.Sections[1].ParagraphIndex);
        Assert.Equal(7, app.Document.Sections[1].PageNumberStart);
        Run(app, "edit.undo");
        Assert.Same(body, app.Document);
        Run(app, "edit.redo");
        Assert.Equal("One\nTwo\nThree", app.Document.PlainText);
        Run(app, "page.section-paper");
        Submit(app, "500, 700", "portrait", "42, 48, 64, 60", "18, 18", "2, 18");
        Assert.Equal(500, app.Document.Sections[1].Page!.Width);
        Assert.Equal(800, app.Document.PageGeometry!.Width);
        Run(app, "page.pagination");
        Submit(app, "yes", "yes", "2, 2", "column");
        Assert.True(app.Document.Paragraphs[1].Style.KeepWithNext);
        Assert.True(app.Document.Paragraphs[1].Style.ColumnBreakBefore);
    }

    [Fact]
    public void Soft_Breaks_Roundtrip_As_Odt_Hints_Without_Changing_Body_Text()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("One\nTwo\nThree");
        var original = app.Document;
        app.Editor.Selection = new RichTextRange(original.PositionAt(1, 0), original.End);
        Run(app, "page.pagination");
        Submit(app, "", "", "", "soft");
        Assert.Equal(new[] { 0, 2, 3 }, app.Document.Paragraphs.Select(p => p.Style.SoftPageBreakBefore));

        using var stream = new System.IO.MemoryStream();
        var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
        codec.Write(app.Document, stream);
        stream.Position = 0;
        var reopened = codec.Read(stream).Document;
        Assert.Equal(original.PlainText, reopened.PlainText);
        Assert.Equal(new[] { 0, 2, 3 }, reopened.Paragraphs.Select(p => p.Style.SoftPageBreakBefore));
        Assert.All(reopened.Paragraphs, p => Assert.False(p.Style.PageBreakBefore));
        Assert.Null(WriterPageFidelity.DescribeFirstChange(app.Document, reopened));
        using var docx = new System.IO.MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(app.Document));
        Assert.Contains("pagination settings", WriterPageFidelity.DescribeFirstChange(app.Document,
            new Broiler.Documents.Docx.DocxDocumentCodec().Read(docx).Document));
        using var pages = WriterPageLayout.Paginate(reopened, "Arial");
        Assert.Equal(3, pages.PageCount);

        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.pagination");
        Submit(app, "", "", "", "none");
        Assert.Equal(new[] { 0, 0, 2 }, app.Document.Paragraphs.Select(p => p.Style.SoftPageBreakBefore));
        Run(app, "edit.undo");
        Assert.Equal(2, app.Document.Paragraphs[1].Style.SoftPageBreakBefore);
    }

    [Fact]
    public void Odt_Inline_Markers_Preserve_Their_Offsets_And_Later_Leading_Hint()
    {
        var source = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Alpha beta gamma").WithParagraphStyle(ParagraphStyle.Default with
                { SoftPageMarkerOffsets = [5, 10] }),
            RichTextParagraph.Plain("Next page").WithParagraphStyle(ParagraphStyle.Default with { SoftPageBreakBefore = 4 }),
        ]);
        var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
        var imported = Roundtrip(source);
        Assert.Equal(source.PlainText, imported.PlainText);
        Assert.Equal(new[] { 5, 10 }, imported.Paragraphs[0].Style.SoftPageMarkerOffsets.ToArray());
        Assert.Equal(4, imported.Paragraphs[1].Style.SoftPageBreakBefore);
        var edited = imported.InsertText(imported.PositionAt(0, 0), "New ").Document;
        var saved = Roundtrip(edited);
        Assert.Equal("New Alpha beta gamma\nNext page", saved.PlainText);
        Assert.Equal(new[] { 9, 14 }, saved.Paragraphs[0].Style.SoftPageMarkerOffsets.ToArray());
        Assert.Equal(4, saved.Paragraphs[1].Style.SoftPageBreakBefore);
        Assert.Null(WriterPageFidelity.DescribeFirstChange(edited, saved));

        RichTextDocument Roundtrip(RichTextDocument document)
        {
            using var stream = new System.IO.MemoryStream();
            codec.Write(document, stream);
            stream.Position = 0;
            return codec.Read(stream).Document;
        }
    }

    [Fact]
    public void Soft_Break_On_List_Item_Preserves_List_And_Hint_In_Odt()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var bullet = ParagraphStyle.Default with { ListKind = ListKind.Bullet };
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("First item").WithParagraphStyle(bullet),
            RichTextParagraph.Plain("Second item").WithParagraphStyle(bullet),
        ]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.pagination");
        Submit(app, "", "", "", "soft");
        using var stream = new System.IO.MemoryStream();
        var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
        codec.Write(app.Document, stream);
        stream.Position = 0;
        var saved = codec.Read(stream).Document;
        Assert.Equal(app.Document.PlainText, saved.PlainText);
        Assert.All(saved.Paragraphs, p => Assert.Equal(ListKind.Bullet, p.Style.ListKind));
        Assert.Equal(2, saved.Paragraphs[1].Style.SoftPageBreakBefore);
        using var pages = WriterPageLayout.Paginate(saved, "Arial");
        Assert.Equal(2, pages.PageCount);
    }

    [Fact]
    public void Soft_Break_Before_Inline_Table_Roundtrips_With_Cell_Content()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Before table"),
            RichTextParagraph.Plain("Left cell"),
            RichTextParagraph.Plain("Right cell").WithParagraphStyle(ParagraphStyle.Default with { SoftPageMarkerOffsets = [5] }),
            RichTextParagraph.Plain("After table"),
        ]).WithTables([new DocumentTable(1, 2, [new TableRow([new TableCell(1, 1, 0), new TableCell(2, 1, 1)])],
            [100, 100], cellPadding: 0)]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.pagination");
        Submit(app, "", "", "", "soft");
        using var stream = new System.IO.MemoryStream();
        var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
        codec.Write(app.Document, stream);
        stream.Position = 0;
        var reopened = codec.Read(stream).Document;
        Assert.Equal(app.Document.PlainText, reopened.PlainText);
        Assert.Equal(2, Assert.Single(Assert.Single(reopened.Tables).Rows).Cells.Count);
        Assert.Equal(2, reopened.Paragraphs[1].Style.SoftPageBreakBefore);
        Assert.Equal(new[] { 5 }, reopened.Paragraphs[2].Style.SoftPageMarkerOffsets.ToArray());
        Assert.Null(WriterPageFidelity.DescribeFirstChange(app.Document, reopened));
        using var pages = WriterPageLayout.Paginate(reopened, "Arial");
        Assert.Equal(2, pages.PageCount);
        Assert.Contains("Before table", string.Join("", Text(pages.GetPage(0).Commands)));
        string second = string.Join("", Text(pages.GetPage(1).Commands));
        Assert.Contains("Left cell", second);
        Assert.Contains("Right cell", second);
    }

    [Fact]
    public void Soft_Break_Before_Second_Row_Survives_Odt_And_Skips_Remaining_Columns()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Opening row"),
            RichTextParagraph.Plain("Following row"),
        ]).WithTables([new DocumentTable(0, 2, [
            new TableRow([new TableCell(0, 1, 0)]),
            new TableRow([new TableCell(1, 1, 0)]),
        ], [140])]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.pagination");
        Submit(app, "", "", "", "soft");

        using var stream = new System.IO.MemoryStream();
        var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
        codec.Write(app.Document, stream);
        stream.Position = 0;
        var reopened = codec.Read(stream).Document;
        Assert.Equal(2, Assert.Single(reopened.Tables).Rows.Count);
        Assert.Equal(2, reopened.Paragraphs[1].Style.SoftPageBreakBefore);
        var columns = (reopened.PageGeometry ?? PageGeometry.A4) with { ColumnCount = 2, ColumnGap = 18 };
        using var pages = WriterPageLayout.Paginate(reopened.WithPageGeometry(columns), "Arial");
        Assert.Equal(2, pages.PageCount);
        Assert.Contains("Opening row", string.Join("", Text(pages.GetPage(0).Commands)));
        Assert.Contains("Following row", string.Join("", Text(pages.GetPage(1).Commands)));
    }

    [Fact]
    public void Soft_Row_Break_Does_Not_Split_A_Vertically_Merged_Cell()
    {
        var table = new DocumentTable(0, 3, [
            new TableRow([new TableCell(0, 1, 0, rowSpan: 2), new TableCell(1, 1, 1)]),
            new TableRow([new TableCell(2, 1, 1)]),
        ], [80, 80]);
        Assert.True(table.CanAnchorSoftBreak(0));
        Assert.False(table.CanAnchorSoftBreak(2));
    }

    [Fact]
    public void Running_Content_Command_Creates_A_First_Page_Field_And_Can_Undo()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Body");
        var original = app.Document;
        Run(app, "page.running-content");
        var picker = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var choices = Assert.Single(picker.Children).Children;
        choices.OfType<StandardEdit>().First().Text = "first";
        Assert.Single(choices.OfType<StandardButton>(), button => button.Text == "Edit").Click();
        Submit(app, "  Cover {page} ");
        Assert.True(app.Document.RunningContent.DifferentFirstPage);
        var band = Assert.Single(app.Document.RunningContent.Header(PageSelection.First));
        Assert.Equal("  Cover 1 ", band.Text);
        Assert.Contains(band.Runs, run => run.Style.PageField is { Kind: PageFieldKind.PageNumber, FollowsPageFormat: true });
        Run(app, "edit.undo");
        Assert.Same(original, app.Document);
    }

    [Fact]
    public void Rich_Band_Uses_Normal_Formatting_And_Commits_A_Single_Undo_Step()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var running = RunningContent.Empty.WithFooter(PageSelection.Default,
            [RichTextParagraph.Create("First line", new InlineStyle { Italic = true }), RichTextParagraph.Plain("Second line")]);
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("Body")]).WithRunningContent(running);
        var original = app.Document;
        Run(app, "page.running-content");
        var options = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children).Children;
        options.OfType<StandardEdit>().Last().Text = "footer";
        Assert.Single(options.OfType<StandardButton>(), button => button.Text == "Edit").Click();
        var controls = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children).Children;
        var input = Assert.Single(controls.OfType<Broiler.UI.RichEdit.Standard.StandardRichEdit>());
        input.Selection = new RichTextRange(RichTextDocument.Start, input.Document.End);
        Assert.Single(controls.OfType<StandardButton>(), button => button.Text == "Bold").Click();
        Assert.Single(controls.OfType<StandardButton>(), button => button.Text == "Apply").Click();
        var band = app.Document.RunningContent.Footer(PageSelection.Default);
        Assert.Equal(new[] { "First line", "Second line" }, band.Select(paragraph => paragraph.Text));
        Assert.True(band[0].StyleAt(0).Italic);
        Assert.All(band, paragraph => Assert.True(paragraph.StyleAt(0).Bold));
        Assert.Equal("Body", app.Document.PlainText);
        Run(app, "edit.undo");
        Assert.Same(original, app.Document);
    }

    [Fact]
    public void Header_Link_Survives_Docx_And_Tracks_Previous_Section()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var header = RunningContent.Empty.WithHeader(PageSelection.Default, [RichTextParagraph.Plain("Shared header")]);
        var local = RunningContent.Empty.WithFooter(PageSelection.Default, [RichTextParagraph.Plain("Local footer")]);
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("First body"), RichTextParagraph.Plain("Second body"),
        ]).WithPageGeometry(PageGeometry.A4).WithSections([
            new DocumentSection(0, 1, 1, 0, runningContent: header),
            new DocumentSection(1, 1, 1, 0, runningContent: local, breakType: SectionBreakType.NextPage, pageNumberStart: 7)
                { PageNumberFormat = ListNumberFormat.UpperRoman },
        ]);
        var original = app.Document;
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.running-content-link");
        Submit(app, "default", "header", "link");
        Assert.True(app.Document.Sections[1].RunningContent!.IsLinked(PageSelection.Default, header: true));
        using var stream = new System.IO.MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(app.Document));
        var reopened = new Broiler.Documents.Docx.DocxDocumentCodec().Read(stream).Document;
        Assert.Equal(2, reopened.Sections.Count);
        Assert.Equal(7, reopened.Sections[1].PageNumberStart);
        Assert.Equal(ListNumberFormat.UpperRoman, reopened.Sections[1].PageNumberFormat);
        Assert.True(reopened.Sections[1].RunningContent!.IsLinked(PageSelection.Default, header: true));
        Assert.Equal("Local footer", Assert.Single(reopened.RunningContentFor(reopened.Sections[1]).Footer(PageSelection.Default)).Text);
        Run(app, "edit.undo");
        Assert.Same(original, app.Document);

        app.Editor.Document = reopened;
        EditHeader(0, "Updated header");
        using var print = WriterPageLayout.Paginate(app.Document, "Arial");
        Assert.Contains("Updated header", string.Join("", Text(print.GetPage(1).Commands)));
        using var updatedStream = new System.IO.MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(app.Document));
        var updated = new Broiler.Documents.Docx.DocxDocumentCodec().Read(updatedStream).Document;
        Assert.Null(WriterPageFidelity.DescribeFirstChange(app.Document, updated));
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "page.running-content-link");
        Submit(app, "default", "header", "unlink");
        Assert.False(app.Document.Sections[1].RunningContent!.IsLinked(PageSelection.Default, header: true));
        EditHeader(0, "Updated again");
        Assert.Equal("Updated header", Assert.Single(app.Document.RunningContentFor(app.Document.Sections[1]).Header(PageSelection.Default)).Text);

        void EditHeader(int index, string text)
        {
            app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(index, 0));
            Run(app, "page.running-content");
            var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
            Assert.Single(Assert.Single(dialog.Children).Children.OfType<StandardButton>(), button => button.Text == "Edit").Click();
            Submit(app, text);
        }
    }

    [Fact]
    public void Running_Heading_Updates_On_Each_Page_And_Survives_Docx()
    {
        const string styleName = "Section Heading";
        var definition = new NamedParagraphStyle(styleName, ParagraphStyle.Default, InlineStyle.Default);
        var body = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Alpha"),
            RichTextParagraph.Plain("Beta").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        body = body.ApplyParagraphStyle(new RichTextRange(RichTextDocument.Start, body.End),
            new ParagraphStyleDelta { DefineStyle = definition, SetStyleName = true, StyleName = styleName });
        var header = WriterFormattingDialog.ParseRunningBand("Chapter {style:Section Heading}", body);
        var document = body.WithRunningContent(RunningContent.Empty.WithHeader(PageSelection.Default, [header]));
        using var stream = new System.IO.MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(document));
        var reopened = new Broiler.Documents.Docx.DocxDocumentCodec().Read(stream).Document;
        Assert.Contains(reopened.RunningContent.Header(PageSelection.Default)[0].Runs,
            run => run.Style.StyleReference is { ParagraphStyle: true, StyleName: styleName });
        using var print = WriterPageLayout.CreatePaginator("Arial").Paginate(reopened,
            Broiler.Documents.Pagination.DocumentPaginator.PreferredSetup(reopened), TestContext.Current.CancellationToken);
        Assert.Equal(2, print.PageCount);
        Assert.Contains("Chapter Alpha", string.Join("", Text(print.GetPage(0).Commands)));
        Assert.Contains("Chapter Beta", string.Join("", Text(print.GetPage(1).Commands)));
    }

    [Fact]
    public void Go_To_Body_Selects_The_Continuation_Of_A_Paragraph()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain(string.Join(" ", Enumerable.Repeat("Words wrapping across a short page.", 40))),
        ]).WithPageGeometry(PageGeometry.A4 with { Width = 250, Height = 250 });
        var document = app.Document;
        using var pages = WriterPageLayout.Paginate(document, app.Editor.Font.FamilyName);
        Assert.True(pages.PageCount > 1);
        var line = WriterPageOccurrences.Get(pages.Layout.Pages[1]).First(item => item.Story == WriterPageStory.Body);
        Assert.Equal(0, line.Paragraph);
        Assert.True(line.Offset > 0);
        Run(app, "view.page-layout");
        var content = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children);
        Assert.Single(content.Children.OfType<StandardPrintPreview>()).PageIndex = 1;
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Go to body").Click();
        Assert.Equal(document.PositionAt(0, line.Offset), app.Editor.Selection.Focus);
    }

    [Fact]
    public void Three_Sections_Draw_Running_Variants_And_Reevaluate_Page_Fields()
    {
        using var host = Host();
        using var app = new WriterApp(host, () => { });
        var one = RunningContent.Empty.WithHeader(PageSelection.First, [WriterFormattingDialog.ParseRunningBand("First {page}")])
            .WithDifferentFirstPage(true);
        var two = RunningContent.Empty.WithHeader(PageSelection.Default, [WriterFormattingDialog.ParseRunningBand("Middle {page} of {pages}")]);
        var three = RunningContent.Empty.WithFooter(PageSelection.Even, [WriterFormattingDialog.ParseRunningBand("Even footer")]);
        RichTextDocument document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Cover"),
            RichTextParagraph.Plain("Middle heading").WithParagraphStyle(ParagraphStyle.Default with { KeepWithNext = true }),
            RichTextParagraph.Plain("Middle text").WithParagraphStyle(ParagraphStyle.Default with { ColumnBreakBefore = true }),
            RichTextParagraph.Plain("Appendix"),
            RichTextParagraph.Plain("Appendix continued").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]).WithPageGeometry(PageGeometry.A4)
            .WithShapes([new DocumentShape(0, 12, 20, 48, 24, anchorPage: 2,
                fill: ShapeFill.Solid(Broiler.Graphics.Color.BColor.Red))])
            .WithSections([
            new DocumentSection(0, 1, 1, 0, runningContent: one),
            new DocumentSection(1, 2, 2, 18, runningContent: two, breakType: SectionBreakType.NextPage, pageNumberStart: 7)
                { PageNumberFormat = ListNumberFormat.UpperRoman },
            new DocumentSection(3, 2, 1, 0, runningContent: three, breakType: SectionBreakType.EvenPage),
        ]);
        app.Editor.Document = document;
        using var print = WriterPageLayout.CreatePaginator(app.Editor.Font.FamilyName)
            .Paginate(document, Broiler.Documents.Pagination.DocumentPaginator.PreferredSetup(document), TestContext.Current.CancellationToken);
        Assert.True(print.PageCount >= 4);
        var report = print.Layout.Pages.SelectMany(WriterPageOccurrences.Get).ToArray();
        Assert.Contains(report, occurrence => occurrence.Story == WriterPageStory.Header && occurrence.Variant == PageSelection.First &&
            ReferenceEquals(occurrence.RunningContent, one));
        Assert.Contains(report, occurrence => occurrence.Story == WriterPageStory.Footer && occurrence.Variant == PageSelection.Even &&
            ReferenceEquals(occurrence.RunningContent, three));
        string middle = string.Join("", Text(print.GetPage(1).Commands));
        Assert.Contains("Middle VII of " + print.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture), middle);
        Assert.All(report, occurrence => Assert.True(occurrence.Bounds.Height > 0));
        Run(app, "view.page-layout");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        Assert.Equal(print.PageCount, preview.PageCount);
        var previewDocument = Assert.IsType<Broiler.Documents.Pagination.PaginatedDocument>(preview.Pages);
        Assert.Single(print.Layout.Pages[1].Shapes);
        for (int i = 0; i < print.PageCount; i++)
        {
            Assert.Equal(Text(print.GetPage(i).Commands), Text(preview.Pages!.GetPage(i).Commands));
            Assert.Equal(print.Layout.Pages[i].Shapes.Select(shape => shape.Bounds),
                previewDocument.Layout.Pages[i].Shapes.Select(shape => shape.Bounds));
            Assert.Equal(WriterPageOccurrences.Get(print.Layout.Pages[i]), WriterPageOccurrences.Get(previewDocument.Layout.Pages[i]));
        }
        preview.PageIndex = 1;
        var pointer = new InputEventHeader(InputDeviceId.FromOpaqueValue("writer-page-layout-test"), new InputTimestamp(1, 1000, "test"), 1);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Type on page").Click();
        Assert.Equal(1, document.ParagraphIndexAt(app.Editor.Selection.Focus));
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(pointer, KeyboardKey.FromName("Down"),
            KeyboardKeyTransition.Down, KeyboardModifierState.None, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
        Assert.Equal(2, document.ParagraphIndexAt(app.Editor.Selection.Focus));
        Assert.Equal(1, preview.PageIndex);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Finish typing").Click();
        app.RenderFrame();
        BRect paper = preview.PageBounds;
        var secondColumn = Assert.Single(report, occurrence => occurrence.Story == WriterPageStory.Body && occurrence.Paragraph == 2);
        var pageSize = print.GetPage(1).Size;
        double x = paper.Left + (secondColumn.Bounds.Left + secondColumn.Bounds.Width / 2) * paper.Width / pageSize.Width;
        double y = paper.Top + (secondColumn.Bounds.Top + secondColumn.Bounds.Height / 2) * paper.Height / pageSize.Height;
        app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(pointer,
            InputPoint.ClientDeviceIndependentPixels(x, y), MouseButtons.Left, MouseButton.Left, MouseButtonTransition.Down)));
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(pointer, KeyboardKey.FromName("Space"),
            KeyboardKeyTransition.Down, KeyboardModifierState.None, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
            IsExtended: false, WasDown: false)));
        Assert.Equal(2, document.ParagraphIndexAt(app.Editor.Selection.Focus));
    }

    private static void Submit(WriterApp app, params string[] values)
    {
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        StandardEdit[] fields = content.Children.OfType<StandardEdit>().ToArray();
        Assert.Equal(values.Length, fields.Length);
        for (int index = 0; index < fields.Length; index++)
            fields[index].Text = values[index];
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
        Assert.Null(app.Session.ModalElement);
    }

    private static string[] Text(IEnumerable<BRenderCommand> commands) =>
        commands.OfType<BRenderCommand.DrawText>().Select(command => command.Text.Text).ToArray();
    private static void Run(WriterApp app, string command) => Assert.True(app.Menu.CommandDispatcher!.TryExecute(command));
    private static WriterUiHost Host() => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
}
