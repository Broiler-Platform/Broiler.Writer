using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;

namespace Broiler.Writer;

internal static class WriterTableFormatting
{
    internal static RichTextDocument Update(RichTextDocument document, DocumentTable target,
        Func<DocumentTable, DocumentTable> update)
    {
        return document.WithTables(document.Tables.Select(Visit).ToArray());

        DocumentTable Visit(DocumentTable table)
        {
            if (ReferenceEquals(table, target))
                return update(table);
            bool changed = false;
            var rows = table.Rows.Select(row =>
            {
                var cells = row.Cells.Select(cell =>
                {
                    var nested = cell.Tables.Select(Visit).ToArray();
                    if (nested.SequenceEqual(cell.Tables))
                        return cell;
                    changed = true;
                    return CopyCell(cell, nested: nested);
                }).ToArray();
                return cells.SequenceEqual(row.Cells) ? row : CopyRow(row, cells);
            }).ToArray();
            return changed ? CopyTable(table, rows) : table;
        }
    }

    internal static DocumentTable CopyTable(DocumentTable source, IReadOnlyList<TableRow>? rows = null,
        double? preferredWidth = null, double? widthPercent = null, bool setWidth = false,
        TableAlignment? alignment = null, bool? autoFit = null, double? leftIndent = null,
        bool? keepTogether = null, IReadOnlyList<double>? widths = null) =>
        new(source.ParagraphIndex, source.ParagraphCount, rows ?? source.Rows, widths ?? source.ColumnWidths,
            source.CellPadding, source.Position, source.KeepWithNext,
            setWidth ? preferredWidth : source.PreferredWidth, setWidth ? widthPercent : source.WidthPercent,
            alignment ?? source.Alignment, leftIndent ?? source.LeftIndent,
            source.PageBreakBefore, source.ColumnBreakBefore, autoFit ?? source.AutoFit,
            keepTogether ?? source.KeepTogether, source.Name, source.SpacingBefore, source.SpacingAfter)
        {
            ShowGridlines = source.ShowGridlines, FrozenRows = source.FrozenRows, FrozenColumns = source.FrozenColumns,
            SplitRows = source.SplitRows, SplitColumns = source.SplitColumns, ConditionalFormats = source.ConditionalFormats,
            SummaryRowsBelow = source.SummaryRowsBelow, SummaryColumnsRight = source.SummaryColumnsRight, Columns = source.Columns,
        };

    internal static TableRow CopyRow(TableRow row, IReadOnlyList<TableCell>? cells = null, bool? header = null,
        bool? canSplit = null, bool? keepWithNext = null, double? height = null, bool? exactHeight = null) =>
        new(cells ?? row.Cells, header ?? row.IsHeader, height ?? row.MinHeight, canSplit ?? row.CanSplit,
            exactHeight ?? row.ExactHeight, keepWithNext ?? row.KeepWithNext, row.IsHidden, row.OutlineLevel, row.IsCollapsed);

    internal static TableCell CopyCell(TableCell cell, IReadOnlyList<DocumentTable>? nested = null,
        TableCellPadding? padding = null, bool setPadding = false, CellVerticalAlignment? alignment = null,
        Broiler.Graphics.Color.BColor? shading = null, CellBorders? borders = null) =>
        new(cell.ParagraphIndex, cell.ParagraphCount, cell.ColumnIndex, cell.ColumnSpan, cell.RowSpan,
            shading ?? cell.Shading, borders ?? cell.Borders, cell.IsRowSpanContinuation, nested ?? cell.Tables,
            setPadding ? padding : cell.Padding, alignment ?? cell.VerticalAlignment, cell.Formula, cell.NumberFormat,
            cell.Value, cell.WrapText, cell.Note, cell.Hyperlink);

    internal static HashSet<TableCell> SelectedCells(RichTextDocument document, RichTextRange selection, WriterTables.Location at)
    {
        var anchor = WriterTables.At(document, selection.Anchor);
        if (selection.IsEmpty || !ReferenceEquals(anchor?.Table, at.Table))
            return [at.Cell];
        var first = at.Grid.GetCellRange(anchor!.Cell)!.Value;
        var last = at.Grid.GetCellRange(at.Cell)!.Value;
        int top = Math.Min(first.StartRow, last.StartRow);
        int bottom = Math.Max(first.StartRow + first.RowSpan, last.StartRow + last.RowSpan);
        int left = Math.Min(first.StartColumn, last.StartColumn);
        int right = Math.Max(first.StartColumn + first.ColumnSpan, last.StartColumn + last.ColumnSpan);
        return at.Grid.DistinctCells.Where(cell => at.Grid.GetCellRange(cell) is { } span &&
            span.StartRow < bottom && span.StartRow + span.RowSpan > top &&
            span.StartColumn < right && span.StartColumn + span.ColumnSpan > left).ToHashSet();
    }

    internal static DocumentTable FormatCells(DocumentTable table, HashSet<TableCell> cells, Func<TableCell, TableCell> update)
    {
        var replacements = cells.ToDictionary(cell => cell, update);
        return CopyTable(table, table.Rows.Select(row => CopyRow(row, row.Cells.Select(cell =>
        {
            if (replacements.TryGetValue(cell, out var replacement))
                return replacement;
            if (cell.IsRowSpanContinuation)
            {
                var root = cells.FirstOrDefault(root => root.ColumnIndex == cell.ColumnIndex &&
                    root.ParagraphIndex == cell.ParagraphIndex);
                if (root is not null)
                {
                    var style = replacements[root];
                    return CopyCell(cell, padding: style.Padding, setPadding: true, alignment: style.VerticalAlignment,
                        shading: style.Shading, borders: style.Borders);
                }
            }
            return cell;
        }).ToArray())).ToArray());
    }

}
