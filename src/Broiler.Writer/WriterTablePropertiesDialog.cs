using System;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI.RichEdit;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    private static void ShowCellProperties(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterTables.At(editor.Document, editor.Selection.Focus) is not { } at)
            return;
        PageForm(owner, viewport, editor, "Cell properties", "Blank fields keep the selected cells' current values.",
            [("Padding left, top, right, bottom in points (or inherit)", ""),
             ("Vertical alignment (top, center, bottom)", ""), ("Shading (#RRGGBB or none)", "")],
            (document, selection, values) =>
            {
                bool setPadding = values[0].Length > 0;
                TableCellPadding? padding = null;
                if (setPadding && !values[0].Equals("inherit", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Numbers(values[0], 4, out double[] points) || points.Any(value => value > 200))
                        return "Enter four padding values from 0 to 200 points, or inherit.";
                    padding = new TableCellPadding(points[0], points[1], points[2], points[3]);
                }
                CellVerticalAlignment? alignment = null;
                if (values[1].Length > 0)
                {
                    if (!Enum.TryParse(values[1], true, out CellVerticalAlignment parsed) || !Enum.IsDefined(parsed))
                        return "Choose top, center, or bottom.";
                    alignment = parsed;
                }
                BColor? shading = null;
                if (values[2].Length > 0)
                {
                    if (!TryTableColor(values[2], out BColor color))
                        return "Enter a six-digit RGB color or none.";
                    shading = color;
                }
                if (!setPadding && alignment is null && shading is null)
                    return null;
                var cells = WriterTableFormatting.SelectedCells(document, selection, at);
                var updated = WriterTableFormatting.Update(document, at.Table, table =>
                    WriterTableFormatting.FormatCells(table, cells, cell => WriterTableFormatting.CopyCell(cell,
                        padding: padding, setPadding: setPadding, alignment: alignment, shading: shading)));
                editor.ApplyDocumentEdit(document, updated, selection);
                return null;
            });
    }

    private static void ShowCellBorders(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterTables.At(editor.Document, editor.Selection.Focus) is not { } at)
            return;
        PageForm(owner, viewport, editor, "Cell borders", "Apply an edge style to the selected cells.",
            [("Edge (all, left, top, right, bottom)", "all"),
             ("Style (none, solid, dashed, dotted, double, groove, ridge, inset, outset)", "solid"),
             ("Width in points", "0.5"), ("Color (#RRGGBB)", "#000000")],
            (document, selection, values) =>
            {
                string edge = values[0].ToLowerInvariant();
                if (edge is not ("all" or "left" or "top" or "right" or "bottom") ||
                    !Enum.TryParse(values[1], true, out TableBorderStyle style) || !Enum.IsDefined(style))
                    return "Choose a valid edge and border style.";
                if (!double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
                    !double.IsFinite(width) || width < 0 || width > 20 || style != TableBorderStyle.None && width == 0 ||
                    !WriterCharacterFormatting.TryParseColor(values[3], out BColor color))
                    return "Enter a width up to 20 points and a six-digit RGB color.";
                var border = style == TableBorderStyle.None ? TableBorder.None : new TableBorder(color, width, style);
                var cells = WriterTableFormatting.SelectedCells(document, selection, at);
                var updated = WriterTableFormatting.Update(document, at.Table, table =>
                    WriterTableFormatting.FormatCells(table, cells, cell =>
                    {
                        CellBorders borders = edge switch
                        {
                            "left" => cell.Borders with { Left = border },
                            "right" => cell.Borders with { Right = border },
                            "top" => cell.Borders with { Top = border },
                            "bottom" => cell.Borders with { Bottom = border },
                            _ => cell.Borders with { Left = border, Right = border, Top = border, Bottom = border },
                        };
                        return WriterTableFormatting.CopyCell(cell, borders: borders);
                    }));
                editor.ApplyDocumentEdit(document, updated, selection);
                return null;
            });
    }

    private static void ShowTableProperties(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterTables.At(editor.Document, editor.Selection.Focus) is not { } at)
            return;
        var table = at.Table;
        string width = table.WidthPercent is { } percent ? "percent, " + PointsText(percent)
            : table.PreferredWidth is { } points ? "points, " + PointsText(points) : "auto";
        PageForm(owner, viewport, editor, "Table properties", "Settings apply to the current table, including nested tables.",
            [("Preferred width (auto, points plus value, or percent plus value)", width),
             ("Alignment (start, center, end)", table.Alignment.ToString()),
             ("Autofit contents (yes or no)", table.AutoFit ? "yes" : "no"),
             ("Left indent in points", PointsText(table.LeftIndent)),
             ("Keep table together (yes or no)", table.KeepTogether ? "yes" : "no")],
            (document, selection, values) =>
            {
                double? preferred = null, percentage = null;
                string[] parts = values[0].Split(',', StringSplitOptions.TrimEntries);
                if (!values[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) ||
                        !double.IsFinite(amount) || amount <= 0 ||
                        parts[0].ToLowerInvariant() is not ("points" or "percent") ||
                        amount > (parts[0].Equals("percent", StringComparison.OrdinalIgnoreCase) ? 100 : 2000))
                        return "Use auto, points, 300 or percent, 80.";
                    if (parts[0].Equals("percent", StringComparison.OrdinalIgnoreCase))
                        percentage = amount;
                    else
                        preferred = amount;
                }
                if (!Enum.TryParse(values[1], true, out TableAlignment alignment) || !Enum.IsDefined(alignment) ||
                    !Flag(values[2], out bool? autoFit) || autoFit is null ||
                    !Numbers(values[3], 1, out double[] indent) || indent[0] > 1000 ||
                    !Flag(values[4], out bool? keep) || keep is null)
                    return "Check the alignment, indent, autofit and keep settings.";
                var updated = WriterTableFormatting.Update(document, table, current => WriterTableFormatting.CopyTable(current,
                    preferredWidth: preferred, widthPercent: percentage, setWidth: true, alignment: alignment,
                    autoFit: autoFit, leftIndent: indent[0], keepTogether: keep));
                editor.ApplyDocumentEdit(document, updated, selection);
                return null;
            });
    }

    private static void ShowTableRowProperties(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterTables.At(editor.Document, editor.Selection.Focus) is not { } at)
            return;
        var row = at.Table.Rows[at.Row];
        int headers = at.Table.Rows.TakeWhile(item => item.IsHeader).Count();
        PageForm(owner, viewport, editor, "Row properties", "Header rows repeat when the table continues onto another page.",
            [("Leading header rows to repeat", headers.ToString(CultureInfo.InvariantCulture)),
             ("Allow row to split across pages (yes or no)", row.CanSplit ? "yes" : "no"),
             ("Keep with next row (yes or no)", row.KeepWithNext ? "yes" : "no")],
            (document, selection, values) =>
            {
                if (!int.TryParse(values[0], out int count) || count < 0 || count > at.Table.Rows.Count ||
                    !Flag(values[1], out bool? split) || split is null ||
                    !Flag(values[2], out bool? keep) || keep is null)
                    return "Choose a valid header count and yes or no for row settings.";
                if (at.Grid.DistinctCells.Any(cell => at.Grid.GetCellRange(cell) is { } span &&
                    span.StartRow < count && span.StartRow + span.RowSpan > count))
                    return "Place the header boundary outside vertically merged cells.";
                var updated = WriterTableFormatting.Update(document, at.Table, table =>
                    WriterTableFormatting.CopyTable(table, table.Rows.Select((item, index) =>
                        WriterTableFormatting.CopyRow(item, header: index < count, canSplit: index == at.Row ? split : null,
                            keepWithNext: index == at.Row ? keep : null)).ToArray()));
                editor.ApplyDocumentEdit(document, updated, selection);
                return null;
            });
    }

    private static bool TryTableColor(string text, out BColor color)
    {
        color = BColor.Empty;
        return text.Equals("none", StringComparison.OrdinalIgnoreCase) || WriterCharacterFormatting.TryParseColor(text, out color);
    }
}
