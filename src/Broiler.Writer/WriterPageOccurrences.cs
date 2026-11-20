using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Text;

namespace Broiler.Writer;

internal enum WriterPageStory { Body, Header, Footer, Note }

internal sealed record WriterPageOccurrence(int Page, WriterPageStory Story, int Paragraph, BRect Bounds,
    RunningContent? RunningContent = null, PageSelection? Variant = null, DocumentNote? Note = null, int Offset = 0);

internal static class WriterPageOccurrences
{
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
    internal readonly record struct SourceSpan(int Start, int End, double StartX, double EndX)
    {
        internal double Left => Math.Min(StartX, EndX);
        internal double Right => Math.Max(StartX, EndX);
        internal bool IsReversed => StartX > EndX;
    }

    internal static SourceSpan[] SourceSpans(LayoutLine line, WriterPageOccurrence occurrence) =>
        line.Pieces.Where(piece => piece.HasVisualSourceGeometry).SelectMany(piece => piece.SourceClusters.Select(cluster =>
                new SourceSpan(cluster.SourceOffset, cluster.SourceOffset + cluster.SourceLength,
                    piece.X + cluster.StartAdvance, piece.X + cluster.EndAdvance)))
            .Where(span => span.Left <= occurrence.Bounds.Right)
            .GroupBy(span => (span.Start, span.End))
            .Select(group =>
            {
                double left = group.Min(span => span.Left);
                double right = Math.Min(occurrence.Bounds.Right, group.Max(span => span.Right));
                return group.First().IsReversed
                    ? new SourceSpan(group.Key.Start, group.Key.End, right, left)
                    : new SourceSpan(group.Key.Start, group.Key.End, left, right);
            })
            .OrderBy(span => span.Left).ToArray();

    internal static int OffsetAt(IReadOnlyList<SourceSpan> spans, double x, int start)
    {
        int offset = spans.Count == 0 ? start : spans[0].IsReversed ? spans[0].End : spans[0].Start;
        foreach (SourceSpan span in spans)
        {
            if (x < span.Left)
                break;
            if (x < span.Right)
                return (x < (span.Left + span.Right) / 2) != span.IsReversed ? span.Start : span.End;
            offset = span.IsReversed ? span.Start : span.End;
        }
        return offset;
    }
#endif

    internal static WriterPageOccurrence? NearestBodyAt(LayoutPage page, RichTextDocument document, BPoint point)
    {
        if (BodyAt(page, document, point) is { } direct) return direct;
        var line = Get(page).Where(item => item.Story == WriterPageStory.Body && item.Bounds.Width > 0)
            .MinBy(item => Math.Max(item.Bounds.Top - point.Y, Math.Max(0, point.Y - item.Bounds.Bottom)) * 10000 +
                Math.Max(item.Bounds.Left - point.X, Math.Max(0, point.X - item.Bounds.Right)));
        if (line is null) return null;
        return BodyAt(page, document, new BPoint(Math.Clamp(point.X, line.Bounds.Left, Math.Max(line.Bounds.Left, line.Bounds.Right - .001)),
            line.Bounds.Top + line.Bounds.Height / 2));
    }

    internal static (RichTextRange Range, BRect Bounds)? PictureAt(LayoutPage page, RichTextDocument document, BPoint point)
    {
        foreach (var (line, occurrence) in SourceLines(page))
        {
            if (occurrence.Story != WriterPageStory.Body) continue;
            foreach (var piece in line.Pieces)
            {
                if (!piece.IsImage || piece.SourceOffset < 0 || piece.Image is null) continue;
                var box = new BRect(piece.X, line.Top, piece.Width, line.ClipHeight ?? line.Height);
                if (!box.Contains(point)) continue;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
                if (piece.SourceClusters.Count == 0) continue;
                var source = piece.SourceClusters[0];
                int offset = source.SourceOffset;
                int length = source.SourceLength;
#else
                int offset = piece.SourceOffset;
                const int length = 1;
#endif
                var start = document.PositionAt(occurrence.Paragraph, offset);
                var end = document.PositionAt(occurrence.Paragraph, offset + length);
                return (new RichTextRange(start, end), box);
            }
        }
        return null;
    }

