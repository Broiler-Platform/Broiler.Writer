using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;

namespace Broiler.Writer;

internal static class WriterPageFidelity
{
    internal static bool HasStructure(RichTextDocument document) =>
        document.PageGeometry is not null || document.Sections.Count > 0 || !document.RunningContent.IsEmpty ||
        document.Paragraphs.Any(paragraph => paragraph.Style is { KeepWithNext: true } or { KeepTogether: true }
            or { ColumnBreakBefore: true } or { SoftPageBreakBefore: > 0 } or { WidowLines: not null } or { OrphanLines: not null }
            || !paragraph.Style.SoftPageMarkerOffsets.IsDefaultOrEmpty);

    internal static string? DescribeFirstChange(RichTextDocument source, RichTextDocument reopened)
    {
        if (source.PageGeometry is { } paper && !GeometryMatches(paper, reopened.PageGeometry))
            return "The generated file changes the document’s paper, margins, or columns.";
        if (!source.RunningContent.IsEmpty && source.Sections.Sum(section => section.ParagraphCount) != source.ParagraphCount &&
            !RunningMatches(source.RunningContent, reopened.RunningContent))
            return "The generated file changes the document’s headers or footers.";
        foreach (DocumentSection section in source.Sections)
        {
            DocumentSection? saved = DocumentSection.Containing(reopened.Sections, section.ParagraphIndex);
            if (saved is null || section.ParagraphIndex != saved.ParagraphIndex || section.ParagraphCount != saved.ParagraphCount ||
                section.ParagraphIndex > 0 && section.BreakType != saved.BreakType || section.ColumnCount != saved.ColumnCount ||
                section.ColumnCount > 1 && !Near(section.ColumnGap, saved.ColumnGap) ||
                section.PageNumberStart != saved.PageNumberStart ||
                (section.PageNumberFormat ?? ListNumberFormat.Decimal) != (saved.PageNumberFormat ?? ListNumberFormat.Decimal) ||
                section.Page is { } sectionPaper && !GeometryMatches(sectionPaper, saved.Page ?? reopened.PageGeometry) ||
                section.RunningContent is { } running && !LinksMatch(running, saved.RunningContent) ||
                !RunningMatches(source.RunningContentFor(section), reopened.RunningContentFor(saved)))
                return $"The generated file changes the section starting at paragraph {section.ParagraphIndex + 1}.";
        }
        if (source.ParagraphCount == reopened.ParagraphCount)
        {
            for (int index = 0; index < source.ParagraphCount; index++)
            {
                ParagraphStyle original = source.Paragraphs[index].Style;
                ParagraphStyle saved = reopened.Paragraphs[index].Style;
                if (original.KeepWithNext && !saved.KeepWithNext || original.KeepTogether && !saved.KeepTogether ||
                    original.ColumnBreakBefore && !saved.ColumnBreakBefore ||
                    original.SoftPageBreakBefore > 0 && original.SoftPageBreakBefore != saved.SoftPageBreakBefore ||
                    !original.SoftPageMarkerOffsets.IsDefaultOrEmpty &&
                        (saved.SoftPageMarkerOffsets.IsDefaultOrEmpty || !original.SoftPageMarkerOffsets.SequenceEqual(saved.SoftPageMarkerOffsets)) ||
                    original.WidowLines is int widow && widow != saved.WidowLines ||
                    original.OrphanLines is int orphan && orphan != saved.OrphanLines)
                    return $"The generated file changes pagination settings in paragraph {index + 1}.";
            }
        }
        return null;
    }

    private static bool GeometryMatches(PageGeometry a, PageGeometry? b) =>
        b is not null && Near(a.Width, b.Width) && Near(a.Height, b.Height) &&
        Near(a.MarginLeft, b.MarginLeft) && Near(a.MarginRight, b.MarginRight) &&
        Near(a.MarginTop, b.MarginTop) && Near(a.MarginBottom, b.MarginBottom) &&
        Near(a.HeaderDistance, b.HeaderDistance) && Near(a.FooterDistance, b.FooterDistance) &&
        a.ColumnCount == b.ColumnCount && (a.ColumnCount == 1 || Near(a.ColumnGap, b.ColumnGap));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.1;

    private static bool LinksMatch(RunningContent source, RunningContent? saved) =>
        !source.HasLinks || saved is not null && Enum.GetValues<PageSelection>().All(variant =>
            (!source.IsLinked(variant, true) || saved.IsLinked(variant, true)) &&
            (!source.IsLinked(variant, false) || saved.IsLinked(variant, false)));

    private static bool RunningMatches(RunningContent a, RunningContent b) =>
        a.DifferentFirstPage == b.DifferentFirstPage &&
        Enum.GetValues<PageSelection>().All(variant =>
            Band(a.EffectiveHeader(variant)).SequenceEqual(Band(b.EffectiveHeader(variant))) &&
            Band(a.EffectiveFooter(variant)).SequenceEqual(Band(b.EffectiveFooter(variant))) &&
            a.EffectiveHeaderShapes(variant).Count == b.EffectiveHeaderShapes(variant).Count &&
            a.EffectiveFooterShapes(variant).Count == b.EffectiveFooterShapes(variant).Count &&
            a.EffectiveHeaderTables(variant).Count == b.EffectiveHeaderTables(variant).Count &&
            a.EffectiveFooterTables(variant).Count == b.EffectiveFooterTables(variant).Count);

    private static IEnumerable<(char Character, PageField? Field, StyleReferenceField? Reference)> Band(IReadOnlyList<RichTextParagraph> band)
    {
        foreach (RichTextParagraph paragraph in band)
        {
            int offset = 0;
            foreach (StyleRun run in paragraph.Runs)
            {
                if (run.Style.PageField is not null || run.Style.StyleReference is not null)
                {
                    yield return ('\0', run.Style.PageField, run.Style.StyleReference);
                    offset += run.Length;
                    continue;
                }
                for (int end = offset + run.Length; offset < end; offset++)
                    yield return (paragraph.Text[offset], null, null);
            }
            yield return ('\n', null, null);
        }
    }
}
