using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Documents.Model.Tables;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

internal static class WriterTables
{
    internal sealed record Location(DocumentTable Table, TableTopologyMap<TableCell> Grid, TableCell Cell, int Row, int Column);
    private sealed record Cell(int Column, int Columns, int Rows, TableCell? Style, IReadOnlyList<int?> Paragraphs);

    internal static Location? At(RichTextDocument document, RichTextPosition position)
    {
        int paragraph = document.ParagraphIndexAt(position);
        IReadOnlyList<DocumentTable> candidates = document.Tables;
        Location? result = null;
        while (candidates.FirstOrDefault(table => table.ParagraphIndex <= paragraph && paragraph < table.ParagraphEnd) is { } table)
        {
            var grid = DocumentTableTopologyAdapter.FromTable(table);
            var cell = grid.DistinctCells.FirstOrDefault(item => item.ParagraphIndex <= paragraph && paragraph < item.ParagraphEnd);
            if (cell is null || grid.GetCellRange(cell) is not { } span)
                break;
            result = new(table, grid, cell, span.StartRow, span.StartColumn);
            candidates = cell.Tables;
        }
        return result;
    }

    internal static bool Navigate(UiRichEdit editor, int direction)
    {
        if (At(editor.Document, editor.Selection.Focus) is not { } at)
            return false;
        TableCell[] cells = at.Grid.DistinctCells.Where(cell => cell.ParagraphCount > 0).ToArray();
        int next = Array.IndexOf(cells, at.Cell) + direction;
        if (next < 0 || next == cells.Length)
        {
            int outside = next < 0 ? at.Table.ParagraphIndex - 1 : at.Table.ParagraphEnd;
            if (outside >= 0 && outside < editor.Document.ParagraphCount)
                Focus(editor, outside);
            return true;
        }
        Focus(editor, cells[next].ParagraphIndex);
        return true;
    }

    internal static bool HandleTab(UiRichEdit editor, UiInputEvent input) =>
        input.Kind == UiInputEventKind.KeyboardKey && input.KeyTransition == KeyboardKeyTransition.Down &&
        input.KeyName == "Tab" && (input.KeyModifiers & ~KeyboardModifierState.Shift) == 0 &&
        Navigate(editor, input.KeyModifiers.HasFlag(KeyboardModifierState.Shift) ? -1 : 1);

    internal static void Focus(UiRichEdit editor, int paragraph)
    {
        editor.Selection = RichTextRange.Caret(editor.Document.PositionAt(paragraph, 0));
        if (editor is StandardRichEdit rich)
            rich.RevealCaret();
        editor.Session?.SetFocus(editor);
    }

    internal static bool CanEdit(RichTextDocument document, Location at)
    {
        var table = at.Table;
        return document.Tables.Contains(table) && table.Position is null &&
            at.Grid.DistinctCells.All(cell => cell.ParagraphCount > 0 && cell.Tables.Count == 0 &&
                cell.Formula is null && cell.NumberFormat is null && cell.Value is null &&
                !cell.WrapText && cell.Note is null && cell.Hyperlink is null) &&
            ValidParagraphRanges() &&
            !table.Rows.Any(row => row.IsHidden) &&
            !document.Sections.Any(section => Inside(section.ParagraphIndex) || Inside(section.ParagraphEnd));

        bool Inside(int boundary) => boundary > table.ParagraphIndex && boundary < table.ParagraphEnd;
        bool ValidParagraphRanges()
        {
            int expected = table.ParagraphIndex;
            foreach (TableCell cell in at.Grid.DistinctCells.OrderBy(cell => cell.ParagraphIndex))
            {
                if (cell.ParagraphIndex != expected)
                    return false;
                expected = cell.ParagraphEnd;
            }
            return expected == table.ParagraphEnd && expected <= document.ParagraphCount;
        }
    }

    internal static bool IsRegular(Location at) =>
        at.Grid.DistinctCells.All(cell => cell.RowSpan == 1 && cell.ColumnSpan == 1) &&
        (long)at.Grid.CellCount == (long)at.Grid.RowCount * at.Grid.ColumnCount;