    internal static WriterPageOccurrence? BodyAt(LayoutPage page, RichTextDocument document, BPoint point)
    {
        foreach ((LayoutLine line, WriterPageOccurrence occurrence) in SourceLines(page))
        {
            if (occurrence.Story != WriterPageStory.Body || !occurrence.Bounds.Contains(point) ||
                occurrence.Paragraph >= document.ParagraphCount)
                continue;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
            var spans = SourceSpans(line, occurrence);
            return occurrence with { Offset = OffsetAt(spans, point.X, occurrence.Offset) };
#else
            var paragraph = document.Paragraphs[occurrence.Paragraph];
            if (paragraph.Style.RightToLeft || page.VerticalWriting)
                return occurrence;
            foreach (LayoutPiece piece in line.Pieces)
            {
                if (piece.SourceOffset < 0 || piece.Width <= 0 || point.X < piece.X || point.X >= piece.X + piece.Width)
                    continue;
                return occurrence with { Offset = FindOffset(paragraph, piece, point.X - piece.X) };
            }
            return occurrence;
#endif
        }
        // A tap on cell padding still places the caret in that structural cell.
        foreach (LayoutCell cell in page.Cells.Where(cell => cell.Bounds.Contains(point))
            .OrderBy(cell => cell.Bounds.Width * cell.Bounds.Height))
        {
            foreach (var (_, line) in SourceLines(page))
            {
                if (line.Story == WriterPageStory.Body &&
                    cell.Bounds.Contains(new BPoint(line.Bounds.Left, line.Bounds.Top + line.Bounds.Height / 2)) &&
                    WriterTables.At(document, document.PositionAt(line.Paragraph, line.Offset)) is { } location)
                    return line with { Paragraph = location.Cell.ParagraphIndex, Offset = 0 };
            }
        }
        return null;
    }

#if !BROILER_DOCUMENTS_SOURCE_GEOMETRY
    internal static int FindOffset(RichTextParagraph paragraph, LayoutPiece piece, double x)
    {
        int start = Math.Min(piece.SourceOffset, paragraph.Length);
        if (piece.IsTab || piece.IsImage || piece.Note is not null)
            return Math.Min(paragraph.Length, start + (x >= piece.Width / 2 ? 1 : 0));
        string glyphs = piece.Text;
        if (glyphs.Length == 0 || glyphs.Length > paragraph.Length - start ||
            !paragraph.Text.AsSpan(start, glyphs.Length).Equals(glyphs.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return start;
        double advance = BTextMeasurer.MeasureAdvance(glyphs, piece.Font);
        if (advance <= 0)
            return start;
        double target = x * advance / piece.Width;
        double previous = 0;
        int previousOffset = 0;
        var graphemes = StringInfo.ParseCombiningCharacters(glyphs);
        for (int i = 1; i <= graphemes.Length; i++)
        {
            int offset = i == graphemes.Length ? glyphs.Length : graphemes[i];
            double next = i == graphemes.Length ? advance : BTextMeasurer.MeasureAdvance(glyphs[..offset], piece.Font);
            if (target < (previous + next) / 2)
                return start + previousOffset;
            previous = next;
            previousOffset = offset;
        }
        return start + glyphs.Length;
    }
#endif

    internal static IEnumerable<BRect> SelectionBoxes(LayoutPage page, RichTextDocument document, RichTextRange range)
    {
        int first = document.ParagraphIndexAt(range.Start);
        int last = document.ParagraphIndexAt(range.End);
        int start = document.OffsetAt(range.Start);
        int end = document.OffsetAt(range.End);
        foreach ((LayoutLine line, WriterPageOccurrence occurrence) in SourceLines(page))
        {
            if (occurrence.Story != WriterPageStory.Body || occurrence.Paragraph < first || occurrence.Paragraph > last)
                continue;
            RichTextParagraph paragraph = document.Paragraphs[occurrence.Paragraph];
            int from = occurrence.Paragraph == first ? start : 0;
            int to = occurrence.Paragraph == last ? end : paragraph.Length;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
            foreach (SourceSpan span in SourceSpans(line, occurrence))
            {
                if (from < span.End && to > span.Start && span.Right > span.Left)
                    yield return new BRect(span.Left, occurrence.Bounds.Top, span.Right - span.Left, occurrence.Bounds.Height);
            }
#else
            if (paragraph.Style.RightToLeft || page.VerticalWriting)
                continue;
            foreach (LayoutPiece piece in line.Pieces)
            {
                int source = piece.SourceOffset;
                if (source < 0 || source >= paragraph.Length || piece.Width <= 0)
                    continue;
                int length = SourceLength(paragraph, piece);
                if (length == 0)
                    continue;
                if (from >= source + length || to <= source)
                    continue;
                double left = Math.Min(occurrence.Bounds.Right, piece.X + PieceAdvance(piece, length, from - source));
                double right = Math.Min(occurrence.Bounds.Right, piece.X + PieceAdvance(piece, length, to - source));
                if (right > left)
                    yield return new BRect(left, occurrence.Bounds.Top, right - left, occurrence.Bounds.Height);
            }
#endif
        }
    }

#if !BROILER_DOCUMENTS_SOURCE_GEOMETRY
    internal static int SourceLength(RichTextParagraph paragraph, LayoutPiece piece)
    {
        int source = piece.SourceOffset;
        if (source < 0 || source >= paragraph.Length)
            return 0;
        if (piece.IsImage || piece.IsTab || piece.Note is not null)
            return 1;
        int length = piece.Text.Length;
        return length <= paragraph.Length - source &&
            paragraph.Text.AsSpan(source, length).Equals(piece.Text.AsSpan(), StringComparison.OrdinalIgnoreCase)
            ? length : 0;
    }

    internal static double PieceAdvance(LayoutPiece piece, int length, int offset)
    {
        if (offset <= 0)
            return 0;
        if (offset >= length)
            return piece.Width;
        double total = BTextMeasurer.MeasureAdvance(piece.Text, piece.Font);
        return total > 0 ? Math.Clamp(BTextMeasurer.MeasureAdvance(piece.Text[..offset], piece.Font) / total, 0, 1) * piece.Width : 0;
    }
#endif

    internal static IEnumerable<WriterPageOccurrence> Get(LayoutPage page) =>
        SourceLines(page).Select(item => item.Occurrence);

    internal static IEnumerable<(WriterPageStory Story, string Text)> ReadableLines(LayoutPage page) =>
        SourceLines(page)
            .Select(item => (item.Occurrence.Story, Text: string.Concat(item.Line.Pieces.Select(piece =>
                piece.Image is { } image ? image.AltText : piece.IsTab ? " " : piece.Text))))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text));

