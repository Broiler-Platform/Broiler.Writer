using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.Model;
using Broiler.Documents.Odt;
using Broiler.Documents.Pagination;
using Broiler.Documents.Resources;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.Input.Touch;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.PrintPreview.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterGraphicsTests
{
    private static InlineImage Image() => new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQv8BD/kD/YURmXYAAAAASUVORK5CYII="), "image/png", 100, 50, "Original");
    private static WriterUiHost Host() => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
    private static readonly InputEventHeader Input = new(InputDeviceId.FromOpaqueValue("graphics-tests"), new InputTimestamp(1, 1000, "test"), 1);

    [Fact]
    public void Inline_Properties_Edit_Only_One_Shared_Image_Occurrence_And_Undo()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var image = Image();
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create(InlineImage.PlaceholderText + InlineImage.PlaceholderText,
            InlineStyle.Default with { Image = image })]);
        var original = app.Document;
        Submit(app, "picture", "200, 50", "yes", "Updated alternative text", "10, 0, 20, 0");
        var first = app.Document.Paragraphs[0].StyleAt(0).Image!;
        Assert.Equal(200, first.Width); Assert.Equal(100, first.Height);
        Assert.Equal("Updated alternative text", first.AltText);
        Assert.Equal(.1, first.Presentation.CropLeft);
        Assert.Equal(.2, first.Presentation.CropRight);
        Assert.Same(image, app.Document.Paragraphs[0].StyleAt(1).Image);
        Assert.Equal(original.PlainText, app.Document.PlainText);
        Run(app, "edit.undo"); Assert.Same(original, app.Document);
        Run(app, "edit.redo");
        Run(app, "graphics.intrinsic");
        Assert.Equal(1.5, app.Document.Paragraphs[0].StyleAt(0).Image!.Width);
        Assert.Equal(.75, app.Document.Paragraphs[0].StyleAt(0).Image!.Height);
    }

    [Fact]
    public void Native_Commands_Insert_Format_And_Edit_A_Text_Box_As_Undo_Units()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Anchor\nAfter");
        var original = app.Document;
        Submit(app, "insert-shape", "textbox", "180, 90", "Box content");
        var inserted = app.Document;
        Assert.True(Assert.Single(inserted.Shapes).IsTextFrame);
        Submit(app, "placement", "30, 40", "200, 100", "column", "2", "no", "7");
        Submit(app, "wrap", "square", "right", "8");
        var shape = Assert.Single(app.Document.Shapes);
        Assert.Equal(1, shape.ParagraphIndex); Assert.True(shape.OffsetXFromColumn);
        Assert.Equal(30, shape.OffsetX); Assert.Equal(40, shape.OffsetY); Assert.Equal(7, shape.ZOrder);
        Assert.Equal(ShapeWrap.Square, shape.Wrap); Assert.Equal(WrapSide.Right, shape.WrapSide);
        Assert.Equal(8, shape.WrapDistance); Assert.False(shape.BehindText);
        Run(app, "graphics.text");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children);
        var edit = Assert.Single(content.Children.OfType<Broiler.UI.RichEdit.Standard.StandardRichEdit>());
        edit.SetPlainText("Edited box\nSecond line");
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
        Assert.Equal(new[] { "Edited box", "Second line" }, app.Document.Shapes[0].Paragraphs.Select(paragraph => paragraph.Text));
        Run(app, "edit.undo"); Assert.Equal("Box content", app.Document.Shapes[0].Paragraphs[0].Text);
        Run(app, "edit.undo"); Run(app, "edit.undo"); Assert.Same(inserted, app.Document);
        Run(app, "edit.undo"); Assert.Same(original, app.Document);
    }

    [Fact]
    public void Floating_Picture_And_Text_Box_Follow_A_Paragraph_Split_And_Save_Reopen_Print()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var resources = new DocumentConversionContextBuilder(DocumentResourcePolicy.AllowOwnDocuments);
        var picture = resources.AdmitImage(Image(), DocumentResourceProvenance.CallerSupplied, DocumentResourceDisposition.Embedded);
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("Before"),
            RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = picture })]);
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(1, 0));
        Run(app, "graphics.float-picture");
        Submit(app, "placement", "200, 0", "100, 50", "paragraph", "2", "no", "2");
        Submit(app, "insert-shape", "textbox", "120, 70", "Wrapped box");
        Submit(app, "placement", "0, 0", "120, 70", "paragraph", "2", "no", "1");
        var before = app.Document;
        app.Editor.Selection = RichTextRange.Caret(app.Document.PositionAt(0, 3));
        app.Editor.ReplaceTextRange(app.Editor.Selection, "\n");
        if (WriterGraphics.TracksParagraphAnchors)
            Assert.All(app.Document.Shapes, shape => Assert.Equal(2, shape.ParagraphIndex));
        else
            Assert.Contains(WriterViewDiagnostics.Collect(app.Document), note => note.Code == "writer.view.graphics-anchors");
        // Move the anchors explicitly through the authoring controls as well as splitting text.
        // Automatic tracking of the split is covered by the Documents model regression tests.
        WriterGraphics.Select(app.Editor, app.Document.Shapes[0]);
        Submit(app, "placement", "200, 0", "100, 50", "paragraph", "3", "no", "2");
        WriterGraphics.Select(app.Editor, app.Document.Shapes[1]);
        Submit(app, "placement", "0, 0", "120, 70", "paragraph", "3", "no", "1");
        var split = app.Document;
        Assert.All(split.Shapes, shape => Assert.Equal(2, shape.ParagraphIndex));
        Run(app, "edit.undo"); Run(app, "edit.undo"); Run(app, "edit.undo"); Assert.Same(before, app.Document);
        Run(app, "edit.redo"); Run(app, "edit.redo"); Run(app, "edit.redo"); Assert.Same(split, app.Document);
        foreach (DocumentCodec codec in new DocumentCodec[] { new DocxDocumentCodec(), new OdtDocumentCodec() })
        {
            using var stream = new MemoryStream(); codec.Write(split, stream, new DocumentWriteOptions(resources: resources.Build())); stream.Position = 0;
            var restored = codec.Read(stream, new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments)).Document;
            Assert.Equal(split.PlainText, restored.PlainText);
            Assert.Equal(2, restored.Shapes.Count);
            Assert.All(restored.Shapes, shape => Assert.Equal(2, shape.ParagraphIndex));
            Assert.Contains(restored.Shapes, shape => shape.Image?.AltText == "Original" && shape.Wrap == ShapeWrap.Square);
            Assert.Contains(restored.Shapes, shape => shape.Paragraphs.Any(paragraph => paragraph.Text == "Wrapped box"));
            Assert.Null(WriterGraphicsFidelity.DescribeFirstChange(split, restored));
            using var pages = WriterPageLayout.Paginate(restored, app.Editor.Font.FamilyName);
            Assert.Equal(2, pages.Layout.Pages.SelectMany(page => page.Shapes).Count());
            Assert.Contains(pages.GetPage(0).Commands.OfType<BRenderCommand.DrawText>(), command => command.Text.Text == "Wrapped");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Page_Drag_Commits_One_Undo_And_Handle_Resize_Uses_Page_Scale(bool touch)
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var shape = new DocumentShape(0, 30, 20, 100, 60, fill: ShapeFill.Solid(BColor.White), behindText: false);
        app.Editor.Document = RichTextDocument.FromPlainText("Anchor").WithPageGeometry(PageGeometry.A4).WithShapes([shape]);
        WriterGraphics.Select(app.Editor, shape);
        var original = app.Document;
        Run(app, "graphics.pages");
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(dialog.Children); var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>());
        app.RenderFrame();
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        var box = Assert.Single(pages.Layout.Pages[0].Shapes).Bounds;
        Drag(new(box.Left + 30, box.Top + 20), new(box.Left + 55, box.Top + 35));
        Assert.Equal(55, app.Document.Shapes[0].OffsetX, 5); Assert.Equal(35, app.Document.Shapes[0].OffsetY, 5);
        var moved = app.Document;
        app.RenderFrame();
        box = Assert.Single(Assert.IsType<PaginatedDocument>(preview.Pages).Layout.Pages[0].Shapes).Bounds;
        Drag(new(box.Right, box.Bottom), new(box.Right + 20, box.Bottom + 10));
        Assert.Equal(120, app.Document.Shapes[0].Width, 5); Assert.Equal(70, app.Document.Shapes[0].Height, 5);
        var resized = app.Document;
        app.RenderFrame();
        box = Assert.Single(Assert.IsType<PaginatedDocument>(preview.Pages).Layout.Pages[0].Shapes).Bounds;
        Contact(new(box.Left + 30, box.Top + 20), 0);
        Contact(new(box.Left + 50, box.Top + 40), 1);
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(Input, KeyboardKey.FromName("Escape"),
            KeyboardKeyTransition.Down, KeyboardModifierState.None, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1, IsExtended: false, WasDown: false)));
        Contact(new(box.Left + 50, box.Top + 40), 2);
        Assert.Same(resized, app.Document);
        dialog.Cancel();
        Run(app, "edit.undo"); Assert.Same(moved, app.Document);
        Run(app, "edit.undo"); Assert.Same(original, app.Document);

        void Drag(BPoint start, BPoint end)
        {
            var beforeDrag = app.Document;
            Contact(start, 0); Contact(end, 1); Assert.Same(beforeDrag, app.Document); // provisional until release
            Contact(end, 2);
        }
        void Contact(BPoint point, int phase)
        {
            var page = Assert.IsType<PaginatedDocument>(preview.Pages).Layout.Pages[preview.PageIndex]; var paper = preview.PageBounds;
            var position = InputPoint.ClientDeviceIndependentPixels(paper.Left + point.X * paper.Width / page.WidthPoints,
                paper.Top + point.Y * paper.Height / page.HeightPoints);
            if (touch) app.Dispatch(UiInputEvent.FromTouchContact(new TouchContactEvent(Input, 1, position,
                phase == 0 ? TouchContactState.Pressed : phase == 1 ? TouchContactState.Moved : TouchContactState.Released)));
            else if (phase == 1) app.Dispatch(UiInputEvent.FromMouseMove(new MouseMoveEvent(Input, position, MouseButtons.Left)));
            else app.Dispatch(UiInputEvent.FromMouseButton(new MouseButtonEvent(Input, position, MouseButtons.Left, MouseButton.Left,
                phase == 0 ? MouseButtonTransition.Down : MouseButtonTransition.Up)));
        }
    }

    [Fact]
    public void Scene_Identifies_Repeated_Header_And_Column_Objects_Without_Changing_The_Document()
    {
        var body = new DocumentShape(1, 10, 5, 40, 20, zOrder: 3) { OffsetXFromColumn = true };
        var pageObject = new DocumentShape(0, 15, 50, 30, 15, fill: ShapeFill.Solid(BColor.White), zOrder: 3, anchorPage: 2);
        var header = new DocumentShape(0, 20, 15, 50, 25, zOrder: 3);
        var document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("One"), RichTextParagraph.Plain("Two")
            .WithParagraphStyle(ParagraphStyle.Default with { ColumnBreakBefore = true }), RichTextParagraph.Plain("Third")
            .WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true })])
            .WithPageGeometry(PageGeometry.A4 with { ColumnCount = 2, ColumnGap = 18 }).WithShapes([body, pageObject])
            .WithRunningContent(RunningContent.Empty.WithHeader(PageSelection.Default, [RichTextParagraph.Plain("Heading")], [header]));
        using var expected = WriterPageLayout.Paginate(document, "Segoe UI");
        var scene = WriterGraphics.CreateScene(document, "Segoe UI"); using var pages = scene.Pages;
        Assert.Equal(expected.PageCount, pages.PageCount);
        Assert.True(pages.Layout.Pages[0].Shapes.Single(shape => ReferenceEquals(scene.Sources[shape.ZOrder], body)).Bounds.Left > PageGeometry.A4.Width / 2);
        Assert.Contains(pages.Layout.Pages[1].Shapes, shape => ReferenceEquals(scene.Sources[shape.ZOrder], pageObject));
        for (int index = 0; index < pages.PageCount; index++)
        {
            Assert.Equal(expected.Layout.Pages[index].Shapes.Select(shape => shape.Bounds), pages.Layout.Pages[index].Shapes.Select(shape => shape.Bounds));
            Assert.Contains(pages.Layout.Pages[index].Shapes, shape => ReferenceEquals(scene.Sources[shape.ZOrder], header));
        }
        Assert.Equal(3, body.ZOrder); Assert.Equal(3, header.ZOrder);
        using var host = Host(); using var app = new WriterApp(host, () => { }); app.Editor.Document = document;
        WriterGraphics.Apply(app.Editor, document, header, WriterGraphics.Copy(header, x: 35));
        Assert.Same(body, app.Document.Shapes[0]);
        Assert.Equal(35, app.Document.RunningContent.HeaderShapes(PageSelection.Default)[0].OffsetX);
    }

    [Fact]
    public void Page_Properties_Edit_The_Selected_Floating_Picture_Above_An_Inline_Caret()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var image = Image(); var shape = new DocumentShape(0, 200, 0, 100, 50, image: image);
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create(InlineImage.PlaceholderText,
            InlineStyle.Default with { Image = image })]).WithShapes([shape]);
        WriterGraphics.Select(app.Editor, shape); Run(app, "graphics.pages"); app.RenderFrame();
        var pageDialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        var content = Assert.Single(pageDialog.Children);
        Assert.Single(content.Children.OfType<StandardButton>(), button => button.Text == "Properties").Click();
        Assert.DoesNotContain(app.RenderFrame().Commands.OfType<BRenderCommand.StrokeRect>(), command =>
            command.Color == WriterPalette.Accent && command.Rect.Width == 8 && command.Rect.Height == 8);
        Fill(app, "150, 75", "yes", "Floating picture", "0, 0, 0, 0");
        Assert.Same(pageDialog, app.Session.ModalElement);
        Assert.Equal("Floating picture", app.Document.Shapes[0].Image!.AltText);
        Assert.Equal(150, app.Document.Shapes[0].Width);
        Assert.Same(image, app.Document.Paragraphs[0].StyleAt(0).Image);
        app.RenderFrame();
    }

    [Fact]
    public void Aspect_Locked_Corner_Resize_Follows_The_Dominant_Axis()
    {
        var original = new BRect(10, 20, 100, 50);
        Assert.Equal(new BRect(10, 20, 150, 75), WriterPageLayout.TransformObject(original, 0, 25, 1, 1, true));
        Assert.Equal(new BRect(-40, -5, 150, 75), WriterPageLayout.TransformObject(original, -50, 0, -1, -1, true));
    }

    [Fact]
    public void Docx_Crop_Roundtrips_And_Odt_Crop_Loss_Is_Reported()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var resources = new DocumentConversionContextBuilder(DocumentResourcePolicy.AllowOwnDocuments);
        var image = resources.AdmitImage(Image(), DocumentResourceProvenance.CallerSupplied, DocumentResourceDisposition.Embedded);
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image })]);
        Submit(app, "picture", "150, 80", "no", "Cropped logo", "10, 20, 15, 5");
        foreach (DocumentCodec codec in new DocumentCodec[] { new DocxDocumentCodec(), new OdtDocumentCodec() })
        {
            using var stream = new MemoryStream(); codec.Write(app.Document, stream, new DocumentWriteOptions(resources: resources.Build())); stream.Position = 0;
            var restored = codec.Read(stream, new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments)).Document;
            if (codec is DocxDocumentCodec)
                Assert.Null(WriterGraphicsFidelity.DescribeFirstChange(app.Document, restored));
            else
                Assert.Contains("cropping", WriterGraphicsFidelity.DescribeFirstChange(app.Document, restored));
            var picture = WriterGraphics.InlineAt(app.Editor)!;
            var lost = WriterGraphics.ReplaceInline(app.Document, picture, picture.Image.WithAltText("Lost alt text"));
            Assert.Contains("picture", WriterGraphicsFidelity.DescribeFirstChange(app.Document, lost));
        }
    }

    [Fact]
    public void Object_Keyboard_Movement_Resize_And_ReadOnly_Checks_Preserve_Undo()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var shape = new DocumentShape(0, 20, 20, 100, 50, image: Image());
        app.Editor.Document = RichTextDocument.FromPlainText("Anchor").WithShapes([shape]);
        var original = app.Document;
        WriterGraphics.Select(app.Editor, shape); Run(app, "graphics.pages"); app.RenderFrame();
        Key("Right", KeyboardModifierState.Shift); Assert.Equal(30, app.Document.Shapes[0].OffsetX);
        Key("Down", KeyboardModifierState.Alt); Assert.Equal(102, app.Document.Shapes[0].Width); Assert.Equal(51, app.Document.Shapes[0].Height);
        Key("Z", KeyboardModifierState.Control); Assert.Equal(100, app.Document.Shapes[0].Width);
        Key("Z", KeyboardModifierState.Control); Assert.Same(original, app.Document);
        app.Editor.IsReadOnly = true;
        Key("Right", KeyboardModifierState.None); Assert.Same(original, app.Document);
        Key("Delete", KeyboardModifierState.None); Assert.Same(original, app.Document);
        void Key(string name, KeyboardModifierState modifiers) => app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(Input,
            KeyboardKey.FromName(name), KeyboardKeyTransition.Down, modifiers, NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1, IsExtended: false, WasDown: false)));
    }

    [Fact]
    public void Touching_A_Repeated_Header_Object_Updates_Its_Source_On_Every_Page()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var header = new DocumentShape(0, 20, 15, 100, 30, fill: ShapeFill.Solid(BColor.White));
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Plain("First"), RichTextParagraph.Plain("Second")
            .WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true })]).WithPageGeometry(PageGeometry.A4)
            .WithRunningContent(RunningContent.Empty.WithHeader(PageSelection.Default, [RichTextParagraph.Plain("Heading")], [header]));
        var original = app.Document;
        Run(app, "graphics.pages");
        var content = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children);
        var preview = Assert.Single(content.Children.OfType<StandardPrintPreview>()); preview.PageIndex = 1; app.RenderFrame();
        var pages = Assert.IsType<PaginatedDocument>(preview.Pages); var page = pages.Layout.Pages[1]; var box = Assert.Single(page.Shapes).Bounds;
        Contact(new(box.Left + 40, box.Top + 15), TouchContactState.Pressed);
        Contact(new(box.Left + 60, box.Top + 25), TouchContactState.Moved);
        Assert.Same(original, app.Document);
        Contact(new(box.Left + 60, box.Top + 25), TouchContactState.Released);
        Assert.Empty(app.Document.Shapes); Assert.Equal(40, app.Document.RunningContent.HeaderShapes(PageSelection.Default)[0].OffsetX, 5);
        Assert.Equal(25, app.Document.RunningContent.HeaderShapes(PageSelection.Default)[0].OffsetY, 5);
        app.RenderFrame();
        pages = Assert.IsType<PaginatedDocument>(preview.Pages);
        Assert.All(pages.Layout.Pages, item => Assert.Equal(box.Left + 20, Assert.Single(item.Shapes).Bounds.Left, 5));
        Assert.Contains(content.GetSemanticNode().Children, node => node.Role == UiSemanticRole.ImageView && node.State.HasFlag(UiSemanticState.Selected));
        void Contact(BPoint point, TouchContactState state)
        {
            var paper = preview.PageBounds;
            app.Dispatch(UiInputEvent.FromTouchContact(new TouchContactEvent(Input, 7,
                InputPoint.ClientDeviceIndependentPixels(paper.Left + point.X * paper.Width / page.WidthPoints,
                    paper.Top + point.Y * paper.Height / page.HeightPoints), state)));
        }
    }

    [Fact]
    public void Missing_Or_Blocked_Picture_Has_A_Diagnostic_And_Invalid_Crop_Does_Not_Commit()
    {
        using var host = Host(); using var app = new WriterApp(host, () => { });
        var image = InlineImage.CreateMissingPayload(100, 50, "Unavailable logo");
        app.Editor.Document = RichTextDocument.FromParagraphs([RichTextParagraph.Create(InlineImage.PlaceholderText, InlineStyle.Default with { Image = image })]);
        var original = app.Document;
        Assert.Contains(WriterViewDiagnostics.Collect(original), note => note.Code == "writer.view.picture-unavailable");
        Run(app, "graphics.picture"); Fill(app, "100, 50", "no", "Still unavailable", "60, 0, 50, 0");
        Assert.NotNull(app.Session.ModalElement); Assert.Same(original, app.Document);
        Fill(app, "100, 50", "no", "Still unavailable", "0, 0, 0, 0");
        Assert.True(app.Document.Paragraphs[0].StyleAt(0).Image!.IsMissingPayload);
    }

    private static void Run(WriterApp app, string command) => Assert.True(app.Menu.CommandDispatcher!.TryExecute(command));
    private static void Submit(WriterApp app, string command, params string[] values)
    { Run(app, "graphics." + command); Fill(app, values); Assert.Null(app.Session.ModalElement); }
    private static void Fill(WriterApp app, params string[] values)
    {
        var children = Assert.Single(Assert.IsType<StandardDialog>(app.Session.ModalElement).Children).Children;
        var edits = children.OfType<StandardEdit>().ToArray(); Assert.Equal(values.Length, edits.Length);
        for (int index = 0; index < values.Length; index++) edits[index].Text = values[index];
        Assert.Single(children.OfType<StandardButton>(), button => button.Text == "Apply").Click();
    }
}
