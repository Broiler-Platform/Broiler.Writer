using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.Menu;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.Window;

using Broiler.Graphics.Paging;
using Broiler.UI.Dialog.Standard;
namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    internal static void AddPageStructureCommands(StandardCommandDispatcher dispatcher, UiMenuItem menu, UiRichEdit editor,
        Func<UiWindow> owner, Func<BSize> viewport, Action<Action> dispatch)
    {
        Add("page.setup", "Page setup", () => ShowPageSetup(owner(), viewport(), editor));
        Add("page.section-paper", "Section page setup", () => ShowPageSetup(owner(), viewport(), editor, sectionOnly: true));
        menu.Children.Add(new UiMenuItem("sections-separator", "") { IsSeparator = true });
        Add("page.section", "Section properties", () => ShowSection(owner(), viewport(), editor, split: false));
        Add("page.section-break", "Insert section break", () => ShowSection(owner(), viewport(), editor, split: true));
        menu.Children.Add(new UiMenuItem("headers-separator", "") { IsSeparator = true });
        Add("page.running-content", "Headers and footers", () => ShowRunningMenu(rich: false));
        Add("page.running-content-rich", "Format header or footer", () => ShowRunningMenu(rich: true));
        Add("page.running-content-link", "Link header or footer", () => ShowRunningLink(owner(), viewport(), editor));
        menu.Children.Add(new UiMenuItem("pagination-separator", "") { IsSeparator = true });
        Add("page.pagination", "Paragraph pagination", () => ShowPagination(owner(), viewport(), editor));

        void Add(string command, string title, Action open)
        {
            menu.Children.Add(new UiMenuItem(command, title + "...") { CommandName = command });
            dispatcher.Add(new StandardCommand(command, () => dispatch(open), () => !editor.IsReadOnly && editor.IsEnabled));
        }

        void ShowRunningMenu(bool rich)
        {
            PageForm(owner(), viewport(), editor, "Headers and footers",
                "Choose the page variant and band to edit.",
                [("Page variant (default, first, even)", "default"), ("Band (header or footer)", "header")],
                (document, selection, values) =>
                {
                    if (!Enum.TryParse(values[0], true, out PageSelection variant) || !Enum.IsDefined(variant) ||
                        values[1].ToLowerInvariant() is not ("header" or "footer"))
                        return "Choose a valid page variant and band.";
                    dispatch(() => ShowRunningBand(owner(), viewport(), editor, variant,
                        values[1].Equals("header", StringComparison.OrdinalIgnoreCase), rich));
                    return null;
                }, openAfterAccept: true);
        }
    }

    private static void ShowPageSetup(UiWindow owner, BSize viewport, UiRichEdit editor, bool sectionOnly = false)
    {
        RichTextDocument snapshot = editor.Document;
        int index = snapshot.ParagraphIndexAt(editor.Selection.Start);
        DocumentSection? section = sectionOnly ? DocumentSection.Containing(snapshot.Sections, index) : null;
        PageGeometry original = section?.Page ?? snapshot.PageGeometry ?? PageGeometry.A4;
        string title = sectionOnly ? "Section page setup" : "Page setup";
        RichTextRange originalSelection = editor.Selection;
        var initial = new PageSetupOptions(new BSize(original.Width, original.Height),
            new BPageMargins(original.MarginLeft, original.MarginTop, original.MarginRight, original.MarginBottom),
            original.HeaderDistance, original.FooterDistance, section?.ColumnCount ?? original.ColumnCount,
            section?.ColumnGap ?? original.ColumnGap);
        StandardPageSetupDialog.Show(owner, viewport, title, initial, options =>
        {
            if (!ReferenceEquals(snapshot, editor.Document) || originalSelection != editor.Selection)
                return "The document or selection changed. Reopen the dialog.";

            Apply(snapshot, original with
            {
                Width = options.PaperSize.Width, Height = options.PaperSize.Height,
                MarginLeft = options.Margins.Left, MarginRight = options.Margins.Right,
                MarginTop = options.Margins.Top, MarginBottom = options.Margins.Bottom,
                HeaderDistance = options.HeaderDistance, FooterDistance = options.FooterDistance,
                ColumnCount = options.ColumnCount, ColumnGap = options.ColumnGap,
            });
            return null;
        }, canClose: !OperatingSystem.IsBrowser());

        void Apply(RichTextDocument document, PageGeometry page)
        {
            if (!sectionOnly)
                editor.ApplyPageStructure(page, document.Sections, document.RunningContent);
            else
            {
                var sections = EditSection(document, index, false, page.ColumnCount, page.ColumnGap,
                    section?.BreakType ?? SectionBreakType.Continuous, section?.PageNumberStart, section?.PageNumberFormat, page);
                editor.ApplyPageStructure(document.PageGeometry, sections, document.RunningContent);
            }
        }
    }

    private static void ShowSection(UiWindow owner, BSize viewport, UiRichEdit editor, bool split)
    {
        RichTextDocument document = editor.Document;
        int index = document.ParagraphIndexAt(editor.Selection.Start);
        DocumentSection? existing = DocumentSection.Containing(document.Sections, index);
        PageGeometry page = existing?.Page ?? document.PageGeometry ?? PageGeometry.A4;
        PageForm(owner, viewport, editor, split ? "Insert section break" : "Section properties",
            split ? "The new section starts at the current paragraph." : "These settings apply to the section at the caret.",
            [
                ("Break (continuous, nextPage, nextColumn, oddPage, evenPage)", split ? "nextPage" : existing?.BreakType.ToString() ?? "continuous"),
                ("Columns and gap in points", PointsText(existing?.ColumnCount ?? page.ColumnCount, existing?.ColumnGap ?? page.ColumnGap)),
                ("Page numbering start (continue or a number)", split ? "continue" : existing?.PageNumberStart?.ToString(CultureInfo.InvariantCulture) ?? "continue"),
                ("Number format (decimal, upperRoman, lowerRoman, upperLetter, lowerLetter)", existing?.PageNumberFormat?.ToString() ?? "decimal"),
            ], (snapshot, selection, fields) =>
            {
                if (!Enum.TryParse(fields[0], true, out SectionBreakType kind) || !Enum.IsDefined(kind) ||
                    !Columns(fields[1], out int columns, out double gap) || gap * (columns - 1) >= page.ContentWidth ||
                    !Enum.TryParse(fields[3], true, out ListNumberFormat format) ||
                    format is not (ListNumberFormat.Decimal or ListNumberFormat.UpperRoman or ListNumberFormat.LowerRoman or ListNumberFormat.UpperLetter or ListNumberFormat.LowerLetter))
                    return "Check the break type, columns, and number format.";
                int? restart = null;
                if (!fields[2].Equals("continue", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int start) || start is < 1 or > 1_000_000)
                        return "Enter a positive page number or continue.";
                    restart = start;
                }
                if (split && snapshot.Tables.Any(table => index > table.ParagraphIndex && index < table.ParagraphEnd))
                    return "Start the section before or after the table.";
                IReadOnlyList<DocumentSection> sections = EditSection(snapshot, index, split, columns, gap, kind, restart, format);
                editor.ApplyPageStructure(snapshot.PageGeometry, sections, snapshot.RunningContent);
                return null;
            });
    }

    internal static IReadOnlyList<DocumentSection> EditSection(RichTextDocument document, int index, bool split,
        int columns, double gap, SectionBreakType kind, int? pageNumber, ListNumberFormat? format, PageGeometry? page = null)
    {
        DocumentSection? existing = DocumentSection.Containing(document.Sections, index);
        var sections = document.Sections.Where(section => !ReferenceEquals(section, existing)).ToList();
        int first = existing?.ParagraphIndex ?? sections.Where(section => section.ParagraphEnd <= index)
            .Select(section => section.ParagraphEnd).DefaultIfEmpty(0).Max();
        int end = existing?.ParagraphEnd ?? sections.Where(section => section.ParagraphIndex > index)
            .Select(section => section.ParagraphIndex).DefaultIfEmpty(document.ParagraphCount).Min();
        if (split && index > first)
        {
            sections.Add(CopySection(existing, first, index, existing?.ColumnCount ?? document.PageGeometry?.ColumnCount ?? 1,
                existing?.ColumnGap ?? document.PageGeometry?.ColumnGap ?? 0, existing?.BreakType ?? SectionBreakType.Continuous,
                existing?.PageNumberStart, existing?.PageNumberFormat, preserveEnd: false));
            first = index;
        }
        sections.Add(CopySection(existing, first, end, columns, gap, kind, pageNumber, format, page: page));
        return sections.OrderBy(section => section.ParagraphIndex).ToArray();
    }

    private static DocumentSection CopySection(DocumentSection? original, int start, int end, int columns, double gap,
        SectionBreakType kind, int? pageNumber, ListNumberFormat? format, bool preserveEnd = true, RunningContent? running = null,
        PageGeometry? page = null) =>
        new(start, end - start, columns, gap, page ?? original?.Page, running ?? original?.RunningContent, original?.Background ?? default,
            original?.PageStyles, original?.Hidden ?? false, original?.DontBalanceColumns ?? false, kind,
            original?.LeftIndent ?? 0, original?.RightIndent ?? 0, pageNumber)
        {
            PageNumberFormat = format,
            FootnotesEnding = preserveEnd ? original?.FootnotesEnding ?? [] : [],
            ColumnSeparator = original?.ColumnSeparator ?? false,
        };

    private static void ShowRunningBand(UiWindow owner, BSize viewport, UiRichEdit editor, PageSelection variant, bool header, bool rich)
    {
        RichTextDocument document = editor.Document;
        DocumentSection? section = DocumentSection.Containing(document.Sections, document.ParagraphIndexAt(editor.Selection.Start));
        RunningContent running = section?.RunningContent ?? document.RunningContent;
        RunningContent effective = document.RunningContentFor(section);
        IReadOnlyList<RichTextParagraph> band = header ? effective.Header(variant) : effective.Footer(variant);
        var shapes = header ? effective.HeaderShapes(variant) : effective.FooterShapes(variant);
        var tables = header ? effective.HeaderTables(variant) : effective.FooterTables(variant);
        string title = $"{variant} {(header ? "header" : "footer")}";
        bool complex = band.Count > 1 || band.Any(paragraph => paragraph.Style != ParagraphStyle.Default) ||
            band.SelectMany(paragraph => paragraph.Runs).Any(run =>
            run.Style with { PageField = null, StyleReference = null } != InlineStyle.Default ||
            run.Style.StyleReference is { ParagraphStyle: false } or { FromBottom: true } ||
            run.Style.PageField is not (null or
                { Kind: PageFieldKind.PageNumber, Format: ListNumberFormat.Decimal, Adjust: 0, FollowsPageFormat: true } or
                { Kind: PageFieldKind.PageCount, Format: ListNumberFormat.Decimal, Adjust: 0, FollowsPageFormat: false })) ||
            shapes.Count + tables.Count > 0;
        if (rich || complex)
        {
            ShowRichRunningBand(owner, viewport, editor, title,
                RichTextDocument.FromParagraphs(band).WithShapes(shapes).WithTables(tables),
                updated => ApplyBand(updated.Paragraphs, updated.Shapes, updated.Tables));
            return;
        }
        string template = string.Concat(band.SelectMany(paragraph => BandText(paragraph)));
        PageForm(owner, viewport, editor, title, running.IsLinked(variant, header)
                ? "Linked to the previous section. Applying changes makes a local band."
                : "Fields include {page}, {pages}, and {style:Style name}.",
            [("Band text", template)], (snapshot, selection, values) =>
            {
                if (values[0] == template)
                    return null;
                foreach (Match match in Regex.Matches(values[0], @"\{style:([^{}]+)\}"))
                {
                    string name = match.Groups[1].Value.Trim();
                    if (name.Contains('"') || snapshot.FindNamedStyle(name) is null)
                        return $"Choose an existing named paragraph style for {match.Value}.";
                }
                IReadOnlyList<RichTextParagraph> paragraphs = values[0].Length == 0 ? [] : [ParseRunningBand(values[0], snapshot)];
                ApplyBand(paragraphs, [], []);
                return null;
            }, preserveWhitespace: true);

        void ApplyBand(IReadOnlyList<RichTextParagraph> paragraphs, IReadOnlyList<DocumentShape> newShapes, IReadOnlyList<DocumentTable> newTables)
        {
            RunningContent updated = header ? running.WithHeader(variant, paragraphs, newShapes, newTables)
                : running.WithFooter(variant, paragraphs, newShapes, newTables);
            if (variant == PageSelection.First)
                updated = updated.WithDifferentFirstPage(true);
            ApplyRunningContent(editor, document, section, updated);
        }
    }

    private static void ShowRunningLink(UiWindow owner, BSize viewport, UiRichEdit editor) =>
        PageForm(owner, viewport, editor, "Link header or footer", "Choose the band to link to the preceding section or make local.",
            [("Page variant (default, first, even)", "default"), ("Band (header or footer)", "header"),
             ("Action (link or unlink)", "link")], (document, selection, values) =>
            {
                if (!Enum.TryParse(values[0], true, out PageSelection variant) || !Enum.IsDefined(variant) ||
                    values[1].ToLowerInvariant() is not ("header" or "footer") ||
                    values[2].ToLowerInvariant() is not ("link" or "unlink"))
                    return "Choose a page variant, band, and action.";
                bool header = values[1].Equals("header", StringComparison.OrdinalIgnoreCase);
                bool link = values[2].Equals("link", StringComparison.OrdinalIgnoreCase);
                int index = document.ParagraphIndexAt(selection.Start);
                DocumentSection? section = DocumentSection.Containing(document.Sections, index);
                if (section is null || section.ParagraphIndex == 0)
                    return "Place the caret in a section after the first one.";

                RunningContent running = section.RunningContent ?? document.RunningContent;
                if (link == running.IsLinked(variant, header))
                    return null;
                RunningContent resolved = document.RunningContentFor(section);
                if (link)
                {
                    DocumentSection? previous = DocumentSection.Containing(document.Sections, section.ParagraphIndex - 1);
                    resolved = document.RunningContentFor(previous);
                    int count = header ? resolved.Header(variant).Count + resolved.HeaderShapes(variant).Count
                        : resolved.Footer(variant).Count + resolved.FooterShapes(variant).Count;
                    if (count == 0)
                        return "The preceding section has no band for this page variant.";
                }

                RunningContent updated = header ? running.WithHeader(variant, resolved.Header(variant),
                    resolved.HeaderShapes(variant), resolved.HeaderTables(variant)) : running.WithFooter(variant, resolved.Footer(variant),
                    resolved.FooterShapes(variant), resolved.FooterTables(variant));
                if (link)
                    updated = updated.WithLink(variant, header);
                if (variant == PageSelection.First)
                    updated = updated.WithDifferentFirstPage(true);
                ApplyRunningContent(editor, document, section, updated);
                return null;
            });

    private static void ApplyRunningContent(UiRichEdit editor, RichTextDocument document, DocumentSection? section, RunningContent updated)
    {
        if (section is null)
            editor.ApplyPageStructure(document.PageGeometry, document.Sections, updated);
        else
            editor.ApplyPageStructure(document.PageGeometry, document.Sections.Select(item => ReferenceEquals(item, section)
                ? CopySection(section, section.ParagraphIndex, section.ParagraphEnd, section.ColumnCount, section.ColumnGap,
                    section.BreakType, section.PageNumberStart, section.PageNumberFormat, running: updated)
                : item).ToArray(), document.RunningContent);
    }

    private static void ShowRichRunningBand(UiWindow owner, BSize viewport, UiRichEdit body, string title,
        RichTextDocument band, Action<RichTextDocument> apply)
    {
        RichTextDocument before = body.Document;
        RichTextRange selection = body.Selection;
        var input = new StandardRichEdit { Document = band, PaddingX = 12, PaddingY = 8 };
        if (body is StandardRichEdit rich)
            input.Font = rich.Font;
        var message = new StandardLabel { Text = "Edit and format the running text." };
        var dialog = Create(title, 350);
        dialog.AddChild(new StandardDialogFields([message, input],
            [Format("Bold", RichEditCommand.Bold), Format("Italic", RichEditCommand.Italic),
             Format("Underline", RichEditCommand.Underline), Button("Apply", Submit), Button("Cancel", () => dialog.Cancel())],
            stretchRow: input));
        Show(dialog, owner, viewport, input);

        StandardButton Format(string name, RichEditCommand command) => Button(name, () =>
        {
            input.ExecuteCommand(command);
            input.Session?.SetFocus(input);
        });

        void Submit()
        {
            if (!ReferenceEquals(before, body.Document) || selection != body.Selection)
            {
                message.Text = "The document or selection changed. Reopen the dialog.";
                return;
            }
            if (!ReferenceEquals(band, input.Document))
                apply(input.Document);
            dialog.Accept();
        }
    }

    private static IEnumerable<string> BandText(RichTextParagraph paragraph)
    {
        int offset = 0;
        foreach (StyleRun run in paragraph.Runs)
        {
            yield return run.Style.PageField?.Kind switch
            {
                PageFieldKind.PageNumber => "{page}",
                PageFieldKind.PageCount => "{pages}",
                _ => run.Style.StyleReference is { } reference
                    ? "{style:" + reference.StyleName + "}"
                    : paragraph.Text.Substring(offset, run.Length),
            };
            offset += run.Length;
        }
    }

    internal static RichTextParagraph ParseRunningBand(string text, RichTextDocument? document = null)
    {
        RichTextParagraph paragraph = RichTextParagraph.Empty;
        foreach (string part in Regex.Split(text, @"(\{pages?\}|\{style:[^{}]+\})"))
        {
            PageField? field = part switch
            {
                "{page}" => new PageField(PageFieldKind.PageNumber) { FollowsPageFormat = true },
                "{pages}" => new PageField(PageFieldKind.PageCount),
                _ => null,
            };
            NamedParagraphStyle? namedStyle = part.StartsWith("{style:", StringComparison.Ordinal)
                ? document?.FindNamedStyle(part[7..^1].Trim()) : null;
            var reference = namedStyle is null ? null : new StyleReferenceField(namedStyle.Name) { ParagraphStyle = true };
            string value = reference is not null
                ? reference.Text(document!.Paragraphs, (0, 0), (document.ParagraphCount, 0)) ?? namedStyle!.Name
                : field is not null ? "1" : part;
            paragraph = paragraph.InsertText(paragraph.Length, value, new InlineStyle { PageField = field, StyleReference = reference });
        }
        return paragraph;
    }

    private static void ShowPagination(UiWindow owner, BSize viewport, UiRichEdit editor) =>
        PageForm(owner, viewport, editor, "Paragraph pagination", "Blank fields leave selected paragraphs unchanged.",
            [("Keep with next (yes or no)", ""), ("Keep lines together (yes or no)", ""),
             ("Widow and orphan lines (default or two counts)", ""), ("Break before (none, page, column, soft)", "")],
            (document, selection, fields) =>
            {
                if (!Flag(fields[0], out bool? keepNext) || !Flag(fields[1], out bool? keepTogether))
                    return "Use yes or no for keep settings.";
                bool setLines = fields[2].Length > 0;
                int? widow = null, orphan = null;
                if (setLines && !fields[2].Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Numbers(fields[2], 2, out double[] counts) || counts.Any(value => value > 20 || value != Math.Truncate(value)))
                        return "Enter two whole line counts between 0 and 20, or default.";
                    widow = (int)counts[0];
                    orphan = (int)counts[1];
                }
                string before = fields[3].ToLowerInvariant();
                if (before is not ("" or "none" or "page" or "column" or "soft"))
                    return "Choose none, page, column, or soft.";
                if (before == "soft")
                {
                    int first = document.ParagraphIndexAt(selection.Start);
                    int last = document.ParagraphIndexAt(selection.End);
                    if (first == 0 || Enumerable.Range(first, last - first + 1).Any(index =>
                        document.Paragraphs[index].Length == 0 ||
                        document.Tables.Any(table => index >= table.ParagraphIndex && index < table.ParagraphEnd &&
                            !table.CanAnchorSoftBreak(index)) ||
                        DocumentSection.Containing(document.Sections, index)?.Hidden == true))
                        return "Choose nonempty body text or the first paragraph of a table row that can start a page.";
                }
                var delta = new ParagraphStyleDelta
                {
                    KeepWithNext = keepNext, KeepTogether = keepTogether, SetLineCounts = setLines,
                    WidowLines = widow, OrphanLines = orphan, PageBreakBefore = before == "" ? null : before == "page",
                    ColumnBreakBefore = before == "" ? null : before == "column",
                    SoftPageBreak = before is "" ? null : before == "soft",
                };
                if (delta != default)
                    editor.ApplyParagraphStyleRange(selection, delta, selection);
                return null;
            });

    private static void PageForm(UiWindow owner, BSize viewport, UiRichEdit editor, string title, string description,
        (string Label, string Value)[] fields, Func<RichTextDocument, RichTextRange, string[], string?> apply,
        bool openAfterAccept = false, bool preserveWhitespace = false)
    {
        RichTextDocument document = editor.Document;
        RichTextRange selection = editor.Selection;
        var message = new StandardLabel { Text = description };
        var rows = new List<UiElement> { message };
        var inputs = new List<StandardEdit>();
        var dialog = Create(title, 110 + fields.Length * 67);
        foreach ((string label, string value) in fields)
        {
            var input = new StandardEdit { Text = value };
            rows.Add(new StandardLabel { Text = label });
            rows.Add(input);
            inputs.Add(input);
        }
        dialog.AddChild(new StandardDialogFields(rows.ToArray(), [Button(openAfterAccept ? "Edit" : "Apply", Submit), Button("Cancel", () => dialog.Cancel())]));
        Show(dialog, owner, viewport, inputs[0]);

        void Submit()
        {
            if (!ReferenceEquals(document, editor.Document) || selection != editor.Selection)
            {
                message.Text = "The document or selection changed. Reopen the dialog.";
                return;
            }
            string[] values = inputs.Select(input => preserveWhitespace ? input.Text : input.Text.Trim()).ToArray();
            if (openAfterAccept && Enum.TryParse(values[0], true, out PageSelection variant) && Enum.IsDefined(variant) &&
                values[1].ToLowerInvariant() is "header" or "footer")
                dialog.Accept();
            string? error = apply(document, selection, values);
            if (error is not null)
                message.Text = error;
            else if (!openAfterAccept)
                dialog.Accept();
        }
    }

    private static string PointsText(params double[] numbers) =>
        string.Join(", ", numbers.Select(number => number.ToString("0.###", CultureInfo.InvariantCulture)));

    private static bool Numbers(string text, int count, out double[] values)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        values = new double[count];
        if (parts.Length != count)
            return false;
        for (int index = 0; index < count; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index]) || values[index] is < 0 or > 20000)
                return false;
        }
        return true;
    }

    private static bool Columns(string text, out int count, out double gap)
    {
        count = 1;
        gap = 0;
        if (!Numbers(text, 2, out double[] values) || values[0] is < 1 or > 16 || values[0] != Math.Truncate(values[0]))
            return false;
        count = (int)values[0];
        gap = values[1];
        return true;
    }

    private static bool Flag(string value, out bool? result)
    {
        result = value.ToLowerInvariant() switch { "yes" => true, "no" => false, _ => null };
        return result.HasValue || value == "";
    }
}
