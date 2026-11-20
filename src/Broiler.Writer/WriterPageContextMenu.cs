using System;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

internal static partial class WriterPageLayout
{
    internal sealed partial class PageLayoutContent
    {
        private bool OpenPageContextMenu(BPoint point)
        {
            if (!_embedded || _body is not StandardRichEdit editor || !editor.IsEnabled ||
                !Bounds.Contains(point) || _preview.PageCount == 0 || !_preview.PageBounds.Contains(point))
                return false;

            CancelComposition();
            BPoint position = PagePoint(point);
            if (!_pageGeometry.GetSelectionBounds(_preview.PageIndex, editor.Selection, PageClip(Bounds)).Any(box => box.Contains(position)) &&
                _pageGeometry.HitTest(_preview.PageIndex, position, RichEditHitTestMode.Nearest) is { } hit)
            {
                RememberCaretLine(hit.Position, hit.Line);
                editor.Selection = RichTextRange.Caret(hit.Position);
            }

            return ShowPageContextMenu(editor, point);
        }

        private bool ShowPageContextMenu(StandardRichEdit editor, BPoint point)
        {
            _dragAnchor = null;
            FocusEditor();
#if BROILER_UI_HOSTED_RICH_EDIT_MENU
            return editor.OpenContextMenu(point, this);
#else
            return OpenLegacyPageContextMenu(editor, point);
#endif
        }

        private void ClosePageContextMenu()
        {
#if BROILER_UI_HOSTED_RICH_EDIT_MENU
            if (_embedded && _body is StandardRichEdit editor)
                editor.CloseContextMenu();
#else
            if (_legacyContextMenu is { IsDisposed: false, IsOpen: true } menu)
                menu.ClosePopup();
#endif
        }

#if BROILER_UI_HOSTED_RICH_EDIT_MENU
        private bool HandleHostedPageContextMenu(UiInputEvent input)
        {
            if (_body is not StandardRichEdit editor)
                return false;

            // The page stays focused and owns capture. Forward modal input before page
            // scroll, selection, or keyboard routing can act on it.
            if (editor.IsContextMenuOpen)
                return editor.DispatchInput(input);
            if (!IsFocused || !editor.IsEnabled || !StandardRichEdit.IsContextMenuRequest(input))
                return false;

            CancelComposition();
            var caret = CaretGeometry(editor.Selection.Focus);
            BPoint anchor = Bounds.Location;
            BRect paper = _preview.PageBounds;
            if (!paper.IsEmpty && caret is { } geometry && !geometry.Bounds.IsEmpty && geometry.Line.Fragment == _preview.PageIndex)
            {
                double scale = _preview.Zoom;
                anchor = new BPoint(
                    Math.Clamp(paper.Left + geometry.Bounds.Left * scale, Bounds.Left, Bounds.Right),
                    Math.Clamp(paper.Top + geometry.Bounds.Bottom * scale + 2, Bounds.Top, Bounds.Bottom));
            }
            return ShowPageContextMenu(editor, anchor);
        }
#endif

        partial void InitializeLegacyPageContextMenu();
    }
}
