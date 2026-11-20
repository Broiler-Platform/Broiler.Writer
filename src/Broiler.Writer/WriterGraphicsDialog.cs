using System;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI.Menu;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    internal static UiMenuItem CreateGraphicsMenu(StandardCommandDispatcher dispatcher, UiRichEdit editor,
        Func<UiWindow> owner, Func<BSize> viewport, Action<Action> dispatch)
    {
        var menu = new UiMenuItem("graphics", "Graphics") { AccessKey = 'G' };
        bool Editable() => editor.IsEnabled && !editor.IsReadOnly;
        bool HasShape() => WriterGraphics.Selected(editor) is { } shape && WriterGraphics.Editable(shape);
        InlineImage? CurrentPicture() => WriterGraphics.InlineAt(editor)?.Image ?? (HasShape() ? WriterGraphics.Selected(editor)?.Image : null);
        bool HasPicture() => CurrentPicture() is not null;
        Add("insert-shape", "Insert shape or text box...", () => ShowInsertShape(owner(), viewport(), editor), Editable);
        Add("float-picture", "Float selected inline picture", () => FloatPicture(editor), () => Editable() && WriterGraphics.InlineAt(editor) is not null);
        menu.Children.Add(new UiMenuItem("selection-separator", "") { IsSeparator = true });
        Add("next", "Select next floating object", () => WriterGraphics.Next(editor), () => WriterGraphics.Shapes(editor.Document).Any());
        menu.Children.Add(new UiMenuItem("picture-separator", "") { IsSeparator = true });
        Add("picture", "Picture properties...", () => ShowPictureProperties(owner(), viewport(), editor), () => Editable() && HasPicture());
        Add("intrinsic", "Use intrinsic picture size", () => RestoreIntrinsic(editor), () => Editable() &&
            CurrentPicture() is { } image && WriterGraphics.Intrinsic(image) is not null);
        menu.Children.Add(new UiMenuItem("object-separator", "") { IsSeparator = true });
        Add("placement", "Object size and position...", () => ShowShapePlacement(owner(), viewport(), editor), () => Editable() && HasShape());
        Add("wrap", "Object text wrapping...", () => ShowShapeWrap(owner(), viewport(), editor), () => Editable() && HasShape());
        Add("text", "Edit text box...", () => ShowShapeText(owner(), viewport(), editor), () => Editable() && HasShape() && WriterGraphics.Selected(editor)!.Image is null);
        menu.Children.Add(new UiMenuItem("delete-separator", "") { IsSeparator = true });
        Add("delete", "Delete selected floating object", () =>
        {
            if (WriterGraphics.Selected(editor) is { } shape) WriterGraphics.Apply(editor, editor.Document, shape, null);
        }, () => Editable() && HasShape());
        menu.Children.Add(new UiMenuItem("pages-separator", "") { IsSeparator = true });
        Add("pages", "Edit objects on page...", () => WriterPageLayout.Show(owner(), viewport(), editor,
            editor is StandardRichEdit rich ? rich.Font.FamilyName : "Segoe UI",
            editGraphics: true), () => WriterGraphics.Shapes(editor.Document).Any());
        return menu;

        void Add(string id, string label, Action action, Func<bool> enabled)
        {
            string command = "graphics." + id;
            dispatcher.Add(new StandardCommand(command, () => dispatch(action), enabled));
            menu.Children.Add(new UiMenuItem(command, label) { CommandName = command });
        }
    }

    internal static void ShowSelectedGraphics(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (editor.IsReadOnly || WriterGraphics.Selected(editor) is not { } shape || !WriterGraphics.Editable(shape)) return;
        if (shape.Image is not null) ShowPictureProperties(owner, viewport, editor, floatingOnly: true);
        else ShowShapePlacement(owner, viewport, editor);
    }

    private static void ShowInsertShape(UiWindow owner, BSize viewport, UiRichEdit editor) =>
        PageForm(owner, viewport, editor, "Insert shape or text box", "The object is anchored to the current paragraph. Edit its position in Page Layout.",
            [("Kind (rectangle, ellipse, textbox)", "textbox"), ("Width, height in points", "240, 120"), ("Initial text", "Text box")],
            (document, selection, values) =>
            {
                string kind = values[0].ToLowerInvariant();
                if (kind is not ("rectangle" or "ellipse" or "textbox") || !GraphicsSize(values[1], out double[] size))
                    return "Choose rectangle, ellipse or textbox and two positive sizes up to 2000 points.";
                var shape = new DocumentShape(document.ParagraphIndexAt(selection.Focus), 0, 0, size[0], size[1],
                    ShapeFill.Solid(BColor.White), BColor.Black, kind == "textbox" ? [RichTextParagraph.Plain(values[2])] : [],
                    behindText: false, wrap: ShapeWrap.Square,
                    textPadding: new(6, 6, 6, 6), geometry: kind == "ellipse" ? ShapeGeometry.Ellipse : ShapeGeometry.Rectangle)
                { IsTextFrame = kind == "textbox" };
                editor.ApplyDocumentEdit(document, document.WithShapes([.. document.Shapes, shape]), selection);
                WriterGraphics.Select(editor, shape);
                return null;
            });

    private static void FloatPicture(UiRichEdit editor)
    {
        if (WriterGraphics.InlineAt(editor) is not { } picture) return;
        var document = editor.Document;
        var size = PictureSize(picture.Image);
        var shape = new DocumentShape(document.ParagraphIndexAt(picture.Range.Start), 0, 0, size.Width, size.Height,
            image: picture.Image, behindText: false, wrap: ShapeWrap.Square);
        var removed = document.DeleteRange(picture.Range);
        if (editor.ApplyDocumentEdit(document, removed.Document.WithShapes([.. removed.Document.Shapes, shape]), RichTextRange.Caret(removed.Caret)))
            WriterGraphics.Select(editor, shape);
    }

    private static (double Width, double Height) PictureSize(InlineImage image) =>
        image.TryGetDisplaySize(out double width, out double height) ? (width, height) : (120, 90);

    private static bool GraphicsSize(string text, out double[] size) => Numbers(text, 2, out size) && size.All(value => value > 0 && value <= 2000);

    private static void ShowPictureProperties(UiWindow owner, BSize viewport, UiRichEdit editor, bool floatingOnly = false)
    {
        var inline = floatingOnly ? null : WriterGraphics.InlineAt(editor);
        var shape = inline is null ? WriterGraphics.Selected(editor) : null;
        var image = inline?.Image ?? shape?.Image;
        if (image is null) return;
        var intrinsic = WriterGraphics.Intrinsic(image);
        var size = shape is null ? PictureSize(image) : (shape.Width, shape.Height);
        string description = image.IsMissingPayload ? "Picture unavailable: its payload is missing or was blocked. Metadata can still be edited."
            : intrinsic is { } known ? $"Intrinsic size: {PointsText(known.Width, known.Height)} points. Crop values are percentages of the source."
            : "Intrinsic size unavailable. Crop values are percentages of the source.";
        PageForm(owner, viewport, editor, "Picture properties", description,
            [("Width, height in points", PointsText(size.Item1, size.Item2)),
             ("Keep aspect ratio when resizing (yes or no)", WriterGraphics.AspectLocked(editor) ? "yes" : "no"),
             ("Alternative text", image.AltText),
             ("Crop left, top, right, bottom (%)", PointsText(image.Presentation.CropLeft * 100, image.Presentation.CropTop * 100,
                 image.Presentation.CropRight * 100, image.Presentation.CropBottom * 100))],
            (document, selection, values) =>
            {
                if (!GraphicsSize(values[0], out double[] next) || !Flag(values[1], out bool? locked) || locked is null ||
                    !Numbers(values[3], 4, out double[] crop) || crop[0] + crop[2] >= 100 || crop[1] + crop[3] >= 100)
                    return "Use positive sizes, yes or no, and crop edges that leave some of the source visible.";
                if (locked.Value && size.Item1 > 0 && size.Item2 > 0)
                {
                    bool changedWidth = Math.Abs(next[0] - size.Item1) > .01, changedHeight = Math.Abs(next[1] - size.Item2) > .01;
                    if (changedWidth && !changedHeight) next[1] = next[0] * size.Item2 / size.Item1;
                    else if (changedHeight && !changedWidth) next[0] = next[1] * size.Item1 / size.Item2;
                    else if (Math.Abs(next[0] / next[1] - size.Item1 / size.Item2) > .005)
                        return "Change one dimension, or enter dimensions with the same aspect ratio.";
                    if (next.Any(value => value > 2000)) return "The locked dimensions exceed 2000 points.";
                }
                var updated = image.WithSize(next[0], next[1]).WithAltText(values[2]).WithPresentation(image.Presentation with
                    { CropLeft = crop[0] / 100, CropTop = crop[1] / 100, CropRight = crop[2] / 100, CropBottom = crop[3] / 100 });
                if (shape is not null)
                    WriterGraphics.Apply(editor, document, shape, WriterGraphics.Copy(shape, width: next[0], height: next[1], image: updated));
                else
                    editor.ApplyDocumentEdit(document, WriterGraphics.ReplaceInline(document, inline!, updated), selection);
                WriterGraphics.SetAspectLocked(editor, locked.Value);
                return null;
            });
    }

    private static void RestoreIntrinsic(UiRichEdit editor)
    {
        var inline = WriterGraphics.InlineAt(editor);
        var shape = inline is null ? WriterGraphics.Selected(editor) : null;
        var image = inline?.Image ?? shape?.Image;
        if (image is null || WriterGraphics.Intrinsic(image) is not { } size) return;
        if (shape is not null)
            WriterGraphics.Apply(editor, editor.Document, shape, WriterGraphics.Copy(shape, width: size.Width, height: size.Height,
                image: image.WithSize(size.Width, size.Height)));
        else editor.ApplyDocumentEdit(editor.Document, WriterGraphics.ReplaceInline(editor.Document, inline!, image.WithSize(size.Width, size.Height)), editor.Selection);
    }

    private static void ShowShapePlacement(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterGraphics.Selected(editor) is not { } shape) return;
        bool running = !editor.Document.Shapes.Contains(shape);
        string mode = shape.IsPageAnchored ? "page" : shape.OffsetFromPage ? "paragraph-page" : shape.OffsetXFromColumn ? "column" : "paragraph";
        PageForm(owner, viewport, editor, "Object size and position", running
                ? "This object repeats with its header/footer. Keep its anchor; position changes apply to every occurrence."
                : "X uses the text area/column; Y uses the paragraph or page top.",
            [("X, Y in points", PointsText(shape.OffsetX, shape.OffsetY)), ("Width, height in points", PointsText(shape.Width, shape.Height)),
             (running ? "Anchor (keep for header/footer objects)" : "Anchor (paragraph, column, paragraph-page or page)", running ? "keep" : mode),
             (running ? "Existing anchor number (leave unchanged)" : "Anchor paragraph or physical page (1 based)", (shape.IsPageAnchored ? shape.AnchorPage : shape.ParagraphIndex + 1).ToString(CultureInfo.InvariantCulture)),
             ("Behind text (yes or no)", shape.BehindText ? "yes" : "no"), ("Stacking order (0 to 1000000)", shape.ZOrder.ToString(CultureInfo.InvariantCulture))],
            (document, selection, values) =>
            {
                if (!SignedPoints(values[0], 2, out double[] position) || !GraphicsSize(values[1], out double[] size) ||
                    !int.TryParse(values[3], out int anchor) || anchor < 1 || anchor > 1_000_000 ||
                    !Flag(values[4], out bool? behind) || behind is null ||
                    !int.TryParse(values[5], out int z) || z is < 0 or > 1_000_000)
                    return "Check the position, positive sizes, anchor number, behind-text flag and stacking order.";
                string kind = values[2].ToLowerInvariant();
                if (running ? kind != "keep" : kind is not ("paragraph" or "column" or "paragraph-page" or "page"))
                    return "Choose a supported anchor mode. Running objects keep their existing anchor.";
                if (running && anchor != (shape.IsPageAnchored ? shape.AnchorPage : shape.ParagraphIndex + 1))
                    return "Keep the existing running-object anchor number. Change X and Y to reposition it.";
                if (!running && kind != "page" && anchor > document.ParagraphCount) return "Choose an existing anchor paragraph.";
                var replacement = WriterGraphics.Copy(shape, x: position[0], y: position[1], width: size[0], height: size[1],
                    behind: behind, z: z, paragraph: running ? null : kind == "page" ? shape.ParagraphIndex : anchor - 1,
                    page: running ? null : kind == "page" ? anchor : 0,
                    fromPage: running ? null : kind == "paragraph-page", fromColumn: running ? null : kind == "column", resetAnchor: !running);
                WriterGraphics.Apply(editor, document, shape, replacement);
                return null;
            });
    }

    private static bool SignedPoints(string value, int count, out double[] points)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        points = new double[count];
        if (parts.Length != count) return false;
        for (int index = 0; index < count; index++)
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out points[index]) ||
                !double.IsFinite(points[index]) || Math.Abs(points[index]) > 2000) return false;
        return true;
    }

    private static void ShowShapeWrap(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterGraphics.Selected(editor) is not { } shape) return;
        PageForm(owner, viewport, editor, "Object text wrapping", "Page Layout and print use the shared document exclusion geometry.",
            [("Wrap (none, square, topandbottom)", shape.Wrap.ToString()), ("Text side (largest, left, right)", shape.WrapSide.ToString()),
             ("Clearance in points", PointsText(shape.WrapDistance))],
            (document, selection, values) =>
            {
                if (!Enum.TryParse(values[0], true, out ShapeWrap wrap) || !Enum.IsDefined(wrap) ||
                    !Enum.TryParse(values[1], true, out WrapSide side) || !Enum.IsDefined(side) ||
                    !Numbers(values[2], 1, out double[] distance) || distance[0] > 200)
                    return "Choose valid wrap and side values, with clearance from 0 to 200 points.";
                WriterGraphics.Apply(editor, document, shape, WriterGraphics.Copy(shape, wrap: wrap, side: side, distance: distance[0]));
                return null;
            });
    }

    private static void ShowShapeText(UiWindow owner, BSize viewport, UiRichEdit editor)
    {
        if (WriterGraphics.Selected(editor) is not { } shape) return;
        var document = editor.Document;
        var text = RichTextDocument.FromParagraphs(shape.Paragraphs.Count > 0 ? shape.Paragraphs : [RichTextParagraph.Empty]).WithTables(shape.Tables);
        ShowRichRunningBand(owner, viewport, editor, "Text box", text, updated =>
            WriterGraphics.Apply(editor, document, shape, WriterGraphics.Copy(shape, paragraphs: updated.Paragraphs, tables: updated.Tables)));
    }
}
