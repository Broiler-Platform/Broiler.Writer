using Broiler.Documents.Model;
using Broiler.Documents.Model.Tables;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Touch;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.PrintPreview.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterTableTests
{
    [Fact]
    public void Table_Menu_Mutates_Grid_And_Text_As_Single_Undo_Units()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Before\nAfter");
        var original = app.Document;
        Run("table.insert");
        Submit("2", "2");
        var table = Assert.Single(app.Document.Tables);
        Assert.Equal(1, table.ParagraphIndex);
        Assert.Equal(4, table.ParagraphCount);

        var input = new InputEventHeader(InputDeviceId.FromOpaqueValue("table-tab"), new InputTimestamp(1, 1000, "test"), 1);
        Tab(KeyboardModifierState.None);
        Assert.Equal(2, app.Document.ParagraphIndexAt(app.Editor.Selection.Focus));
        Tab(KeyboardModifierState.Shift);
        Assert.Equal(1, app.Document.ParagraphIndexAt(app.Editor.Selection.Focus));

        for (int index = 1; index <= 4; index++)
            app.Editor.ReplaceTextRange(RichTextRange.Caret(app.Document.PositionAt(index, 0)), ((char)('A' + index - 1)).ToString());
        Select(1);
        Run("table.insert-column");
        Assert.Equal("Before\nA\n\nB\nC\n\nD\nAfter", app.Document.PlainText);
        Run("table.delete-column");
        Assert.Equal("Before\nA\nB\nC\nD\nAfter", app.Document.PlainText);
        Select(1);
        Run("table.insert-row");
        Assert.Equal(3, Assert.Single(app.Document.Tables).Rows.Count);
        Run("table.delete-row");
        Assert.Equal("Before\nA\nB\nC\nD\nAfter", app.Document.PlainText);
        Select(1);
        var beforeMerge = app.Document;
        Run("table.merge");
        Submit("2", "2");
        var merged = Assert.Single(app.Document.Tables);
        Assert.Same(DocumentTableTopologyAdapter.FromTable(merged).GetCellAt(0, 0),
            DocumentTableTopologyAdapter.FromTable(merged).GetCellAt(1, 1));
        Assert.Equal(beforeMerge.PlainText, app.Document.PlainText);
        using (var odt = new MemoryStream())
        {
            var codec = new Broiler.Documents.Odt.OdtDocumentCodec();
            codec.Write(app.Document, odt);
            odt.Position = 0;
            Assert.Null(WriterTables.DescribeFirstSaveChange(app.Document, codec.Read(odt).Document));
        }
        Run("edit.undo");
        Assert.Same(beforeMerge, app.Document);
        Run("edit.redo");
        Run("table.split");
        Assert.Equal(4, DocumentTableTopologyAdapter.FromTable(Assert.Single(app.Document.Tables)).CellCount);
        Assert.Equal("Before\nA\nB\nC\nD\n\n\n\nAfter", app.Document.PlainText);

        Run("table.column-width");
        Submit("100");
        Run("table.row-height");
        Submit("26", "exact");
        var sized = app.Document;
        Assert.Equal(100, sized.Tables[0].ColumnWidths[0]);
        Assert.Equal(26, sized.Tables[0].Rows[0].MinHeight);
        using var saved = new MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(sized));
        var restored = new Broiler.Documents.Docx.DocxDocumentCodec().Read(saved).Document;
        Assert.Equal(100, Assert.Single(restored.Tables).ColumnWidths[0]);
        Assert.Equal(26, restored.Tables[0].Rows[0].MinHeight);
        Assert.True(restored.Tables[0].Rows[0].ExactHeight);
        Assert.Null(WriterTables.DescribeFirstSaveChange(sized, restored));
        Run("table.delete-table");
        Assert.Empty(app.Document.Tables);
        Assert.Equal(original.PlainText, app.Document.PlainText);
        Run("edit.undo");
        Assert.Same(sized, app.Document);

        void Run(string command) => Assert.True(app.Menu.CommandDispatcher!.TryExecute(command));
        void Select(int paragraph) => app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(paragraph, 0));
        void Tab(KeyboardModifierState modifiers) =>
            app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(input, KeyboardKey.FromName("Tab"),
                KeyboardKeyTransition.Down, modifiers, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1,
                IsExtended: false, WasDown: false)));
        void Submit(params string[] values)
        {
            var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
            var children = Assert.Single(dialog.Children).Children;
            var inputs = children.OfType<StandardEdit>().ToArray();
            Assert.Equal(values.Length, inputs.Length);
            for (int index = 0; index < values.Length; index++)
                inputs[index].Text = values[index];
            Assert.Single(children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
            Assert.Null(app.Session.ModalElement);
        }
    }

    [Fact]
    public void Row_And_Column_Changes_Shift_Later_Tables_Shapes_And_Sections()
    {
        var first = new DocumentTable(0, 2, [new TableRow([new TableCell(0, 1, 0), new TableCell(1, 1, 1)])], [60, 60]);
        var later = new DocumentTable(3, 1, [new TableRow([new TableCell(3, 1, 0)])], [100]);
        var source = RichTextDocument.FromParagraphs(new[] { "A", "B", "Between", "Later", "After" }.Select(RichTextParagraph.Plain))
            .WithTables([first, later]).WithShapes([new DocumentShape(1, 0, 0, 20, 20), new DocumentShape(4, 0, 0, 20, 20)])
            .WithSections([new DocumentSection(0, 5, 1, 0)]);
        var at = WriterTables.At(source, RichTextDocument.Start)!;
        var changed = WriterTables.Modify(source, at, "insert-column").Document;
        Assert.Equal("A\n\nB\nBetween\nLater\nAfter", changed.PlainText);
        Assert.Equal(4, changed.Tables[1].ParagraphIndex);
        Assert.Equal(new[] { 2, 5 }, changed.Shapes.Select(shape => shape.ParagraphIndex));
        Assert.Equal(6, changed.Sections[0].ParagraphCount);
        var remove = WriterTables.At(changed, changed.PositionAt(1, 0))!;
        var restored = WriterTables.Modify(changed, remove, "delete-column").Document;
        Assert.Equal(source.PlainText, restored.PlainText);
        Assert.Equal(3, restored.Tables[1].ParagraphIndex);
        Assert.Equal(new[] { 1, 4 }, restored.Shapes.Select(shape => shape.ParagraphIndex));
        Assert.Equal(5, restored.Sections[0].ParagraphCount);
    }

    [Fact]
    public void Partial_Rectangle_Merge_Reorders_Text_And_Object_Anchors_Consistently()
    {
        var paragraphs = new[] { "A", "B", "C", "D", "E", "F", "After" }.Select(RichTextParagraph.Plain);
        var table = new DocumentTable(0, 6, [
            new TableRow([new TableCell(0, 1, 0), new TableCell(1, 1, 1), new TableCell(2, 1, 2)]),
            new TableRow([new TableCell(3, 1, 0), new TableCell(4, 1, 1), new TableCell(5, 1, 2)]),
        ], [60, 60, 60]);
        var source = RichTextDocument.FromParagraphs(paragraphs).WithTables([table])
            .WithShapes([new DocumentShape(3, 0, 0, 15, 15)])
            .WithSections([new DocumentSection(0, 7, 1, 0)]);
        var at = WriterTables.At(source, source.PositionAt(1, 0))!;
        Assert.True(WriterTables.CanEdit(source, at));
        var end = at with { Row = 1, Column = 2 };
        var merged = WriterTables.Modify(source, at, "merge", endpoint: end).Document;
        Assert.Equal("A\nB\nC\nE\nF\nD\nAfter", merged.PlainText);
        Assert.Equal(5, Assert.Single(merged.Shapes).ParagraphIndex);
        var grid = DocumentTableTopologyAdapter.FromTable(merged.Tables[0]);
        var root = grid.GetCellAt(0, 1)!;
        Assert.Same(root, grid.GetCellAt(1, 2));
        Assert.Equal("B\nC\nE\nF", string.Join("\n", merged.Paragraphs.Skip(root.ParagraphIndex).Take(root.ParagraphCount).Select(p => p.Text)));
        using var stream = new MemoryStream(Broiler.Documents.Docx.DocxDocumentCodec.WriteToArray(merged));
        Assert.Null(WriterTables.DescribeFirstSaveChange(merged, new Broiler.Documents.Docx.DocxDocumentCodec().Read(stream).Document));
        var split = WriterTables.Modify(merged, WriterTables.At(merged, merged.PositionAt(root.ParagraphIndex, 0))!, "split").Document;
        Assert.Equal(9, split.Tables[0].ParagraphCount);
        Assert.Equal(10, Assert.Single(split.Sections).ParagraphCount);
        Assert.Equal("D", split.Paragraphs[Assert.Single(split.Shapes).ParagraphIndex].Text);
    }

    [Fact]
    public void Touch_In_Rendered_Cell_Padding_Targets_The_Structural_Cell()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        var table = new DocumentTable(0, 2, [new TableRow([new TableCell(0, 1, 0), new TableCell(1, 1, 1)])],
            [100, 100], cellPadding: 6);
        app.Editor.Document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Left"), RichTextParagraph.Plain("Right"),
        ]).WithTables([table]);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.page-layout"));
        var content = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        var pages = Assert.IsType<Broiler.Documents.Pagination.PaginatedDocument>(preview.Pages);
        app.RenderFrame();
        BRect paper = preview.PageBounds;
        var page = pages.Layout.Pages[0];
        BRect right = page.Cells.OrderBy(cell => cell.Bounds.Left).Last().Bounds;
        var hit = InputPoint.ClientDeviceIndependentPixels(paper.Left + (right.Right - 2) * paper.Width / page.WidthPoints,
            paper.Top + (right.Top + right.Height / 2) * paper.Height / page.HeightPoints);
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("table-touch"), new InputTimestamp(1, 1000, "test"), 1);
        app.Dispatch(UiInputEvent.FromTouchContact(new TouchContactEvent(header, 1, hit, TouchContactState.Pressed)));
        app.Dispatch(UiInputEvent.FromTouchContact(new TouchContactEvent(header, 1, hit, TouchContactState.Released)));
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Go to body").Click();
        Assert.Equal(app.Document.PositionAt(1, 0), app.Editor.Selection.Focus);
    }
}
