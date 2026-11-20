using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Geometry;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

/// <summary>Body lines in page order, with source lookup for one pagination snapshot.</summary>
internal sealed class WriterPageLineIndex : IRichEditGeometry
{
    private static int _nextRevision;
    private readonly int _revision = Interlocked.Increment(ref _nextRevision);
    private readonly PaginatedDocument _pages;
    private readonly RichTextDocument _document;
    private readonly List<Line> _lines = [];
    private readonly Dictionary<int, Line[]> _byParagraph;
    private readonly ILookup<int, Line> _byPage;

    internal WriterPageLineIndex(PaginatedDocument pages, RichTextDocument document)
    {
        _pages = pages;
        _document = document;
        for (int pageIndex = 0; pageIndex < pages.PageCount; pageIndex++)
        {
            LayoutPage page = pages.Layout.Pages[pageIndex];
            foreach (var (layout, occurrence) in WriterPageOccurrences.SourceLines(page))
            {
                if (occurrence.Story != WriterPageStory.Body || occurrence.Paragraph >= document.ParagraphCount ||
                    occurrence.Bounds.Height <= 0)
                    continue;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
                var spans = WriterPageOccurrences.SourceSpans(layout, occurrence);
                if (spans.Length == 0 && layout.EmptyLineSourceOffset is null)
                    continue;
                _lines.Add(new Line(_lines.Count, pageIndex, occurrence, spans));
#else
                RichTextParagraph paragraph = document.Paragraphs[occurrence.Paragraph];
                var pieces = layout.Pieces.Where(piece => piece.Width > 0 && piece.X < occurrence.Bounds.Right)
                    .Select(piece => (Piece: piece, Length: WriterPageOccurrences.SourceLength(paragraph, piece)))
                    .Where(item => item.Length > 0).ToArray();
                // A line with no known source positions cannot supply a visual caret.
                if (pieces.Length == 0 && paragraph.Length != 0)
                    continue;
                _lines.Add(new Line(_lines.Count, pageIndex, occurrence, paragraph, pieces,
                    !page.VerticalWriting && !paragraph.Style.RightToLeft));
#endif
            }
        }

        _byParagraph = _lines.GroupBy(line => line.Paragraph)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Start).ToArray());
        _byPage = _lines.ToLookup(line => line.PageIndex);
    }

    public RichEditTextHit? HitTest(int fragment, BPoint point, RichEditHitTestMode mode)
    {
        if ((uint)fragment >= (uint)_pages.PageCount)
            return null;
        var page = _pages.Layout.Pages[fragment];
        var hit = mode == RichEditHitTestMode.Nearest
            ? WriterPageOccurrences.NearestBodyAt(page, _document, point)
            : WriterPageOccurrences.BodyAt(page, _document, point);
        if (hit is null)
            return null;
        RichTextPosition position = _document.PositionAt(hit.Paragraph, hit.Offset);
        int offset = _document.OffsetAt(position);
        var line = _byPage[fragment].FirstOrDefault(line =>
            line.Paragraph == hit.Paragraph && line.Bounds == hit.Bounds &&
            offset >= line.Start && offset <= line.End);
        return new RichEditTextHit(position, line is null ? null : Id(line));
    }

    public RichEditLineGeometry? GetLine(RichTextPosition position, RichEditLineId? preferredLine = null, int? preferredFragment = null) =>
        ResolveLine(position, preferredLine, preferredFragment) is { } line ? Describe(line) : null;

    public RichEditLineGeometry? GetRelativeLine(RichEditLineId line, int delta, bool clamp = false)
    {
        if (ResolveId(line) is not { } source)
            return null;
        long index = (long)source.Order + delta;
        if (clamp)
            index = Math.Clamp(index, 0, _lines.Count - 1);
        return index >= 0 && index < _lines.Count ? Describe(_lines[(int)index]) : null;
    }

    public RichTextPosition? GetPositionInLine(RichEditLineId line, double inlineCoordinate) =>
        ResolveId(line) is { } target
            ? _document.PositionAt(target.Paragraph, target.OffsetAt(Math.Min(inlineCoordinate, target.Bounds.Right)))
            : null;

    public RichEditCaretGeometry? GetCaret(RichTextPosition position, RichEditLineId? preferredLine = null, int? preferredFragment = null) =>
        GetCaret(position, preferredLine, preferredFragment, null);

    internal RichEditCaretGeometry? GetCaret(RichTextPosition position, RichEditLineId? preferredLine, int? preferredFragment,
        double? preferredInlineCoordinate)
    {
        if (ResolveLine(position, preferredLine, preferredFragment) is not { } line)
            return null;
        BRect bounds = line.CaretBounds(_document.OffsetAt(position), preferredInlineCoordinate);
        return new RichEditCaretGeometry(Id(line), bounds.Height > 0 ? bounds.Left : line.Bounds.Left, bounds);
    }

    public IEnumerable<BRect> GetSelectionBounds(int fragment, RichTextRange range, BRect clip)
    {
        if ((uint)fragment >= (uint)_pages.PageCount || range.IsEmpty || clip.IsEmpty)
            yield break;
        foreach (BRect bounds in WriterPageOccurrences.SelectionBoxes(_pages.Layout.Pages[fragment], _document, range))
        {
            BRect visible = bounds.Intersect(clip);
            if (!visible.IsEmpty)
                yield return visible;
        }
    }

    private RichEditLineId Id(Line line) => new(_revision, line.PageIndex, line.Order);

    private RichEditLineGeometry Describe(Line line) => new(Id(line),
        _document.PositionAt(line.Paragraph, line.Start), _document.PositionAt(line.Paragraph, line.End),
        line.Bounds.Left, line.Bounds.Top, line.Bounds.Height);

    private Line? ResolveId(RichEditLineId id) =>
        id.Revision == _revision && (uint)id.Index < (uint)_lines.Count && _lines[id.Index].PageIndex == id.Fragment
            ? _lines[id.Index] : null;

    private Line? ResolveLine(RichTextPosition position, RichEditLineId? preferredLine, int? preferredPage)
    {
        int paragraph = _document.ParagraphIndexAt(position);
        int offset = _document.OffsetAt(position);
        if (preferredLine is { } id && ResolveId(id) is { } line && line.Paragraph == paragraph &&
            offset >= line.Start && offset <= line.End)
            return line;
        return Find(paragraph, offset, preferredPage);
    }

    private Line? Find(int paragraph, int offset, int? preferredPage)
    {
        if (!_byParagraph.TryGetValue(paragraph, out var lines))
            return null;
        int lo = 0;
        int hi = lines.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (lines[mid].Start <= offset) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0)
            return null;
        int start = lines[lo - 1].Start;
        int first = lo - 1;
        while (first > 0 && lines[first - 1].Start == start)
            first--;
        for (int i = first; i < lo; i++)
            if (lines[i].PageIndex == preferredPage)
                return lines[i];
        return lines[first];
    }

    private sealed class Line(
        int order, int pageIndex, WriterPageOccurrence occurrence,
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
        WriterPageOccurrences.SourceSpan[] spans)
