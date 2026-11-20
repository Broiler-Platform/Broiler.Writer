using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Broiler.Documents.Model;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI.RichEdit;

namespace Broiler.Writer;

internal static class WriterGraphics
{
    internal static readonly bool TracksParagraphAnchors = ProbeAnchorTracking();
    private static bool ProbeAnchorTracking()
    {
        var source = RichTextDocument.FromPlainText("One\nTwo").WithShapes([new DocumentShape(1, 0, 0, 10, 10)]);
        return source.SplitParagraph(source.PositionAt(0, 1)).Document.Shapes[0].ParagraphIndex == 2;
    }
    private sealed class Selection { internal int Index = -1; internal bool AspectLocked = true; }
    private static readonly ConditionalWeakTable<UiRichEdit, Selection> Selections = new();
    internal static bool AspectLocked(UiRichEdit editor) => Selections.GetOrCreateValue(editor).AspectLocked;
    internal static void SetAspectLocked(UiRichEdit editor, bool locked) => Selections.GetOrCreateValue(editor).AspectLocked = locked;
    internal static DocumentShape? Selected(UiRichEdit editor) => Shapes(editor.Document).ElementAtOrDefault(Selections.GetOrCreateValue(editor).Index);
    internal static void Select(UiRichEdit editor, DocumentShape? shape) =>
        Selections.GetOrCreateValue(editor).Index = shape is null ? -1 : Array.IndexOf(Shapes(editor.Document).ToArray(), shape);
    internal static DocumentShape? Next(UiRichEdit editor, int direction = 1)
    {
        var shapes = Shapes(editor.Document).ToArray();
        if (shapes.Length == 0) return null;
        var state = Selections.GetOrCreateValue(editor);
        state.Index = ((state.Index + direction) % shapes.Length + shapes.Length) % shapes.Length;
        return shapes[state.Index];
    }

    internal static IEnumerable<DocumentShape> Shapes(RichTextDocument document) =>
        document.Shapes.Concat(RunningShapes(document.RunningContent))
            .Concat(document.Sections.SelectMany(section =>
                (section.RunningContent is { } running ? RunningShapes(running) : [])
                    .Concat(section.PageStyles?.Styles.SelectMany(style => RunningShapes(style.RunningContent)) ?? [])))
            .Distinct();

    private static IEnumerable<DocumentShape> RunningShapes(RunningContent running) =>
        Enum.GetValues<PageSelection>().SelectMany(variant => running.HeaderShapes(variant).Concat(running.FooterShapes(variant)));

    internal static bool Editable(DocumentShape shape) => shape.Width > 0 && shape.Height > 0 &&
        double.IsFinite(shape.Width + shape.Height + shape.OffsetX + shape.OffsetY) && !shape.IsAsCharacter && !shape.InlineInSource &&
        shape.RotationRadians == 0 && shape.GroupNumber == 0 && shape.NextFrameName is null &&
        !shape.FitsText && !shape.ShrinksWidthToText && !shape.GrowsWidthToText;

    // Copy all model properties, including imported properties that the dialogs do not edit.
    internal static DocumentShape Copy(DocumentShape shape, double? x = null, double? y = null,
        double? width = null, double? height = null, InlineImage? image = null,
        IReadOnlyList<RichTextParagraph>? paragraphs = null, IReadOnlyList<DocumentTable>? tables = null,
        ShapeWrap? wrap = null, WrapSide? side = null, double? distance = null, int? z = null,
        bool? behind = null, int? paragraph = null, int? page = null, bool? fromPage = null,
        bool? fromColumn = null, bool resetAnchor = false) =>
        new(paragraph ?? shape.ParagraphIndex, x ?? shape.OffsetX, y ?? shape.OffsetY, width ?? shape.Width, height ?? shape.Height,
            shape.Fill, shape.Outline, paragraphs ?? shape.Paragraphs, image ?? shape.Image,
            behind ?? shape.BehindText, wrap ?? shape.Wrap, side ?? shape.WrapSide, distance ?? shape.WrapDistance,
            z ?? shape.ZOrder, page ?? shape.AnchorPage, fromPage ?? shape.OffsetFromPage, height is null && shape.GrowsToText,
            tables ?? shape.Tables, shape.TextPadding, shape.TextVerticalAlignment, shape.TextOverflow,
            width is null && shape.GrowsWidthToText, shape.FrameName, shape.NextFrameName, shape.Geometry, shape.OutlineWidth,
            resetAnchor ? false : shape.OffsetXFromCell, resetAnchor ? null : shape.CellHorizontalAlignment, shape.Printable)
        {
            IsAsCharacter = shape.IsAsCharacter, IsCustomShape = shape.IsCustomShape, IsTextFrame = shape.IsTextFrame,
            InlineInSource = shape.InlineInSource, GroupNumber = shape.GroupNumber, FitsText = width is null && height is null && shape.FitsText,
            ShrinksWidthToText = width is null && shape.ShrinksWidthToText, RotationRadians = shape.RotationRadians,
            TextFlow = shape.TextFlow, StretchesTextToBox = shape.StretchesTextToBox, TextOutline = shape.TextOutline,
            TextOutlineWidth = shape.TextOutlineWidth,
            WrapMargins = distance is { } clearance ? new TableCellPadding(clearance, clearance, clearance, clearance) : shape.WrapMargins,
            WrapPolygon = shape.WrapPolygon,
            OutlinePaths = shape.OutlinePaths, MinimumWrapRoom = shape.MinimumWrapRoom, BoundedByPage = shape.BoundedByPage,
            OffsetXFromColumn = fromColumn ?? shape.OffsetXFromColumn, KeptBelowPageTop = shape.KeptBelowPageTop,
            OffsetFromBandParagraph = resetAnchor ? null : shape.OffsetFromBandParagraph is { } band && y is { } nextY ? band + nextY - shape.OffsetY : shape.OffsetFromBandParagraph,
            DrawsInFront = behind is null ? shape.DrawsInFront : !behind.Value,
        };

