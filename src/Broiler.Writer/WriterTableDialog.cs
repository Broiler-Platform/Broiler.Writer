using System;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI.Menu;
using Broiler.UI.RichEdit;
using Broiler.UI.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    internal static UiMenuItem CreateTableMenu(StandardCommandDispatcher dispatcher, UiRichEdit editor,
        Func<UiWindow> owner, Func<BSize> viewport, Action<Action> dispatch)
    {
        var menu = new UiMenuItem("table", "Table") { AccessKey = 'T' };
        Add("insert", "Insert table...", ShowInsert, () => Editable() && Current() is null);
        Add("delete-table", "Delete table", () => Modify("delete-table"), CanEdit);
        menu.Children.Add(new UiMenuItem("rows-separator", "") { IsSeparator = true });
        Add("insert-row", "Insert row below", () => Modify("insert-row"), Regular);
        Add("delete-row", "Delete row", () => Modify("delete-row"), Regular);
        menu.Children.Add(new UiMenuItem("columns-separator", "") { IsSeparator = true });
        Add("insert-column", "Insert column to the right", () => Modify("insert-column"), Regular);
        Add("delete-column", "Delete column", () => Modify("delete-column"), Regular);
        menu.Children.Add(new UiMenuItem("navigation-separator", "") { IsSeparator = true });
        Add("previous-cell", "Previous cell", () => WriterTables.Navigate(editor, -1), HasCell);
        Add("next-cell", "Next cell", () => WriterTables.Navigate(editor, 1), HasCell);
        menu.Children.Add(new UiMenuItem("selection-separator", "") { IsSeparator = true });
        Add("select-cell", "Select cell content", () => Select("cell"), HasCell);
        Add("select-row", "Select row content", () => Select("row"), HasCell);
        Add("select-table", "Select table content", () => Select("table"), HasCell);
        menu.Children.Add(new UiMenuItem("merge-separator", "") { IsSeparator = true });
        Add("merge", "Merge cells...", ShowMerge, Regular);
        Add("split", "Split merged cell", () => Modify("split"), () => CanEdit() &&
            Current() is { Cell: { } cell } && (cell.ColumnSpan > 1 || cell.RowSpan > 1));
        menu.Children.Add(new UiMenuItem("size-separator", "") { IsSeparator = true });
        Add("column-width", "Column width...", () => ShowSize("column-width", "Column width", 60), () => Editable() && HasCell());
        Add("row-height", "Row height...", () => ShowSize("row-height", "Row height", 0), () => Editable() && HasCell());
        menu.Children.Add(new UiMenuItem("properties-separator", "") { IsSeparator = true });
        Add("cell-properties", "Cell properties...", () => ShowCellProperties(owner(), viewport(), editor), () => Editable() && HasCell());
        Add("cell-borders", "Cell borders...", () => ShowCellBorders(owner(), viewport(), editor), () => Editable() && HasCell());
        Add("properties", "Table properties...", () => ShowTableProperties(owner(), viewport(), editor), () => Editable() && HasCell());
        Add("row-properties", "Row properties...", () => ShowTableRowProperties(owner(), viewport(), editor), () => Editable() && HasCell());
        return menu;

        bool Editable() => editor.IsEnabled && !editor.IsReadOnly;
        WriterTables.Location? Current() => WriterTables.At(editor.Document, editor.Selection.Focus);
        bool HasCell() => Current() is not null;
        bool CanEdit() => Editable() && Current() is { } at && WriterTables.CanEdit(editor.Document, at);
        bool Regular() => CanEdit() && Current() is { } at && WriterTables.IsRegular(at);

        void Add(string key, string label, Action action, Func<bool> enabled)
        {
            string command = "table." + key;
            menu.Children.Add(new UiMenuItem(command, label) { CommandName = command });
            dispatcher.Add(new StandardCommand(command, () => dispatch(action), enabled));
        }

        void ShowInsert() => PageForm(owner(), viewport(), editor, "Insert table", "The table starts after the current paragraph.",
            [("Rows", "2"), ("Columns", "2")], (document, selection, values) =>
            {
                if (!int.TryParse(values[0], out int rows) || !int.TryParse(values[1], out int columns) ||
                    rows is < 1 or > 100 || columns is < 1 or > 32)
                    return "Enter 1–100 rows and 1–32 columns.";
                if (WriterTables.At(document, selection.Focus) is not null)
                    return "Place the caret outside a table.";
                Apply(document, WriterTables.Insert(document, document.ParagraphIndexAt(selection.Focus), rows, columns));
                return null;
            });

        void Select(string kind)
        {
            if (Current() is not { } at)
                return;
            RichTextDocument document = editor.Document;
            var cells = kind switch
            {
                "table" => at.Grid.DistinctCells,
                "row" => at.Grid.DistinctCells.Where(cell => at.Grid.GetCellRange(cell) is { } range &&
                    range.StartRow <= at.Row && range.StartRow + range.RowSpan > at.Row).ToArray(),
                _ => new[] { at.Cell },
            };
            int first = cells.Min(cell => cell.ParagraphIndex);
            int last = cells.Max(cell => cell.ParagraphEnd) - 1;
            editor.Selection = new RichTextRange(document.PositionAt(first, 0),
                document.ParagraphEnd(document.PositionAt(last, 0)));
            editor.Session?.SetFocus(editor);
        }

        void ShowMerge()
        {
            if (Current() is not { } at)
                return;
            var anchor = WriterTables.At(editor.Document, editor.Selection.Anchor);
            int row = ReferenceEquals(anchor?.Table, at.Table) ? Math.Min(anchor!.Row, at.Row) : at.Row;
            int col = ReferenceEquals(anchor?.Table, at.Table) ? Math.Min(anchor!.Column, at.Column) : at.Column;
            int rows = ReferenceEquals(anchor?.Table, at.Table) ? Math.Abs(anchor!.Row - at.Row) + 1 : 1;
            int cols = ReferenceEquals(anchor?.Table, at.Table) ? Math.Abs(anchor!.Column - at.Column) + 1 : Math.Min(2, at.Grid.ColumnCount - col);
            PageForm(owner(), viewport(), editor, "Merge cells", "The merge grows down and right from the selection's first row and column.",
                [("Rows to merge", rows.ToString(CultureInfo.InvariantCulture)), ("Columns to merge", cols.ToString(CultureInfo.InvariantCulture))],
                (document, selection, values) =>
                {
                    if (!int.TryParse(values[0], out int height) || !int.TryParse(values[1], out int width) ||
                        height < 1 || height > at.Grid.RowCount - row || width < 1 || width > at.Grid.ColumnCount - col ||
                        height == 1 && width == 1)
                        return "Choose a rectangle of two or more cells within the table.";
                    if (at.Table.Rows.Skip(row).Take(height).Select(item => item.IsHeader).Distinct().Count() > 1)
                        return "A vertical merge cannot cross the repeated-header boundary.";
                    var first = at with { Cell = at.Grid.GetCellAt(row, col)!, Row = row, Column = col };
                    var last = at with { Row = row + height - 1, Column = col + width - 1 };
                    Apply(document, WriterTables.Modify(document, first, "merge", endpoint: last));
                    return null;
                });
        }

        void ShowSize(string command, string title, double fallback)
        {
            if (Current() is not { } at)
                return;
            bool isRow = command == "row-height";
            double current = isRow ? at.Table.Rows[at.Row].MinHeight
                : at.Column < at.Table.ColumnWidths.Count ? at.Table.ColumnWidths[at.Column] : fallback;
            (string, string)[] fields = [(title + " in points", current.ToString(CultureInfo.InvariantCulture))];
            if (isRow)
                fields = [.. fields, ("Rule (minimum or exact)", at.Table.Rows[at.Row].ExactHeight ? "exact" : "minimum")];
            PageForm(owner(), viewport(), editor, title, "Enter a size in points for the current row or column.",
                fields, (document, selection, values) =>
                {
                    if (!double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ||
                        !double.IsFinite(size) || size < (isRow ? 0 : 1) || size > 2000)
                        return "Enter a valid size up to 2000 points.";
                    string rule = isRow ? values[1].ToLowerInvariant() : "minimum";
                    if (rule is not ("minimum" or "exact") || rule == "exact" && size == 0)
                        return "Choose minimum or exact. An exact height must be positive.";
                    var updated = WriterTableFormatting.Update(document, at.Table, table =>
                        isRow ? WriterTableFormatting.CopyTable(table, table.Rows.Select((row, index) => index == at.Row
                            ? WriterTableFormatting.CopyRow(row, height: size, exactHeight: rule == "exact") : row).ToArray())
                        : WriterTableFormatting.CopyTable(table, widths: Enumerable.Range(0, at.Grid.ColumnCount)
                            .Select(index => index == at.Column ? size : index < table.ColumnWidths.Count ? table.ColumnWidths[index] : 60).ToArray()));
                    editor.ApplyDocumentEdit(document, updated, selection);
                    return null;
                });
        }

        void Modify(string command)
        {
            var document = editor.Document;
            if (CanEdit() && Current() is { } at)
                Apply(document, WriterTables.Modify(document, at, command));
        }

        void Apply(RichTextDocument original, (RichTextDocument Document, int Caret) edit) =>
            editor.ApplyDocumentEdit(original, edit.Document, RichTextRange.Caret(edit.Document.PositionAt(edit.Caret, 0)));
    }
}
