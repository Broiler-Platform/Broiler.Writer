using System;
using Broiler.Graphics;

namespace Broiler.Writer.FormatCodes;

/// <summary>
/// The Writer's toolbar icons, drawn as geometry rather than loaded as pictures.
/// </summary>
/// <remarks>
/// There is no image file and no icon font here, and that is the point: an icon recorded as a
/// handful of render commands is resolution-independent for free, takes the foreground colour the
/// control has already worked out for its state, and costs nothing to ship. At twenty icons the
/// drawing cost is not worth a thought.
///
/// It lives in this assembly because it is the only project below the application that both the
/// desktop Writer and the browser Writer already reference, and those two carry hand-maintained
/// copies of the whole shell. Anything put in either one of them would have to be written twice.
///
/// Everything is authored on a <see cref="DesignExtent"/>-unit square and scaled into whatever box
/// the control gives it, so the same source draws a 16 DIP bar icon and a 20 DIP one on a compact
/// toolbar. Letters are deliberately absent - B, I, U, S and the zoom steps are glyphs in the
/// toolbar itself, because a letterform is a thing a font does better than geometry.
/// </remarks>
public static class WriterIcons
{
    /// <summary>The side of the square every icon is authored on.</summary>
    public const double DesignExtent = 16;

    /// <summary>The weight of a drawn line, in design units.</summary>
    private const double Stroke = 1.4;

    public static void NewDocument(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // A page with its top-right corner turned down. The outline stops short of the corner on
        // two sides and the fold fills the gap, which is what makes it read as paper rather than
        // as a rectangle.
        pen.Bar(3, 1.5, 6, Stroke);
        pen.Bar(3, 13.1, 9, Stroke);
        pen.Column(3, 1.5, 13, Stroke);
        pen.Column(10.6, 5.2, 9.3, Stroke);
        pen.Triangle(9, 1.5, 12, 4.5, 9, 4.5);
    }

    public static void OpenDocument(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // A folder, solid: at this size an outlined one closes up into a grey smudge. The tab is
        // separated from the body by a hairline of background rather than merged into it, which is
        // the only thing that stops the whole shape reading as a rounded blob.
        pen.Rect(1.5, 2.6, 6.2, 2.2);
        pen.Rect(1.5, 5.2, 13, 8.2);
    }

    public static void Save(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // A diskette: outline, shutter at the top, label at the bottom. Nobody has seen one for
        // twenty years and everybody still knows what it means.
        pen.Outline(2, 2, 12, 12, Stroke);
        pen.Rect(5.5, 3.4, 5, 3.4);
        pen.Rect(4.6, 8.8, 6.8, 3.2);
    }

    public static void SaveAs(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // The same diskette, smaller, with a plus clear of its corner: saving, but somewhere else.
        // The plus has to stand off the body - overlapping it turns both into one unreadable mass.
        pen.Outline(0.8, 1, 9, 9, Stroke);
        pen.Rect(3.4, 2, 3.8, 2.4);
        pen.Rect(2.8, 6, 5, 2.2);
        pen.Bar(10.2, 12.6, 5.6, Stroke);
        pen.Column(12.3, 9.8, 5.6, Stroke);
    }

    public static void Undo(BRenderList list, BRect box, BColor color) => Arrow(list, box, color, back: true);

    public static void Redo(BRenderList list, BRect box, BColor color) => Arrow(list, box, color, back: false);

    /// <summary>
    /// The curved arrow both undo and redo are. The arc is a circle stroked through a clip that
    /// keeps its upper half; the head is a triangle on the end the arc stops at.
    /// </summary>
    private static void Arrow(BRenderList list, BRect box, BColor color, bool back)
    {
        var pen = new Pen(list, box, color);

        pen.Clip(0, 2.6, DesignExtent, 6.2);
        pen.Circle(2.6, 2.8, 10.8, Stroke);
        pen.Unclip();

        // The tail drops from the end of the arc, and the head sits at the bottom of the tail. The
        // head is kept narrow enough to stay inside the icon's box: the bar packs these a few
        // pixels apart, so anything that overruns paints over the button beside it.
        double tip = back ? 2.6 : 13.4;
        pen.Column(tip - (Stroke / 2), 8.2, 2.2, Stroke);
        pen.Triangle(tip - 2.2, 9.8, tip + 2.2, 9.8, tip, 13.6);
    }

    public static void ClearFormatting(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // A letter - crossbar and stem, the shape every word processor uses for "text" - with a
        // stroke taken through it. The letter is drawn large enough to still be a letter once the
        // stroke crosses it, which the first attempt at this was not.
        pen.Bar(2.4, 3, 8.8, Stroke);
        pen.Column(6.1, 3, 9.6, Stroke);
        pen.Line(1.6, 13.4, 14.4, 3.4, 1.3);
    }

    public static void AlignLeft(BRenderList list, BRect box, BColor color) =>
        Paragraph(list, box, color, Align.Left);

    public static void AlignCenter(BRenderList list, BRect box, BColor color) =>
        Paragraph(list, box, color, Align.Center);

    public static void AlignRight(BRenderList list, BRect box, BColor color) =>
        Paragraph(list, box, color, Align.Right);

    public static void AlignJustify(BRenderList list, BRect box, BColor color) =>
        Paragraph(list, box, color, Align.Justify);

    private enum Align
    {
        Left,
        Center,
        Right,
        Justify,
    }

