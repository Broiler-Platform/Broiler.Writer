#if !BROILER_UI_SHARED_GEOMETRY
using System.Collections.Generic;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;

namespace Broiler.Writer;

// Compatibility for standalone builds with the pinned UI packages. Remove when the
// minimum Broiler.UI.RichEdit.Standard version provides the shared contract.
internal interface IRichEditGeometry
{
    RichEditTextHit? HitTest(int fragment, BPoint point, RichEditHitTestMode mode);
    RichEditLineGeometry? GetLine(RichTextPosition position, RichEditLineId? preferredLine = null, int? preferredFragment = null);
    RichEditLineGeometry? GetRelativeLine(RichEditLineId line, int delta, bool clamp = false);
    RichTextPosition? GetPositionInLine(RichEditLineId line, double inlineCoordinate);
    RichEditCaretGeometry? GetCaret(RichTextPosition position, RichEditLineId? preferredLine = null, int? preferredFragment = null);
    IEnumerable<BRect> GetSelectionBounds(int fragment, RichTextRange range, BRect clip);
}

internal enum RichEditHitTestMode { Exact, Nearest }
internal readonly record struct RichEditLineId(int Revision, int Fragment, int Index);
internal readonly record struct RichEditLineGeometry(
    RichEditLineId Id, RichTextPosition Start, RichTextPosition End, double InlineStart, double Top, double Height);
internal readonly record struct RichEditTextHit(RichTextPosition Position, RichEditLineId? Line);
internal readonly record struct RichEditCaretGeometry(RichEditLineId Line, double InlineCoordinate, BRect Bounds);
#endif