    internal static string? DescribeFirstSaveChange(RichTextDocument original, RichTextDocument reopened)
    {
        if (original.Tables.Count != reopened.Tables.Count)
            return "The generated file changes the number of tables.";
        for (int index = 0; index < original.Tables.Count; index++)
        {
            DocumentTable before = original.Tables[index];
            DocumentTable after = reopened.Tables[index];
            var source = DocumentTableTopologyAdapter.FromTable(before);
            var saved = DocumentTableTopologyAdapter.FromTable(after);
            if (source.RowCount != saved.RowCount || source.ColumnCount != saved.ColumnCount ||
                !Cells(original, source).SequenceEqual(Cells(reopened, saved)))
                return $"The generated file changes the cells or merged spans in table {index + 1}.";
            if (FormattingChanged(before, after))
                return $"The generated file changes cell formatting, table sizing, or row behavior in table {index + 1}.";
        }
        return null;

        static IEnumerable<(int Row, int Column, int Rows, int Columns, string Text)> Cells(
            RichTextDocument document, TableTopologyMap<TableCell> grid)
        {
            foreach (TableCell cell in grid.DistinctCells)
            {
                var span = grid.GetCellRange(cell)!.Value;
                yield return (span.StartRow, span.StartColumn, span.RowSpan, span.ColumnSpan,
                    string.Join("\n", document.Paragraphs.Skip(cell.ParagraphIndex).Take(cell.ParagraphCount).Select(p => p.Text)));
            }
        }
    }

    private static bool FormattingChanged(DocumentTable before, DocumentTable after)
    {
        if (before.Rows.Count != after.Rows.Count || before.Alignment != after.Alignment ||
            !Near(before.WidthPercent is null ? before.PreferredWidth ?? before.TotalWidth : null,
                after.WidthPercent is null ? after.PreferredWidth ?? after.TotalWidth : null) || !Near(before.WidthPercent, after.WidthPercent) ||
            !Near(before.LeftIndent, after.LeftIndent) || before.AutoFit != after.AutoFit ||
            before.KeepTogether != after.KeepTogether || before.ColumnWidths.Count != after.ColumnWidths.Count ||
            before.ColumnWidths.Where((width, index) => !Near(width, after.ColumnWidths[index])).Any())
            return true;
        for (int row = 0; row < before.Rows.Count; row++)
        {
            var a = before.Rows[row];
            var b = after.Rows[row];
            if (a.IsHeader != b.IsHeader || !Near(a.MinHeight, b.MinHeight) || a.ExactHeight != b.ExactHeight ||
                a.CanSplit != b.CanSplit || a.KeepWithNext != b.KeepWithNext)
                return true;
            var roots = a.Cells.Where(cell => !cell.IsRowSpanContinuation).ToArray();
            var saved = b.Cells.Where(cell => !cell.IsRowSpanContinuation).ToArray();
            if (roots.Length != saved.Length)
                return true;
            for (int index = 0; index < roots.Length; index++)
            {
                var cell = roots[index];
                var other = saved[index];
                var padding = cell.PaddingFor(before);
                var restored = other.PaddingFor(after);
                if (cell.ColumnIndex != other.ColumnIndex || cell.ColumnSpan != other.ColumnSpan || cell.RowSpan != other.RowSpan ||
                    cell.Shading != other.Shading || cell.VerticalAlignment != other.VerticalAlignment ||
                    !Near(padding.Left, restored.Left) || !Near(padding.Top, restored.Top) ||
                    !Near(padding.Right, restored.Right) || !Near(padding.Bottom, restored.Bottom) ||
                    !Edge(cell.Borders.Left, other.Borders.Left) || !Edge(cell.Borders.Right, other.Borders.Right) ||
                    !Edge(cell.Borders.Top, other.Borders.Top) || !Edge(cell.Borders.Bottom, other.Borders.Bottom) ||
                    cell.Tables.Count != other.Tables.Count)
                    return true;
                for (int nested = 0; nested < cell.Tables.Count; nested++)
                    if (FormattingChanged(cell.Tables[nested], other.Tables[nested]))
                        return true;
            }
        }
        return false;

        static bool Near(double? a, double? b) => a is null || b is null ? a == b : Math.Abs(a.Value - b.Value) <= .05;
        static bool Edge(TableBorder a, TableBorder b) => !a.IsVisible && !b.IsVisible ||
            a.IsVisible == b.IsVisible && a.Style == b.Style && a.Color == b.Color && Near(a.Width, b.Width);
    }

    internal static (RichTextDocument Document, int Caret) Insert(RichTextDocument document, int after, int rows, int columns)
    {
        int start = after + 1;
        var grid = Enumerable.Range(0, rows)
            .Select(_ => Enumerable.Range(0, columns).Select(EmptyCell).ToList()).ToList();
        var origins = new List<int?>();
        DocumentTable table = BuildTable(start, grid, null, Enumerable.Repeat(60d, columns).ToArray(), origins);
        if (start == document.ParagraphCount)
            origins.Add(null);
        return (Rewrite(document, start, 0, origins, null, table), start);
    }

