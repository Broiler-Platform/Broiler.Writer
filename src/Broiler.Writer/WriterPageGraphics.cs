using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.Input.Touch;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterPageLayout
{
    internal static BRect TransformObject(BRect box, double dx, double dy, int horizontal, int vertical, bool aspectLocked)
    {
        if (horizontal == 0 && vertical == 0) return new(box.Left + dx, box.Top + dy, box.Width, box.Height);
        double width = Math.Clamp(box.Width + dx * horizontal, 1, 2000);
        double height = Math.Clamp(box.Height + dy * vertical, 1, 2000);
        if (aspectLocked && box.Width > 0 && box.Height > 0)
        {
            double widthScale = width / box.Width, heightScale = height / box.Height;
            double scale = horizontal == 0 ? heightScale : vertical == 0 ? widthScale
                : Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1) ? widthScale : heightScale;
            scale = Math.Clamp(scale, Math.Max(1 / box.Width, 1 / box.Height), Math.Min(2000 / box.Width, 2000 / box.Height));
            width = box.Width * scale; height = box.Height * scale;
        }
        return new(box.Left + (horizontal < 0 ? box.Width - width : 0),
            box.Top + (vertical < 0 ? box.Height - height : 0), width, height);
    }

    internal sealed partial class PageLayoutContent
    {
        private StandardButton _objects = null!, _objectProperties = null!;
        private UiWindow? _graphicsOwner;
        private BSize _graphicsViewport;
        private bool _graphicsMode;
        private IReadOnlyDictionary<int, DocumentShape> _graphicsSources = new Dictionary<int, DocumentShape>();
        private Drag? _objectDrag;
        private BRect? _objectGhost;
        private sealed record Drag(RichTextDocument Document, DocumentShape Shape, BRect Bounds, BPoint Start, int Horizontal, int Vertical, long? Contact);

        internal void InitializeGraphics(UiWindow owner, BSize viewport) { _graphicsOwner = owner; _graphicsViewport = viewport; }
        private void CreateGraphicsButtons()
        {
            _objects = Button("Edit objects", BeginGraphics);
            _objectProperties = Button("Properties", () =>
            {
                if (_graphicsOwner is not null)
                    WriterFormattingDialog.ShowSelectedGraphics(_graphicsOwner, _graphicsViewport, _body);
            });
            AddChild(_objects); AddChild(_objectProperties);
        }

        internal void BeginGraphics()
        {
            _graphicsMode = !_graphicsMode;
            _objects.Text = _graphicsMode ? "Finish objects" : "Edit objects";
            _preview.ToolTipText = _graphicsMode
                ? "Drag objects or their handles. Arrows move; Alt+arrows resize; Shift uses ten points. Tab selects objects."
                : "Click text to place the caret. Shift-click to select text.";
            _objectDrag = null; _objectGhost = null;
            Focusable = _graphicsMode;
            RebuildGraphics();
            if (_graphicsMode)
            {
                Session?.SetFocus(this);
                Session?.AnnounceStatus(this, "Select an object. Drag to move; use the handles to resize. Arrows move, Alt+arrows resize, Shift uses ten points. Tab selects the next object. Escape cancels a drag.");
            }
            else FocusPreview();
            UpdateNavigation();
        }

        private void RebuildGraphics()
        {
            int page = _preview.PageIndex;
            var previous = _pages;
            _snapshot = _body.Document;
            if (_graphicsMode)
            {
                var scene = WriterGraphics.CreateScene(_snapshot, _fontFamily);
                _pages = scene.Pages; _graphicsSources = scene.Sources;
            }
            else { _pages = Paginate(_snapshot, _fontFamily); _graphicsSources = new Dictionary<int, DocumentShape>(); }
            _preview.Pages = _pages;
            _preview.PageIndex = Math.Min(page, _pages.PageCount - 1);
            if (_graphicsMode) RevealSelectedObject();
            previous.Dispose();
            Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
        }

        private void SyncGraphics()
        {
            if (!ReferenceEquals(_snapshot, _body.Document))
            {
                _objectDrag = null; _objectGhost = null;
                RebuildGraphics();
            }
            UpdateNavigation();
        }

        private IEnumerable<(LayoutShape Placed, DocumentShape Source)> PageObjects() =>
            _preview.PageCount == 0 ? [] : _pages.Layout.Pages[_preview.PageIndex].Shapes
                .Where(shape => _graphicsSources.ContainsKey(shape.ZOrder))
                .Select(shape => (shape, _graphicsSources[shape.ZOrder]));

        private void RevealSelectedObject()
        {
            var selected = WriterGraphics.Selected(_body);
            if (selected is null || PageObjects().Any(item => ReferenceEquals(item.Source, selected))) return;
            for (int page = 0; page < _pages.PageCount; page++)
                if (_pages.Layout.Pages[page].Shapes.Any(shape => _graphicsSources.TryGetValue(shape.ZOrder, out var source) && ReferenceEquals(source, selected)))
                { _preview.PageIndex = page; return; }
        }

        private static IEnumerable<(BPoint Point, int X, int Y)> Handles(BRect bounds)
        {
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                    if (x != 0 || y != 0)
                        yield return (new(bounds.Left + bounds.Width * (x + 1) / 2, bounds.Top + bounds.Height * (y + 1) / 2), x, y);
        }

        private BPoint PagePoint(BPoint point)
        {
            var paper = _preview.PageBounds; var size = _pages.GetPage(_preview.PageIndex).Size;
            return new((point.X - paper.Left) * size.Width / paper.Width, (point.Y - paper.Top) * size.Height / paper.Height);
        }

        private bool HandleGraphicsPointer(UiInputEvent input)
        {
            if (!_graphicsMode) return false;
            if (Session?.ModalElement?.Children.Contains(this) != true) return true;
            if (_preview.PageCount == 0 || _preview.PageBounds.IsEmpty) return true;
            bool touch = input.Kind == UiInputEventKind.TouchContact;
            bool down = touch ? input.TouchContactState == TouchContactState.Pressed :
                input.Kind == UiInputEventKind.PointerButton && input.MouseButton == MouseButton.Left && input.MouseButtonTransition == MouseButtonTransition.Down;
            bool up = touch ? input.TouchContactState == TouchContactState.Released :
                input.Kind == UiInputEventKind.PointerButton && input.MouseButton == MouseButton.Left && input.MouseButtonTransition == MouseButtonTransition.Up;
            bool move = input.Kind == UiInputEventKind.PointerMove || touch && input.TouchContactState == TouchContactState.Moved;
            if (touch && input.TouchContactState == TouchContactState.Cancelled && (_objectDrag is null || _objectDrag.Contact == input.ContactId))
            { _objectDrag = null; _objectGhost = null; Invalidate(UiInvalidationKind.Render); return true; }
            if (_objectDrag is { } dragging)
            {
                if (dragging.Contact != (touch ? input.ContactId : null)) return true;
                if (move || up)
                {
                    var point = PagePoint(input.Position);
                    _objectGhost = TransformObject(dragging.Bounds, point.X - dragging.Start.X, point.Y - dragging.Start.Y,
                        dragging.Horizontal, dragging.Vertical, dragging.Shape.Image is not null && WriterGraphics.AspectLocked(_body));
                    if (up)
                    {
                        var box = _objectGhost.Value;
                        _objectDrag = null; _objectGhost = null;
                        if (box != dragging.Bounds)
                            WriterGraphics.Apply(_body, dragging.Document, dragging.Shape, WriterGraphics.Copy(dragging.Shape,
                                x: dragging.Shape.OffsetX + box.Left - dragging.Bounds.Left,
                                y: dragging.Shape.OffsetY + box.Top - dragging.Bounds.Top, width: box.Width, height: box.Height));
                        SyncGraphics();
                    }
                    Invalidate(UiInvalidationKind.Render);
                }
                return true;
            }
            if (!down || !_preview.PageBounds.Contains(input.Position)) return true;
            SyncGraphics();
            var hit = PagePoint(input.Position);
            double tolerance = (touch ? 12 : 7) * _pages.GetPage(_preview.PageIndex).Size.Width / _preview.PageBounds.Width;
            var selected = WriterGraphics.Selected(_body);
            var current = PageObjects().FirstOrDefault(item => ReferenceEquals(item.Source, selected));
            int hx = 0, hy = 0;
            if (current.Source is not null && WriterGraphics.Editable(current.Source))
                foreach (var handle in Handles(current.Placed.Bounds))
                    if (Math.Abs(hit.X - handle.Point.X) <= tolerance && Math.Abs(hit.Y - handle.Point.Y) <= tolerance)
                    { hx = handle.X; hy = handle.Y; break; }
            if (hx == 0 && hy == 0)
                current = PageObjects().OrderByDescending(item => !item.Placed.BehindText).ThenByDescending(item => item.Placed.ZOrder)
                    .FirstOrDefault(item => item.Placed.Bounds.Contains(hit));
            WriterGraphics.Select(_body, current.Source);
            if (current.Source is not null && _body.IsEnabled && !_body.IsReadOnly && WriterGraphics.Editable(current.Source))
                _objectDrag = new(_snapshot, current.Source, current.Placed.Bounds, hit, hx, hy, touch ? input.ContactId : null);
            Session?.SetFocus(this);
            UpdateNavigation();
            Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic);
            return true;
        }

        private bool HandleGraphicsKey(UiInputEvent input)
        {
            if (input.Kind != UiInputEventKind.KeyboardKey || input.KeyTransition != KeyboardKeyTransition.Down || Session?.FocusedElement != this) return false;
            if (input.KeyName == "Escape")
            { _objectDrag = null; _objectGhost = null; Invalidate(UiInvalidationKind.Render); return true; }
            if (input.KeyName == "Tab")
            { WriterGraphics.Next(_body, input.KeyModifiers.HasFlag(KeyboardModifierState.Shift) ? -1 : 1); RevealSelectedObject(); UpdateNavigation(); Invalidate(UiInvalidationKind.Render | UiInvalidationKind.Semantic); return true; }
            if (input.KeyName is "PageUp" or "PageDown")
            { _preview.PageIndex += input.KeyName == "PageUp" ? -1 : 1; return true; }
            if (_body.IsReadOnly || !_body.IsEnabled) return false;
            if (input.KeyModifiers.HasFlag(KeyboardModifierState.Control) && input.KeyName is "z" or "Z")
            { _body.ExecuteCommand(RichEditCommand.Undo); SyncGraphics(); return true; }
            if (WriterGraphics.Selected(_body) is not { } shape || !WriterGraphics.Editable(shape)) return false;
            if (input.KeyName == "Delete")
            { WriterGraphics.Apply(_body, _snapshot, shape, null); SyncGraphics(); return true; }
            if (input.KeyName == "Enter") { _objectProperties.Click(); return true; }
            if (input.KeyName is not ("Left" or "Right" or "Up" or "Down")) return false;
            double step = input.KeyModifiers.HasFlag(KeyboardModifierState.Shift) ? 10 : 1;
            double dx = input.KeyName == "Left" ? -step : input.KeyName == "Right" ? step : 0;
            double dy = input.KeyName == "Up" ? -step : input.KeyName == "Down" ? step : 0;
            bool resize = input.KeyModifiers.HasFlag(KeyboardModifierState.Alt);
            var bounds = TransformObject(new(0, 0, shape.Width, shape.Height), dx, dy, resize && dx != 0 ? 1 : 0, resize && dy != 0 ? 1 : 0,
                resize && shape.Image is not null && WriterGraphics.AspectLocked(_body));
            WriterGraphics.Apply(_body, _snapshot, shape, resize ? WriterGraphics.Copy(shape, width: bounds.Width, height: bounds.Height)
                : WriterGraphics.Copy(shape, x: shape.OffsetX + dx, y: shape.OffsetY + dy));
            SyncGraphics();
            return true;
        }

        private void PaintGraphics(UiRenderContext context)
        {
            if (Session?.ModalElement?.Children.Contains(this) != true) return;
            if (WriterGraphics.Selected(_body) is not { } selected) return;
            var occurrence = PageObjects().FirstOrDefault(item => ReferenceEquals(item.Source, selected));
            if (occurrence.Source is null) return;
            var box = _objectGhost ?? occurrence.Placed.Bounds;
            var paper = _preview.PageBounds; var size = _pages.GetPage(_preview.PageIndex).Size;
            BPoint ToControl(BPoint p) => new(paper.Left + p.X * paper.Width / size.Width, paper.Top + p.Y * paper.Height / size.Height);
            var top = ToControl(new(box.Left, box.Top)); var bottom = ToControl(new(box.Right, box.Bottom));
            context.RenderList.PushClip(paper);
            context.RenderList.StrokeRect(new(top.X, top.Y, bottom.X - top.X, bottom.Y - top.Y), WriterPalette.Accent, 2);
            if (!_body.IsReadOnly && WriterGraphics.Editable(selected))
                foreach (var handle in Handles(box))
                {
                    var point = ToControl(handle.Point);
                    context.RenderList.FillRect(new(point.X - 4, point.Y - 4, 8, 8), BColor.White);
                    context.RenderList.StrokeRect(new(point.X - 4, point.Y - 4, 8, 8), WriterPalette.Accent, 1);
                }
            context.RenderList.PopClip();
        }

        private IEnumerable<UiSemanticNode> GraphicsSemantics()
        {
            if (!_graphicsMode || _preview.PageCount == 0) yield break;
            var paper = _preview.PageBounds; var page = _pages.Layout.Pages[_preview.PageIndex];
            foreach (var item in PageObjects())
            {
                var box = item.Placed.Bounds;
                var bounds = new BRect(paper.Left + box.Left * paper.Width / page.WidthPoints,
                    paper.Top + box.Top * paper.Height / page.HeightPoints, box.Width * paper.Width / page.WidthPoints, box.Height * paper.Height / page.HeightPoints);
                string label = item.Source.Image?.AltText ?? string.Join(" ", item.Source.Paragraphs.Select(paragraph => paragraph.Text));
                if (string.IsNullOrWhiteSpace(label)) label = item.Source.Geometry.ToString();
                if (label.Length > 200) label = label[..200];
                yield return new UiSemanticNode(UiSemanticRole.ImageView, label, bounds,
                    UiSemanticState.Visible | (ReferenceEquals(item.Source, WriterGraphics.Selected(_body)) ? UiSemanticState.Selected : UiSemanticState.None), [])
                { Description = "Floating object. Tab selects objects; arrows move; Alt+arrows resize; Enter opens properties." };
            }
        }
    }
}