    /// <summary>
    /// Four lines of text with one short line among them, placed where the alignment would put it.
    /// The short line is the whole icon: four full-width bars would say nothing.
    /// </summary>
    private static void Paragraph(BRenderList list, BRect box, BColor color, Align align)
    {
        var pen = new Pen(list, box, color);
        const double full = 12;
        const double part = 8;
        const double left = 2;

        for (int row = 0; row < 4; row++)
        {
            double y = 2.4 + (row * 3.2);
            bool shortRow = row is 1 or 3;
            if (!shortRow || align == Align.Justify)
            {
                pen.Bar(left, y, full, Stroke);
                continue;
            }

            double x = align switch
            {
                Align.Center => left + ((full - part) / 2),
                Align.Right => left + (full - part),
                _ => left,
            };
            pen.Bar(x, y, part, Stroke);
        }
    }

    public static void BulletList(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);
        for (int row = 0; row < 3; row++)
        {
            double y = 3 + (row * 4.4);
            pen.Dot(1.6, y - 0.7, 2.8);
            pen.Bar(6.4, y, 8, Stroke);
        }
    }

    public static void NumberedList(BRenderList list, BRect box, BColor color)
    {
        var pen = new Pen(list, box, color);

        // The numerals are the one place a font beats geometry: three legible digits inside five
        // design units is what type is for, and hand-built ones read as noise at this size.
        var font = new BFontStyle("Segoe UI", box.Height * (4.6 / DesignExtent));
        for (int row = 0; row < 3; row++)
        {
            double y = 3 + (row * 4.4);
            pen.Text((row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 1.4, y - 2.4, font);
            pen.Bar(6.4, y, 8, Stroke);
        }
    }

    public static void Indent(BRenderList list, BRect box, BColor color) => Shift(list, box, color, right: true);

    public static void Outdent(BRenderList list, BRect box, BColor color) => Shift(list, box, color, right: false);

    /// <summary>
    /// Lines of text with an arrow saying which way they are about to move. The two middle lines
    /// are inset, so the icon shows the indent as well as naming it.
    /// </summary>
    private static void Shift(BRenderList list, BRect box, BColor color, bool right)
    {
        var pen = new Pen(list, box, color);
        pen.Bar(2, 2.2, 12, Stroke);
        pen.Bar(7, 6.4, 7, Stroke);
        pen.Bar(7, 9.4, 7, Stroke);
        pen.Bar(2, 13.4, 12, Stroke);

        if (right)
            pen.Triangle(1.8, 6.2, 5, 8.4, 1.8, 10.6);
        else
            pen.Triangle(5, 6.2, 5, 10.6, 1.8, 8.4);
    }

    /// <summary>
    /// Maps the design grid onto the box an icon was handed, and records the primitives. Every
    /// method takes design units; nothing above this ever sees a device coordinate.
    /// </summary>
    private readonly struct Pen(BRenderList list, BRect box, BColor color)
    {
        private readonly double _scale = box.Width / DesignExtent;

        private double X(double x) => box.Left + (x * _scale);

        private double Y(double y) => box.Top + (y * _scale);

        private double S(double v) => v * _scale;

        /// <summary>A horizontal line.</summary>
        public void Bar(double x, double y, double length, double thickness) =>
            Rect(x, y - (thickness / 2), length, thickness);

        /// <summary>A vertical line.</summary>
        public void Column(double x, double y, double length, double thickness) =>
            Rect(x, y, thickness, length);

        public void Rect(double x, double y, double width, double height) =>
            list.FillRect(new BRect(X(x), Y(y), S(width), S(height)), color);

        /// <summary>A filled circle, which a rounded rectangle is when its radius is half its side.</summary>
        public void Dot(double x, double y, double diameter) =>
            list.FillRoundedRect(
                new BRect(X(x), Y(y), S(diameter), S(diameter)),
                color,
                S(diameter / 2),
                S(diameter / 2));

        /// <summary>An unfilled circle.</summary>
        public void Circle(double x, double y, double diameter, double thickness) =>
            list.StrokeRoundedRect(
                new BRect(X(x), Y(y), S(diameter), S(diameter)),
                color,
                S(diameter / 2),
                S(diameter / 2),
                S(thickness));

        /// <summary>A rectangular outline, drawn as its four sides.</summary>
        public void Outline(double x, double y, double width, double height, double thickness)
        {
            Rect(x, y, width, thickness);
            Rect(x, y + height - thickness, width, thickness);
            Rect(x, y, thickness, height);
            Rect(x + width - thickness, y, thickness, height);
        }

        public void Triangle(double ax, double ay, double bx, double by, double cx, double cy) =>
            list.FillTriangle(new BPoint(X(ax), Y(ay)), new BPoint(X(bx), Y(by)), new BPoint(X(cx), Y(cy)), color);

        /// <summary>
        /// A line at any angle, as the two triangles of the quad that runs along it. This is why the
        /// render list needed a triangle: a rectangle turned to the same angle is not portable,
        /// because most backends reduce a rotated shape to the box around it.
        /// </summary>
        public void Line(double ax, double ay, double bx, double by, double thickness)
        {
            double dx = bx - ax;
            double dy = by - ay;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0)
                return;

            double nx = -dy / length * (thickness / 2);
            double ny = dx / length * (thickness / 2);

            Triangle(ax + nx, ay + ny, bx + nx, by + ny, bx - nx, by - ny);
            Triangle(ax + nx, ay + ny, bx - nx, by - ny, ax - nx, ay - ny);
        }

        public void Text(string text, double x, double y, BFontStyle font) =>
            list.DrawText(new BTextRun(text, font, color), new BPoint(X(x), Y(y)));

        public void Clip(double x, double y, double width, double height) =>
            list.PushClip(new BRect(X(x), Y(y), S(width), S(height)));

        public void Unclip() => list.PopClip();
    }
}
