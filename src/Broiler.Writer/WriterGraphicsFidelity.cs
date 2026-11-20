using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;

namespace Broiler.Writer;

internal static class WriterGraphicsFidelity
{
    internal static bool HasGraphics(RichTextDocument document)
    {
        if (WriterGraphics.Shapes(document).Any()) return true;
        bool found = false;
        document.MapPictures(image => { found = true; return image; });
        return found;
    }

    internal static string? DescribeFirstChange(RichTextDocument original, RichTextDocument reopened)
    {
        bool columns = original.PageGeometry?.ColumnCount > 1 || original.Sections.Any(section =>
            section.ColumnCount > 1 || section.Page?.ColumnCount > 1 || section.PageStyles?.Styles.Any(style => style.Page?.ColumnCount > 1) == true);
        if (!ShapeList(original.Shapes, reopened.Shapes, columns))
            return "The generated file changes floating objects, their anchors, wrapping, stacking, text, or picture properties.";
        if (!Pictures(original.Paragraphs, reopened.Paragraphs))
            return "The generated file changes inline picture size, alternative text, or cropping.";
        var checkedRunning = new HashSet<(RunningContent, RunningContent)>();
        for (int index = 0; index < Math.Min(original.ParagraphCount, reopened.ParagraphCount); index++)
        {
            var a = original.RunningContentFor(DocumentSection.Containing(original.Sections, index));
            var b = reopened.RunningContentFor(DocumentSection.Containing(reopened.Sections, index));
            if (!checkedRunning.Add((a, b))) continue;
            foreach (var variant in Enum.GetValues<PageSelection>())
                if (!ShapeList(a.EffectiveHeaderShapes(variant), b.EffectiveHeaderShapes(variant)) ||
                    !ShapeList(a.EffectiveFooterShapes(variant), b.EffectiveFooterShapes(variant)) ||
                    !Pictures(a.EffectiveHeader(variant), b.EffectiveHeader(variant)) ||
                    !Pictures(a.EffectiveFooter(variant), b.EffectiveFooter(variant)))
                    return "The generated file changes pictures or floating objects in a header or footer.";
        }
        return null;
    }

    private static bool ShapeList(IReadOnlyList<DocumentShape> source, IReadOnlyList<DocumentShape> saved, bool columnSensitive = false)
    {
        if (source.Count != saved.Count) return false;
        // A codec may renumber stacking values; their relative paint order is the contract.
        var a = source.OrderBy(shape => shape.ZOrder).ToArray();
        var b = saved.OrderBy(shape => shape.ZOrder).ToArray();
        for (int index = 0; index < a.Length; index++)
        {
            var first = a[index]; var second = b[index];
            if (!Near(first.OffsetX, second.OffsetX) || !Near(first.OffsetY, second.OffsetY) ||
                !Near(first.Width, second.Width) || !Near(first.Height, second.Height) ||
                first.ParagraphIndex != second.ParagraphIndex || first.AnchorPage != second.AnchorPage ||
                first.OffsetFromPage != second.OffsetFromPage || columnSensitive && first.OffsetXFromColumn != second.OffsetXFromColumn ||
                first.BehindText != second.BehindText || first.Wrap != second.Wrap || first.WrapSide != second.WrapSide ||
                !Near(first.WrapDistance, second.WrapDistance) || !Margins(first, second) ||
                !OptionalNear(first.OffsetFromBandParagraph, second.OffsetFromBandParagraph) || first.Geometry != second.Geometry ||
                !first.Paragraphs.Select(paragraph => paragraph.Text).SequenceEqual(second.Paragraphs.Select(paragraph => paragraph.Text)) ||
                !Picture(first.Image, second.Image) || !Pictures(first.Paragraphs, second.Paragraphs)) return false;
        }
        return true;
    }
    private static bool Margins(DocumentShape a, DocumentShape b)
    {
        var first = a.WrapMargins ?? new TableCellPadding(a.WrapDistance, a.WrapDistance, a.WrapDistance, a.WrapDistance);
        var second = b.WrapMargins ?? new TableCellPadding(b.WrapDistance, b.WrapDistance, b.WrapDistance, b.WrapDistance);
        return Near(first.Left, second.Left) && Near(first.Top, second.Top) && Near(first.Right, second.Right) && Near(first.Bottom, second.Bottom);
    }
    private static bool OptionalNear(double? a, double? b) => a is null || b is null ? a == b : Near(a.Value, b.Value);

    private static bool Pictures(IReadOnlyList<RichTextParagraph> source, IReadOnlyList<RichTextParagraph> saved)
    {
        var a = source.SelectMany(Images).ToArray(); var b = saved.SelectMany(Images).ToArray();
        return a.Length == b.Length && a.Where((image, index) => !Picture(image, b[index])).Any() == false;
        static IEnumerable<InlineImage> Images(RichTextParagraph paragraph) => paragraph.Runs
            .Where(run => run.Style.Image is not null).SelectMany(run => Enumerable.Repeat(run.Style.Image!, run.Length));
    }

    private static bool Picture(InlineImage? first, InlineImage? second)
    {
        if (first is null || second is null) return first is null && second is null;
        if (first.AltText != second.AltText) return false;
        if (first.TryGetDisplaySize(out double width, out double height) &&
            (!second.TryGetDisplaySize(out double otherWidth, out double otherHeight) || !Near(width, otherWidth) || !Near(height, otherHeight))) return false;
        var a = first.Presentation; var b = second.Presentation;
        return Math.Abs(a.CropLeft - b.CropLeft) < .001 && Math.Abs(a.CropTop - b.CropTop) < .001 &&
            Math.Abs(a.CropRight - b.CropRight) < .001 && Math.Abs(a.CropBottom - b.CropBottom) < .001 && a.Mask == b.Mask;
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) < .1;
}