    internal static (RichTextDocument Document, int Caret) Modify(RichTextDocument document, Location at,
        string command, double size = 0, Location? endpoint = null, bool exactHeight = false)
    {
        DocumentTable table = at.Table;
        var rows = ReadRows(at);
        double[] widths = Enumerable.Range(0, at.Grid.ColumnCount).Select(index =>
            index < table.ColumnWidths.Count && table.ColumnWidths[index] > 0 ? table.ColumnWidths[index] : 60).ToArray();
        var rowStyles = table.Rows.Cast<TableRow?>().ToList();
        int row = at.Row;
        int column = at.Column;
        switch (command)
        {
            case "delete-table":
                return (Rewrite(document, table.ParagraphIndex, table.ParagraphCount, [], table, null), table.ParagraphIndex);
            case "insert-row":
                rows.Insert(++row, Enumerable.Range(0, widths.Length).Select(EmptyCell).ToList());
                rowStyles.Insert(row, row < table.Rows.Count && table.Rows[row - 1].IsHeader && table.Rows[row].IsHeader
                    ? new TableRow([], isHeader: true) : null);
                break;
            case "delete-row":
                rows.RemoveAt(row);
                rowStyles.RemoveAt(row);
                if (rows.Count == 0)
                    return Modify(document, at, "delete-table");
                row = Math.Min(row, rows.Count - 1);
                break;
            case "insert-column":
                column++;
                widths = [.. widths.Take(column), 60, .. widths.Skip(column)];
                foreach (var cells in rows)
                {
                    for (int index = column; index < cells.Count; index++)
                        cells[index] = cells[index] with { Column = index + 1 };
                    cells.Insert(column, EmptyCell(column));
                }
                break;
            case "delete-column":
                if (widths.Length == 1)
                    return Modify(document, at, "delete-table");
                widths = [.. widths.Where((_, index) => index != column)];
                foreach (var cells in rows)
                {
                    cells.RemoveAt(column);
                    for (int index = column; index < cells.Count; index++)
                        cells[index] = cells[index] with { Column = index };
                }
                column = Math.Min(column, widths.Length - 1);
                break;
            case "merge":
                var other = endpoint!;
                int bottom = Math.Max(row, other.Row);
                int right = Math.Max(column, other.Column);
                row = Math.Min(row, other.Row);
                column = Math.Min(column, other.Column);
                var root = rows[row][column];
                var content = new List<int?>();
                for (int index = row; index <= bottom; index++)
                {
                    content.AddRange(rows[index].Skip(column).Take(right - column + 1).SelectMany(cell => cell.Paragraphs));
                    rows[index].RemoveRange(column, right - column + 1);
                }
                rows[row].Add(root with { Rows = bottom - row + 1, Columns = right - column + 1, Paragraphs = content });
                break;
            case "split":
                var merged = rows[row].Single(cell => cell.Column == column);
                rows[row].Remove(merged);
                for (int r = row; r < row + merged.Rows; r++)
                    for (int c = column; c < column + merged.Columns; c++)
                        rows[r].Add(r == row && c == column ? merged with { Rows = 1, Columns = 1 } : EmptyCell(c));
                break;
            case "column-width":
                widths[column] = size;
                break;
            case "row-height":
                TableRow old = table.Rows[row];
                rowStyles[row] = new TableRow(old.Cells, old.IsHeader, size, old.CanSplit, exactHeight, old.KeepWithNext);
                break;
        }

        var origins = new List<int?>();
        var updated = BuildTable(table.ParagraphIndex, rows, table, widths, origins, rowStyles,
            fixedWidths: command == "column-width");
        TableCell? active = DocumentTableTopologyAdapter.FromTable(updated).GetCellAt(row, column);
        return (Rewrite(document, table.ParagraphIndex, table.ParagraphCount, origins, table, updated),
            active?.ParagraphIndex ?? updated.ParagraphIndex);
    }

    private static Cell EmptyCell(int column) => new(column, 1, 1, null, [null]);

    private static List<List<Cell>> ReadRows(Location at) =>
        at.Table.Rows.Select(row => row.Cells.Where(cell => !cell.IsRowSpanContinuation)
            .Select(cell => new Cell(cell.ColumnIndex, cell.ColumnSpan, cell.RowSpan, cell,
                Enumerable.Range(cell.ParagraphIndex, cell.ParagraphCount).Select(index => (int?)index).ToArray()))
            .OrderBy(cell => cell.Column).ToList()).ToList();

