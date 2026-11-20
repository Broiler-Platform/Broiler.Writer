using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.ComboBox;
using Broiler.UI.ComboBox.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.Menu;
using Broiler.UI.RichEdit;
using Broiler.UI.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    internal static void AddParagraphCommands(StandardCommandDispatcher dispatcher, UiMenuItem paragraph,
        UiRichEdit editor, Func<UiWindow> owner, Func<BSize> viewport, Action<Action> dispatch)
    {
        Add("properties", "Properties...", () => ShowParagraphProperties(owner(), viewport(), editor));
        Add("tabs", "Tab stops...", () => ShowTabStops(owner(), viewport(), editor));
        Add("numbering", "List properties...", () => ShowListProperties(owner(), viewport(), editor));
        paragraph.Children.Add(new UiMenuItem("styles-separator", "") { IsSeparator = true });
        Add("named-styles", "Named styles...", () => ShowNamedParagraphStyles(owner(), viewport(), editor));

        var presets = new UiMenuItem("preset", "Formatting preset");
        paragraph.Children.Add(presets);
        AddPreset("body", "Body", null, false, 0, 0);
        AddPreset("title", "Title", 28, true, 0, 12);
        AddPreset("heading-large", "Large heading", 20, true, 12, 6);
        AddPreset("heading-small", "Small heading", 14, true, 8, 4);

        bool CanEdit() => editor.GetCommandState(RichEditCommand.InsertText).IsEnabled;
        void Add(string id, string title, Action action)
        {
            string command = "paragraph." + id;
            paragraph.Children.Add(new UiMenuItem(id, title) { CommandName = command });
            dispatcher.Add(new StandardCommand(command, () => dispatch(action), CanEdit));
        }

        void AddPreset(string id, string title, float? size, bool bold, float before, float after)
        {
            string command = "paragraph.preset." + id;
            presets.Children.Add(new UiMenuItem(id, title) { CommandName = command });
            dispatcher.Add(new StandardCommand(command, () => editor.ApplyParagraphStyleRange(editor.Selection, new ParagraphStyleDelta
            {
                InlinePreset = new InlineStyleDelta { SetFontSize = true, FontSize = size, Bold = bold },
                SpacingBefore = before,
                SpacingAfter = after,
            }), CanEdit));
        }
    }

    private static void ShowNamedParagraphStyles(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        RichTextDocument document = editor.Document;
        RichTextRange selection = editor.Selection;
        RichTextParagraph sample = document.ParagraphAt(selection.Start);
        var names = document.StyleDefaults.NamedParagraphStyles;
        var message = new StandardLabel
        {
            Text = names.Length == 0 ? "No named styles yet." : "Choose an existing style or enter a name below.",
        };
        var name = new StandardEdit { Text = sample.Style.StyleName ?? "", PlaceholderText = "Style name" };
        var rows = new List<UiElement> { message };
        if (names.Length > 0)
        {
            var picker = new StandardComboBox { ToolTipText = "Existing styles", MaxDropDownItems = 8 };
            picker.SetItems(names.OrderBy(style => style.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(style => new UiComboBoxItem(style.Name, style.Name)).ToArray());
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is { } item)
                    name.Text = item.Text;
            };
            rows.Add(picker);
        }
        rows.Add(new StandardLabel { Text = "Save style uses the first selected paragraph’s formatting." });
        rows.Add(name);
        StandardDialog dialog = Create("Named paragraph styles", names.Length == 0 ? 208 : 242);

        void Submit(bool capture, bool detach = false)
        {
            if (!ReferenceEquals(document, editor.Document) || selection != editor.Selection)
            {
                message.Text = "The document or selection changed. Reopen this dialog.";
                return;
            }
            string value = name.Text.Trim();
            if (!detach && !NamedParagraphStyle.IsValidName(value))
            {
                message.Text = "Enter a style name of 1–128 characters.";
                return;
            }
            NamedParagraphStyle? style = detach ? null : document.FindNamedStyle(value);
            if (!capture && !detach && style is null)
            {
                message.Text = "That style does not exist. Use Save style to create it.";
                return;
            }
            var delta = new ParagraphStyleDelta { SetStyleName = true, StyleName = detach ? null : style?.Name ?? value };
            if (capture)
                delta = delta with { DefineStyle = new NamedParagraphStyle(style?.Name ?? value, sample.Style, sample.MarkStyle) };
            editor.ApplyParagraphStyleRange(selection, delta, selection);
            dialog.Accept();
        }
        name.Submitted += (_, _) => Submit(capture: false);
        dialog.AddChild(new StandardDialogFields(rows.ToArray(),
            [Button("Apply style", () => Submit(capture: false)), Button("Save style", () => Submit(capture: true)),
             Button("Detach style", () => Submit(capture: false, detach: true)), Button("Cancel", () => dialog.Cancel())]));
        Show(dialog, owner, viewport, name);
    }

    private static void ShowParagraphProperties(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        ParagraphStyle style = editor.Document.ParagraphStyleAt(editor.Selection.Start);
        ShowParagraphForm(owner, viewport, editor, "Paragraph properties",
            "Enter values in points. Blank fields keep their current values.",
            [
                ("Space before and after, separated by a comma", Pair(style.SpacingBefore, style.SpacingAfter)),
                ("Line height (1x, exact 18, minimum 18, or leading 3)", LineHeightText(style)),
                ("Left, right, and first-line indents, or default for each", IndentText(style)),
                ("Background (#RRGGBB or none)", ColorText(style.Background)),
                ("Border width and color, or none", BorderText(style.Border)),
                ("Outline level (1–9 or body)", style.HeadingLevel?.ToString(CultureInfo.InvariantCulture) ?? "body"),
            ], ParseParagraphProperties);
    }

    private static ParagraphStyleDelta? ParseParagraphProperties(string[] fields)
    {
        var delta = new ParagraphStyleDelta();
        if (fields[0].Length > 0)
        {
            string[] values = fields[0].Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 2 || !TryPoints(values[0], 0, 1584, out float before) || !TryPoints(values[1], 0, 1584, out float after))
                return null;
            delta = delta with { SpacingBefore = before, SpacingAfter = after };
        }
        if (fields[1].Length > 0)
        {
            string[] words = Words(fields[1]);
            if (words.Length == 1 && words[0].EndsWith('x') && TryPoints(words[0][..^1], 0.1f, 20, out float multiplier))
                delta = delta with { LineSpacing = multiplier };
            else if (words.Length == 2 && TryPoints(words[1], words[0] == "leading" ? -1584 : 1, 1584, out float height))
            {
                LineHeightRule? rule = words[0].ToLowerInvariant() switch
                {
                    "exact" => LineHeightRule.Exact,
                    "minimum" => LineHeightRule.AtLeast,
                    "leading" => LineHeightRule.Leading,
                    _ => null,
                };
                if (rule is null)
                    return null;
                delta = delta with { LineHeightRule = rule, LineHeight = height };
            }
            else
                return null;
        }
        if (fields[2].Length > 0)
        {
            string[] values = fields[2].Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 3 || !TryIndent(values[0], out float? left) || !TryIndent(values[1], out float? right) || !TryIndent(values[2], out float? first))
                return null;
            delta = delta with
            {
                SetLeftIndent = true, LeftIndent = left, SetRightIndent = true, RightIndent = right,
                SetFirstLineIndent = true, FirstLineIndent = first,
            };
        }
        if (fields[3].Length > 0)
        {
            if (!TryParagraphColor(fields[3], out BColor background))
                return null;
            delta = delta with { Background = background };
        }
        if (fields[4].Length > 0)
        {
            ParagraphBorder? border = null;
            if (!EqualsText(fields[4], "none"))
            {
                string[] words = Words(fields[4]);
                if (words.Length != 2 || !TryPoints(words[0], 0.25f, 24, out float width) ||
                    !WriterCharacterFormatting.TryParseColor(words[1], out BColor color))
                    return null;
                border = new ParagraphBorder(CellBorders.All(new TableBorder(color, width)), new TableCellPadding(3, 3, 3, 3));
            }
            delta = delta with { SetBorder = true, Border = border };
        }
        if (fields[5].Length > 0)
        {
            int? heading = null;
            if (!EqualsText(fields[5], "body"))
            {
                if (!TryInteger(fields[5], 1, 9, out int level))
                    return null;
                heading = level;
            }
            delta = delta with { SetHeadingLevel = true, HeadingLevel = heading };
        }
        return delta;
    }

    private static void ShowTabStops(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        ParagraphTabStops? stops = editor.Document.ParagraphStyleAt(editor.Selection.Start).TabStops;
        ShowParagraphForm(owner, viewport, editor, "Tab stops",
            "Enter stops in points, separated by commas. Use none to clear.",
            [("Try 72 left, 144 decimal dot, 216 right dash", stops is null ? "" : FormatTabs(stops))],
            fields => ParseTabStops(fields[0]), prefill: true);
    }

    internal static ParagraphStyleDelta? ParseTabStops(string input)
    {
        if (input.Length == 0)
            return new ParagraphStyleDelta();
        if (EqualsText(input, "none"))
            return new ParagraphStyleDelta { SetTabStops = true };
        var stops = new List<ParagraphTabStop>();
        foreach (string entry in input.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] words = Words(entry);
            if (words.Length is < 2 or > 4 || !TryPoints(words[0], 0, 20000, out float points) ||
                !Enum.TryParse(words[1], ignoreCase: true, out TabAlignment alignment) || !Enum.IsDefined(alignment))
                return null;
            string? leader = words.Length > 2 ? words[2].ToLowerInvariant() switch
            {
                "dot" => ".", "dash" => "-", "line" => "_", "none" => null,
                _ when words[2].Length == 1 && !char.IsControl(words[2][0]) && !char.IsSurrogate(words[2][0]) => words[2],
                _ => "",
            } : null;
            if (leader == "" || words.Length == 4 && (alignment != TabAlignment.Decimal || !EqualsText(words[3], "comma")))
                return null;
            stops.Add(new ParagraphTabStop(points, alignment, leader, words.Length == 4 ? ',' : '.'));
            if (stops.Count > 256)
                return null;
        }
        return new ParagraphStyleDelta { SetTabStops = true, TabStops = new ParagraphTabStops(stops) };
    }

    private static void ShowListProperties(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        RichTextDocument document = editor.Document;
        RichTextPosition start = document.ParagraphStart(editor.Selection.Start);
        ParagraphStyle style = document.ParagraphStyleAt(start);
        ShowParagraphForm(owner, viewport, editor, "List properties",
            "Enter the list action and optional level or hanging indent.",
            [("Action (new, continue, restart, or remove)", "new"),
             ("New list marker (decimal, upperRoman, lowerLetter, or bullet)", style.List?.Definition.LevelAt(style.List.Value.Level).Format.ToString() ?? "decimal"),
             ("Level from 1 to 9 or blank", (style.List?.Level + 1)?.ToString(CultureInfo.InvariantCulture) ?? "1"),
             ("Restart number or blank for 1", "1"),
             ("Hanging indent in points or blank", "")],
            fields => ParseList(document, start, style, fields));
    }

    private static ParagraphStyleDelta? ParseList(RichTextDocument document, RichTextPosition start, ParagraphStyle style, string[] fields)
    {
        string action = fields[0].ToLowerInvariant();
        if (action == "remove")
            return ParagraphStyleDelta.WithListKind(ListKind.None);
        if (action is not ("new" or "continue" or "restart"))
            return null;

        ListReference? existing = style.List;
        if (action == "continue" && existing is null)
        {
            RichTextPosition position = start;
            while (position != RichTextDocument.Start && existing is null)
            {
                position = document.ParagraphStart(document.PositionLeftOf(position));
                existing = document.ParagraphStyleAt(position).List;
            }
            if (existing is null)
                return null;
        }
        int level = (existing?.Level ?? 0) + 1;
        if (fields[2].Length > 0 && !TryInteger(fields[2], 1, ListDefinition.MaxLevels, out level))
            return null;

        int restart = 1;
        if (fields[3].Length > 0 && !TryInteger(fields[3], 1, 1_000_000, out restart))
            return null;

        ListDefinition definition;
        if (action == "new" || existing is null)
        {
            string formatName = fields[1].Length == 0 ? "Decimal" : fields[1];
            if (!Enum.TryParse(formatName, ignoreCase: true, out ListNumberFormat format) || !Enum.IsDefined(format) || format == ListNumberFormat.None)
                return null;
            definition = new ListDefinition(Enumerable.Range(0, ListDefinition.MaxLevels).Select(index =>
                ListLevel.DecimalAt(index) with { Format = format, Template = format == ListNumberFormat.Bullet ? "•" : ListLevel.DecimalAt(index).Template,
                    Start = action == "restart" ? restart : 1 }).ToArray());
        }
        else
            definition = existing.Value.Definition;

        float hanging = 0;
        if (fields[4].Length > 0 && !TryPoints(fields[4], 0, 1584, out hanging))
            return null;
        return new ParagraphStyleDelta
        {
            SetListReference = true, ListReference = new ListReference(definition, level - 1, action == "restart" ? restart : null),
            IndentLevel = level != style.IndentLevel ? level : null,
            SetLeftIndent = fields[4].Length > 0, LeftIndent = 18 * level + hanging,
            SetFirstLineIndent = fields[4].Length > 0, FirstLineIndent = -hanging,
        };
    }

    private static void ShowParagraphForm(UiWindow owner, BSize viewport, UiRichEdit editor,
        string title, string instructions, (string Label, string Hint)[] fields, Func<string[], ParagraphStyleDelta?> parse,
        bool prefill = false)
    {
        var message = new StandardLabel { Text = instructions };
        StandardDialog dialog = Create(title, 105 + fields.Length * 66);
        RichTextDocument document = editor.Document;
        RichTextRange selection = editor.Selection;
        var rows = new List<UiElement> { message };
        var inputs = new List<StandardEdit>();
        foreach ((string label, string hint) in fields)
        {
            var input = new StandardEdit { Text = prefill ? hint : "", PlaceholderText = prefill ? "" : hint };
            input.Submitted += (_, _) => Submit();
            rows.Add(new StandardLabel { Text = label });
            rows.Add(input);
            inputs.Add(input);
        }
        dialog.AddChild(new StandardDialogFields(rows.ToArray(), [Button("Apply", Submit), Button("Cancel", () => dialog.Cancel())]));
        Show(dialog, owner, viewport, inputs[0]);

        void Submit()
        {
            if (!ReferenceEquals(document, editor.Document) || selection != editor.Selection)
            {
                message.Text = "The document or selection changed. Reopen this dialog.";
                return;
            }
            if (parse(inputs.Select(input => input.Text.Trim()).ToArray()) is not { } delta)
            {
                message.Text = "Check the values against the examples above each field.";
                return;
            }
            if (delta != default)
            {
                RichTextRange target = delta.ListReference is { RestartAt: not null }
                    ? RichTextRange.Caret(selection.Start) : selection;
                editor.ApplyParagraphStyleRange(target, delta, selection);
            }
            dialog.Accept();
        }
    }

    private static bool TryPoints(string text, float minimum, float maximum, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        float.IsFinite(value) && value >= minimum && value <= maximum;
    private static bool TryInteger(string text, int minimum, int maximum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool EqualsText(string input, string value) => string.Equals(input, value, StringComparison.OrdinalIgnoreCase);
    private static bool TryIndent(string word, out float? result)
    {
        result = null;
        if (EqualsText(word, "default"))
            return true;
        if (!TryPoints(word, -20000, 20000, out float value))
            return false;
        result = value;
        return true;
    }
    private static bool TryParagraphColor(string word, out BColor color) =>
        WriterCharacterFormatting.TryParseColor(word, out color) || EqualsText(word, "none");
    private static string ColorText(BColor color) => color.IsEmpty ? "none" : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static string Pair(float before, float after) => FormattableString.Invariant($"{before}, {after}");
    private static string IndentText(ParagraphStyle style) => string.Join(", ", Number(style.LeftIndent), Number(style.RightIndent), Number(style.FirstLineIndent));
    private static string Number(float? value) => value?.ToString("G", CultureInfo.InvariantCulture) ?? "default";
    private static string BorderText(ParagraphBorder? border) => border is { } value && value.Edges.Top.IsVisible
        ? FormattableString.Invariant($"{value.Edges.Top.Width} {ColorText(value.Edges.Top.Color)}") : "none";
    private static string LineHeightText(ParagraphStyle style) => style.LineHeightRule switch
    {
        LineHeightRule.Exact => "exact " + Number(style.LineHeight),
        LineHeightRule.AtLeast => "minimum " + Number(style.LineHeight),
        LineHeightRule.Leading => "leading " + Number(style.LineHeight),
        _ => Number(style.LineSpacing > 0 ? style.LineSpacing : 1) + "x",
    };
    private static string FormatTabs(ParagraphTabStops stops) => string.Join(", ", stops.Select(stop =>
        FormattableString.Invariant($"{stop.Position} {stop.Alignment.ToString().ToLowerInvariant()}") +
        (stop.Leader is null && stop.DecimalCharacter != ',' ? "" : " " + (stop.Leader ?? "none")) +
        (stop.DecimalCharacter == ',' ? " comma" : "")));
}