    internal static IEnumerable<(LayoutLine Line, WriterPageOccurrence Occurrence)> SourceLines(LayoutPage page)
    {
        var headers = page.HeaderLines.ToHashSet();
        var footers = page.FooterLines.ToHashSet();
        foreach (LayoutLine line in page.Lines)
        {
            if (line.Height <= 0)
                continue;
            double left = line.Pieces.Count == 0 ? line.IndentLeft : line.Pieces.Min(piece => piece.X);
            double right = line.Pieces.Count == 0 ? left + 8 : line.Pieces.Max(piece => piece.X + piece.Width);
            if (line.ClipRight is double clip)
                right = System.Math.Min(clip, right);
            var bounds = new BRect(left, line.Top, System.Math.Max(0, right - left), line.ClipHeight ?? line.Height);
            if (headers.Contains(line))
                yield return (line, new(page.Number, WriterPageStory.Header, -1 - line.ParagraphIndex, bounds, page.RunningContentSource, page.HeaderSelection));
            else if (footers.Contains(line))
                yield return (line, new(page.Number, WriterPageStory.Footer, -1 - line.ParagraphIndex, bounds, page.RunningContentSource, page.FooterSelection));
            else if (line.SourceNote is { } note)
                yield return (line, new(page.Number, WriterPageStory.Note, line.NoteParagraphIndex, bounds, Note: note));
            else if (line.ParagraphIndex >= 0)
            {
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
                int start = line.Pieces.SelectMany(piece => piece.SourceClusters).Select(cluster => cluster.SourceOffset)
                    .DefaultIfEmpty(line.EmptyLineSourceOffset ?? 0).Min();
#else
                int start = line.Pieces.Where(piece => piece.SourceOffset >= 0).Select(piece => piece.SourceOffset)
                    .DefaultIfEmpty(0).Min();
#endif
                yield return (line, new(page.Number, WriterPageStory.Body, line.ParagraphIndex, bounds, Offset: start));
            }
        }
    }
}