    private static DocumentTable BuildTable(int start, List<List<Cell>> rows, DocumentTable? original,
        double[] widths, List<int?> origins, IReadOnlyList<TableRow?>? rowStyles = null, bool fixedWidths = false)
    {
        var built = new List<TableRow>(rows.Count);
        var continuations = Enumerable.Range(0, rows.Count).Select(_ => new List<TableCell>()).ToArray();
        for (int index = 0; index < rows.Count; index++)
        {
            var cells = continuations[index];
            foreach (Cell cell in rows[index].OrderBy(cell => cell.Column))
            {
                TableCell? old = cell.Style;
                var root = new TableCell(start + origins.Count, cell.Paragraphs.Count, cell.Column, cell.Columns, cell.Rows,
                    old?.Shading ?? default, old?.Borders ?? default, padding: old?.Padding,
                    verticalAlignment: old?.VerticalAlignment ?? CellVerticalAlignment.Top);
                cells.Add(root);
                origins.AddRange(cell.Paragraphs);
                for (int row = index + 1; row < index + cell.Rows; row++)
                    continuations[row].Add(new TableCell(root.ParagraphIndex, 0, cell.Column, cell.Columns,
                        shading: root.Shading, borders: root.Borders, isRowSpanContinuation: true, padding: root.Padding,
                        verticalAlignment: root.VerticalAlignment));
            }
            TableRow? style = rowStyles?[index];
            built.Add(new TableRow(cells.OrderBy(cell => cell.ColumnIndex).ToArray(), style?.IsHeader ?? false, style?.MinHeight ?? 0, style?.CanSplit ?? true,
                style?.ExactHeight ?? false, style?.KeepWithNext ?? false));
        }
        return new DocumentTable(start, origins.Count, built,
            !fixedWidths && original is { ColumnWidths.Count: 0 } ? [] : widths,
            original?.CellPadding ?? DocumentTable.DefaultCellPadding,
            keepWithNext: original?.KeepWithNext ?? false, preferredWidth: fixedWidths ? widths.Sum() : original?.PreferredWidth,
            widthPercent: fixedWidths ? null : original?.WidthPercent, alignment: original?.Alignment ?? TableAlignment.Start,
            leftIndent: original?.LeftIndent ?? 0, pageBreakBefore: original?.PageBreakBefore ?? false,
            columnBreakBefore: original?.ColumnBreakBefore ?? false, autoFit: !fixedWidths && original?.AutoFit == true,
            keepTogether: original?.KeepTogether ?? false, name: original?.Name, spacingBefore: original?.SpacingBefore ?? 0,
            spacingAfter: original?.SpacingAfter ?? 0);
    }

    private static RichTextDocument Rewrite(RichTextDocument original, int start, int count, IReadOnlyList<int?> origins,
        DocumentTable? oldTable, DocumentTable? newTable)
    {
        int end = start + count;
        int delta = origins.Count - count;
        var positions = new Dictionary<int, int>();
        for (int index = 0; index < origins.Count; index++)
            if (origins[index] is int source)
                positions.Add(source, start + index);
        var body = original.Paragraphs.Take(start)
            .Concat(origins.Select(index => index is int source ? original.Paragraphs[source] : RichTextParagraph.Empty))
            .Concat(original.Paragraphs.Skip(end));
        var tables = original.Tables.Where(table => !ReferenceEquals(table, oldTable))
            .Select(table => table.ParagraphIndex >= end ? table.ShiftedBy(delta) : table);
        if (newTable is not null)
            tables = tables.Append(newTable);
        var shapes = original.Shapes.Where(shape => shape.IsPageAnchored || shape.ParagraphIndex < start ||
                shape.ParagraphIndex >= end || positions.ContainsKey(shape.ParagraphIndex))
            .Select(shape => shape.IsPageAnchored || shape.ParagraphIndex < start ? shape :
                shape.WithParagraphIndex(shape.ParagraphIndex >= end ? shape.ParagraphIndex + delta : positions[shape.ParagraphIndex]));
        var sections = original.Sections.Select(section =>
        {
            bool ownsReplacement = section.ParagraphIndex <= start && section.ParagraphEnd >= end &&
                (count > 0 || section.ParagraphIndex < start);
            if (!ownsReplacement)
                return section.ParagraphIndex >= end ? section.ShiftedBy(delta) : section;
            int length = section.ParagraphCount + delta;
            if (length <= 0)
                return null;
            return new DocumentSection(section.ParagraphIndex, length, section.ColumnCount, section.ColumnGap, section.Page,
                section.RunningContent, section.Background, section.PageStyles, section.Hidden, section.DontBalanceColumns,
                section.BreakType, section.LeftIndent, section.RightIndent, section.PageNumberStart)
            {
                PageNumberFormat = section.PageNumberFormat, FootnotesEnding = section.FootnotesEnding, ColumnSeparator = section.ColumnSeparator,
            };
        }).OfType<DocumentSection>();
        return RichTextDocument.FromParagraphs(body).WithTables(tables.OrderBy(table => table.ParagraphIndex).ToArray())
            .WithShapes(shapes.ToArray()).WithSections(sections.ToArray()).WithPageGeometry(original.PageGeometry)
            .WithRunningContent(original.RunningContent).WithPageBackground(original.PageBackground)
            .WithStyleDefaults(original.StyleDefaults).WithNoteNumbering(original.NoteNumbering).WithShowsRevisions(original.ShowsRevisions);
    }
}