    internal static RichTextDocument MapShapes(RichTextDocument document, Func<DocumentShape, DocumentShape?> map)
    {
        var mapped = new Dictionary<DocumentShape, DocumentShape?>();
        IReadOnlyList<DocumentShape> Items(IReadOnlyList<DocumentShape> source) => source.Select(shape =>
        {
            if (!mapped.TryGetValue(shape, out var result)) mapped[shape] = result = map(shape);
            return result;
        }).OfType<DocumentShape>().ToArray();
        RunningContent Running(RunningContent running)
        {
            var result = running;
            foreach (var variant in Enum.GetValues<PageSelection>())
            {
                if (running.HeaderShapes(variant).Count > 0)
                {
                    result = result.WithHeader(variant, running.Header(variant), Items(running.HeaderShapes(variant)), running.HeaderTables(variant));
                    if (running.IsLinked(variant, true)) result = result.WithLink(variant, true);
                }
                if (running.FooterShapes(variant).Count > 0)
                {
                    result = result.WithFooter(variant, running.Footer(variant), Items(running.FooterShapes(variant)), running.FooterTables(variant));
                    if (running.IsLinked(variant, false)) result = result.WithLink(variant, false);
                }
            }
            return result;
        }
        return document.WithShapes(Items(document.Shapes)).WithRunningContent(Running(document.RunningContent))
            .WithSections(document.Sections.Select(section => new DocumentSection(section.ParagraphIndex, section.ParagraphCount,
                section.ColumnCount, section.ColumnGap, section.Page,
                section.RunningContent is { } running ? Running(running) : null, section.Background,
                section.PageStyles is { } styles ? new PageStyleSequence(styles.Styles.Select(style =>
                    style with { RunningContent = Running(style.RunningContent) }).ToArray(), styles.RepeatFrom) : null,
                section.Hidden, section.DontBalanceColumns, section.BreakType, section.LeftIndent, section.RightIndent, section.PageNumberStart)
            { PageNumberFormat = section.PageNumberFormat, FootnotesEnding = section.FootnotesEnding, ColumnSeparator = section.ColumnSeparator }).ToArray());
    }

    internal static bool Apply(UiRichEdit editor, RichTextDocument original, DocumentShape target, DocumentShape? replacement)
    {
        if (editor.IsReadOnly || !editor.IsEnabled || !ReferenceEquals(editor.Document, original) || !Shapes(original).Contains(target)) return false;
        int index = Array.IndexOf(Shapes(original).ToArray(), target);
        var updated = MapShapes(original, shape => ReferenceEquals(shape, target) ? replacement : shape);
        bool applied = editor.ApplyDocumentEdit(original, updated, editor.Selection);
        if (applied) Selections.GetOrCreateValue(editor).Index = replacement is null ? -1 : index;
        return applied;
    }

    internal sealed record Picture(RichTextRange Range, InlineImage Image, InlineStyle Style);
    internal static Picture? InlineAt(UiRichEdit editor)
    {
        var document = editor.Document;
        int paragraph = document.ParagraphIndexAt(editor.Selection.Start), offset = document.OffsetAt(editor.Selection.Start);
        var text = document.Paragraphs[paragraph];
        if (offset >= text.Length || text.Text[offset] != InlineImage.Placeholder)
        {
            if (!editor.Selection.IsEmpty || offset == 0 || text.Text[offset - 1] != InlineImage.Placeholder) return null;
            offset--;
        }
        var style = text.StyleAt(offset);
        return style.Image is { } image ? new(new(document.PositionAt(paragraph, offset), document.PositionAt(paragraph, offset + 1)), image, style) : null;
    }

    internal static RichTextDocument ReplaceInline(RichTextDocument document, Picture picture, InlineImage image) =>
        document.DeleteRange(picture.Range).Document.InsertText(picture.Range.Start, InlineImage.PlaceholderText,
            picture.Style with { Image = image }).Document;

    internal static (double Width, double Height)? Intrinsic(InlineImage image) =>
        image.Resource.PixelWidth is > 0 && image.Resource.PixelHeight is > 0
            ? (image.Resource.PixelWidth.Value * 72d / 96, image.Resource.PixelHeight.Value * 72d / 96) : null;

    internal sealed record Scene(PaginatedDocument Pages, IReadOnlyDictionary<int, DocumentShape> Sources);
    internal static Scene CreateScene(RichTextDocument document, string fontFamily)
    {
        // Published pagination artifacts retain z-order but no source-object ID.
        // Assign ordered, unique tokens ONLY in this disposable editing snapshot.
        // Original z-order and document data are never changed by selecting an object.
        var ordered = Shapes(document).OrderBy(shape => shape.ZOrder).ToArray();
        var tokens = ordered.Select((shape, index) => (shape, Token: index + 1)).ToDictionary(item => item.shape, item => item.Token);
        var layoutDocument = MapShapes(document, shape => shape.WithPlacement(shape.ParagraphIndex, shape.OffsetX,
            shape.OffsetY, shape.AnchorPage, shape.OffsetFromPage, shape.Wrap, tokens[shape]));
        var paginator = WriterPageLayout.CreatePaginator(fontFamily, editorGeometry: true, drawNonPrintingShapes: true);
        return new(paginator.Paginate(layoutDocument, DocumentPaginator.PreferredSetup(layoutDocument)),
            tokens.ToDictionary(item => item.Value, item => item.Key));
    }
}
