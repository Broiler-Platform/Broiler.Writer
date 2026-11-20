using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterTableFormattingTests
{
    [Fact]
    public void Properties_Apply_To_Selected_Cells_With_Undo_And_Docx_Odt_Roundtrip()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        var table = new DocumentTable(0, 4,
            [new TableRow([new TableCell(0, 1, 0), new TableCell(1, 1, 1)]),
             new TableRow([new TableCell(2, 1, 0), new TableCell(3, 1, 1)])], [100, 100]);
        app.Editor.Document = RichTextDocument.FromPlainText("A\nB\nC\nD").WithTables([table]);
        app.Editor.Selection = new(app.Document.PositionAt(0, 0), app.Document.PositionAt(1, 1));
        var original = app.Document;
        Submit(app, "cell-properties", "5, 6, 7, 8", "bottom", "#123456");
        Submit(app, "cell-borders", "all", "double", "2", "#654321");
        var formatted = app.Document;
        foreach (var cell in formatted.Tables[0].Rows[0].Cells)
        {
            Assert.Equal(new TableCellPadding(5, 6, 7, 8), cell.Padding);
            Assert.Equal(CellVerticalAlignment.Bottom, cell.VerticalAlignment);
            Assert.Equal(new BColor(0x12, 0x34, 0x56), cell.Shading);
            Assert.Equal(TableBorderStyle.Double, cell.Borders.Left.Style);
            Assert.Equal(2, cell.Borders.Right.Width);
        }
        Assert.Null(formatted.Tables[0].Rows[1].Cells[0].Padding);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("edit.undo"));
        Assert.Equal(TableBorder.None, app.Document.Tables[0].Rows[0].Cells[0].Borders.Left);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("edit.undo"));
        Assert.Same(original, app.Document);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("edit.redo"));
        Assert.True(app.Menu.CommandDispatcher.TryExecute("edit.redo"));
        Submit(app, "properties", "percent, 75", "center", "yes", "12", "yes");
        Submit(app, "row-properties", "1", "no", "yes");
        foreach (var codec in new Broiler.Documents.DocumentCodec[]
                 { new Broiler.Documents.Docx.DocxDocumentCodec(), new Broiler.Documents.Odt.OdtDocumentCodec() })
        {
            using var stream = new MemoryStream();
            codec.Write(app.Document, stream);
            stream.Position = 0;
            var restored = codec.Read(stream).Document;
            Assert.Equal(app.Document.PlainText, restored.PlainText);
            var reopened = Assert.Single(restored.Tables);
            Assert.Equal(75, reopened.WidthPercent);
            Assert.Equal(TableAlignment.Center, reopened.Alignment);
            if (codec is Broiler.Documents.Docx.DocxDocumentCodec)
                Assert.True(reopened.Rows[0].IsHeader);
            else if (!reopened.Rows[0].IsHeader)
                Assert.Contains("row behavior", WriterTables.DescribeFirstSaveChange(app.Document, restored));
            Assert.False(reopened.Rows[1].IsHeader);
            Assert.False(reopened.Rows[0].CanSplit);
            var cell = reopened.Rows[0].Cells[0];
            Assert.Equal(new TableCellPadding(5, 6, 7, 8), cell.Padding);
            Assert.Equal(CellVerticalAlignment.Bottom, cell.VerticalAlignment);
            Assert.Equal(TableBorderStyle.Double, cell.Borders.Top.Style);
            Assert.Equal(new BColor(0x12, 0x34, 0x56), cell.Shading);
        }
    }

    [Fact]
    public void Nested_Sizing_And_Formatting_Preserve_Parent_And_Text()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        var nested = new DocumentTable(1, 1, [new TableRow([new TableCell(1, 1, 0)])], [50]);
        var parent = new DocumentTable(0, 3, [new TableRow([new TableCell(0, 3, 0, tables: [nested])])], [200]);
        app.Editor.Document = RichTextDocument.FromPlainText("Before\nInside\nAfter").WithTables([parent]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        var original = app.Document;
        Submit(app, "properties", "points, 80", "end", "no", "4", "no");
        Submit(app, "column-width", "70");
        Submit(app, "row-height", "18", "exact");
        Submit(app, "cell-properties", "3, 4, 5, 6", "center", "none");
        var changed = Assert.Single(app.Document.Tables[0].Rows[0].Cells[0].Tables);
        Assert.Equal(80, changed.PreferredWidth);
        Assert.Equal(70, Assert.Single(changed.ColumnWidths));
        Assert.Equal(18, changed.Rows[0].MinHeight);
        Assert.True(changed.Rows[0].ExactHeight);
        Assert.Equal(CellVerticalAlignment.Center, changed.Rows[0].Cells[0].VerticalAlignment);
        Assert.Equal(parent.ColumnWidths, app.Document.Tables[0].ColumnWidths);
        Assert.Equal(original.PlainText, app.Document.PlainText);
        for (int index = 0; index < 4; index++)
            Assert.True(app.Menu.CommandDispatcher!.TryExecute("edit.undo"));
        Assert.Same(original, app.Document);
    }

    [Fact]
    public void Header_Control_Repeats_In_Page_Layout_And_Print()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        const int count = 30;
        app.Editor.Document = RichTextDocument.FromParagraphs(Enumerable.Range(0, count)
                .Select(index => RichTextParagraph.Plain(index == 0 ? "Repeated heading" : "Body row " + index)))
            .WithPageGeometry(PageGeometry.A4)
            .WithTables([new DocumentTable(0, count, Enumerable.Range(0, count)
                .Select(index => new TableRow([new TableCell(index, 1, 0)], minHeight: 60)).ToArray(), [300])]);
        Submit(app, "row-properties", "1", "no", "no");
        using var print = WriterPageLayout.CreatePaginator(app.Editor.Font.FamilyName).Paginate(app.Document,
            Broiler.Documents.Pagination.DocumentPaginator.PreferredSetup(app.Document), TestContext.Current.CancellationToken);
        Assert.True(print.PageCount > 1);
        for (int index = 0; index < print.PageCount; index++)
            Assert.Contains("Repeated heading", string.Concat(print.GetPage(index).Commands.OfType<BRenderCommand.DrawText>()
                .Select(command => command.Text.Text)));
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.page-layout"));
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var preview = Assert.Single(Assert.Single(dialog.Children).Children.OfType<Broiler.UI.PrintPreview.Standard.StandardPrintPreview>());
        Assert.Equal(print.PageCount, preview.PageCount);
    }

    [Fact]
    public void Formatting_Save_Review_Reports_Lost_Cell_And_Nested_Settings()
    {
        var nested = new DocumentTable(0, 1, [new TableRow([new TableCell(0, 1, 0)])], [50]);
        var outer = new DocumentTable(0, 1, [new TableRow([new TableCell(0, 1, 0, tables: [nested])])], [100]);
        var original = RichTextDocument.FromPlainText("Cell").WithTables([outer]);
        var changed = WriterTableFormatting.Update(original, nested, table => WriterTableFormatting.CopyTable(table, alignment: TableAlignment.End));
        Assert.Contains("formatting", WriterTables.DescribeFirstSaveChange(original, changed));
        var padded = WriterTableFormatting.Update(original, outer, table => WriterTableFormatting.FormatCells(table,
            [table.Rows[0].Cells[0]], cell => WriterTableFormatting.CopyCell(cell, padding: new(10, 20, 30, 40), setPadding: true)));
        Assert.Contains("formatting", WriterTables.DescribeFirstSaveChange(original, padded));
        Assert.Null(WriterTables.DescribeFirstSaveChange(original, original));
    }

    [Fact]
    public void Structural_Edits_Preserve_Header_Groups_And_Reject_Merges_Into_Body_Rows()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromPlainText("Header one\nHeader two\nBody").WithTables([
            new DocumentTable(0, 3, [
                new TableRow([new TableCell(0, 1, 0, padding: new(5, 6, 7, 8))], isHeader: true, minHeight: 30, exactHeight: true),
                new TableRow([new TableCell(1, 1, 0)], isHeader: true),
                new TableRow([new TableCell(2, 1, 0)])], [100], alignment: TableAlignment.Center)]);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("table.insert-row"));
        var table = Assert.Single(app.Document.Tables);
        Assert.Equal(new[] { true, true, true, false }, table.Rows.Select(row => row.IsHeader));
        Assert.Equal(TableAlignment.Center, table.Alignment);
        Assert.True(table.Rows[0].ExactHeight);
        Assert.Equal(new TableCellPadding(5, 6, 7, 8), table.Rows[0].Cells[0].Padding);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(2, 0));
        var before = app.Document;
        Assert.True(app.Menu.CommandDispatcher.TryExecute("table.merge"));
        Fill(app, "2", "1");
        Assert.NotNull(app.Session.ModalElement);
        Assert.Same(before, app.Document);
    }

    [Fact]
    public void Repeated_Header_Boundary_Cannot_Cut_A_Rowspan()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = RichTextDocument.FromPlainText("Merged\nRight\nBelow").WithTables([
            new DocumentTable(0, 3, [new TableRow([new TableCell(0, 1, 0, rowSpan: 2), new TableCell(1, 1, 1)]),
                new TableRow([new TableCell(2, 1, 1)])], [100, 100])]);
        var original = app.Document;
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("table.row-properties"));
        Fill(app, "1", "yes", "no");
        Assert.NotNull(app.Session.ModalElement);
        Assert.Same(original, app.Document);
        Fill(app, "2", "yes", "no");
        Assert.Null(app.Session.ModalElement);
        Assert.All(app.Document.Tables[0].Rows, row => Assert.True(row.IsHeader));
    }

    private static void Submit(WriterApp app, string command, params string[] values)
    {
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("table." + command));
        Fill(app, values);
        Assert.Null(app.Session.ModalElement);
    }

    private static void Fill(WriterApp app, params string[] values)
    {
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var children = Assert.Single(dialog.Children).Children;
        var inputs = children.OfType<StandardEdit>().ToArray();
        Assert.Equal(values.Length, inputs.Length);
        for (int index = 0; index < values.Length; index++)
            inputs[index].Text = values[index];
        Assert.Single(children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
    }
}