#else
        RichTextParagraph paragraph, (LayoutPiece Piece, int Length)[] pieces, bool horizontalLtr)
#endif
    {
        internal int Order { get; } = order;
        internal int PageIndex { get; } = pageIndex;
        internal int Paragraph => occurrence.Paragraph;
        internal BRect Bounds => occurrence.Bounds;
#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
        internal int Start { get; } = spans.Length == 0 ? occurrence.Offset : spans.Min(span => span.Start);
        internal int End { get; } = spans.Length == 0 ? occurrence.Offset : spans.Max(span => span.End);

        internal BRect CaretBounds(int offset, double? preferredInlineCoordinate)
        {
            if (spans.Length == 0)
                return new BRect(Bounds.Left, Bounds.Top, 1, Bounds.Height);
            double? best = null;
            foreach (var span in spans)
            {
                if (offset >= span.Start && offset <= span.End)
                {
                    double x = offset == span.End ? span.EndX : span.StartX;
                    if (preferredInlineCoordinate is not double preferred)
                        return new BRect(x, Bounds.Top, 1, Bounds.Height);
                    if (best is null || Math.Abs(x - preferred) < Math.Abs(best.Value - preferred))
                        best = x;
                }
            }
            return best is double edge ? new BRect(edge, Bounds.Top, 1, Bounds.Height) : BRect.Empty;
        }

        internal int OffsetAt(double x) => WriterPageOccurrences.OffsetAt(spans, x, Start);
#else
        internal int Start { get; } = pieces.Length == 0 ? 0 : pieces.Min(item => item.Piece.SourceOffset);
        internal int End { get; } = pieces.Length == 0 ? 0 : pieces.Max(item => item.Piece.SourceOffset + item.Length);

        internal BRect CaretBounds(int offset, double? preferredInlineCoordinate)
        {
            if (!horizontalLtr)
                return BRect.Empty;
            if (pieces.Length == 0)
                return new BRect(Bounds.Left, Bounds.Top, 1, Bounds.Height);
            foreach (var (piece, length) in pieces)
            {
                int within = offset - piece.SourceOffset;
                if (within < 0 || within > length)
                    continue;
                double x = piece.X + WriterPageOccurrences.PieceAdvance(piece, length, within);
                return new BRect(Math.Min(x, Bounds.Right), Bounds.Top, 1, Bounds.Height);
            }
            return BRect.Empty;
        }

        internal int OffsetAt(double x)
        {
            if (!horizontalLtr || pieces.Length == 0)
                return Start;
            int offset = Start;
            foreach (var (piece, length) in pieces)
            {
                if (x < piece.X)
                    break;
                if (x < piece.X + piece.Width)
                    return Math.Min(piece.SourceOffset + length, WriterPageOccurrences.FindOffset(paragraph, piece, x - piece.X));
                offset = piece.SourceOffset + length;
            }
            return offset;
        }
#endif
    }
}
