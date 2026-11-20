using System;
using System.Linq;
using Broiler.Documents.Model;

namespace Broiler.Writer;

internal static class WriterParagraphFidelity
{
    internal static bool HasFormatting(RichTextDocument document) =>
        document.StyleDefaults.NamedParagraphStyles.Length > 0 ||
        document.Paragraphs.Any(paragraph => HasFormatting(paragraph.Style));

    private static bool HasFormatting(ParagraphStyle style) =>
        style.StyleName is not null || style.HeadingLevel.HasValue || !style.Background.IsEmpty || style.Border is not null ||
        style.TabStops is { Count: > 0 } || style.List is not null ||
        style.LeftIndent.HasValue || style.RightIndent.HasValue || style.FirstLineIndent.HasValue ||
        style.SpacingBefore != 0 || style.SpacingAfter != 0 || style.LineHeightRule != LineHeightRule.Proportional ||
        style.LineSpacing is > 0 and not 1;

    internal static string? DescribeFirstChange(RichTextDocument source, RichTextDocument reopened)
    {
        if (source.ParagraphCount != reopened.ParagraphCount)
            return "The paragraph structure changed, so Writer could not verify the paragraph formatting.";

        foreach (NamedParagraphStyle definition in source.StyleDefaults.NamedParagraphStyles)
        {
            if (reopened.FindNamedStyle(definition.Name) is not { } saved)
                return $"The generated file drops the paragraph style \"{definition.Name}\".";
            if (definition.Text != saved.Text || DescribeStyleChange(definition.Paragraph, saved.Paragraph) is not null)
                return $"The generated file changes the definition of paragraph style \"{definition.Name}\".";
        }

        var sourceList = new ListNumberer();
        var reopenedList = new ListNumberer();
        for (int index = 0; index < source.ParagraphCount; index++)
        {
            RichTextParagraph original = source.Paragraphs[index];
            RichTextParagraph saved = reopened.Paragraphs[index];
            ParagraphStyle before = original.Style;
            ParagraphStyle after = saved.Style;
            string? beforeMarker = sourceList.Advance(before);
            string? afterMarker = reopenedList.Advance(after);
            if (!HasFormatting(before))
                continue;

            string? field = original.Text != saved.Text ? "text or tab characters"
                : before.List is not null && (beforeMarker != afterMarker || before.List.Value.Level != after.List?.Level) ? "list numbering"
                : DescribeStyleChange(before, after);
            if (field is not null)
                return $"The generated file changes {field} in paragraph {index + 1}.";
        }
        return null;
    }

    private static string? DescribeStyleChange(ParagraphStyle before, ParagraphStyle after)
    {
        if (!string.Equals(before.StyleName, after.StyleName, StringComparison.OrdinalIgnoreCase))
            return "named paragraph style";
        if (before.HeadingLevel != after.HeadingLevel)
            return "outline level";
        if (!TabsMatch(before.TabStops, after.TabStops))
            return "tab stops";
        if (before.Background != after.Background)
            return "background";
        if (before.Border != after.Border)
            return "border";
        if (!Near(before.SpacingBefore, after.SpacingBefore) || !Near(before.SpacingAfter, after.SpacingAfter))
            return "paragraph spacing";
        if (before.LineHeightRule != after.LineHeightRule ||
            (before.LineHeightRule == LineHeightRule.Proportional
                ? !Near(Multiplier(before), Multiplier(after)) : !Near(before.LineHeight, after.LineHeight)))
            return "line height";
        if (before.LeftIndent.HasValue && !Near(before.LeftIndentIn(), after.LeftIndentIn()) ||
            before.RightIndent.HasValue && !Near(before.RightIndentIn, after.RightIndentIn) ||
            before.FirstLineIndent.HasValue && !Near(before.FirstLineIndentIn, after.FirstLineIndentIn))
            return "indents";
        return before.Alignment != after.Alignment ? "alignment" : null;
    }

    private static float Multiplier(ParagraphStyle style) => style.LineSpacing > 0 ? style.LineSpacing : 1;
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.06;
    private static bool TabsMatch(ParagraphTabStops? before, ParagraphTabStops? after)
    {
        int count = before?.Count ?? 0;
        if (count != (after?.Count ?? 0))
            return false;
        for (int index = 0; index < count; index++)
        {
            ParagraphTabStop a = before![index];
            ParagraphTabStop b = after![index];
            if (!Near(a.Position, b.Position) || a.Alignment != b.Alignment || a.Leader != b.Leader ||
                a.Alignment == TabAlignment.Decimal && a.DecimalCharacter != b.DecimalCharacter)
                return false;
        }
        return true;
    }
}
