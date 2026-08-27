using Broiler.Documents.FormatCodes;
using Broiler.Documents.Model;
using Broiler.Graphics;
using Broiler.UI;
using Broiler.UI.FormatCodeView;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterFormatCodesControllerTests
{
    [Fact(Timeout = 600000)]
    public void Text_Edit_From_Pane_Is_Atomic_And_Undoable_From_Either_Pane()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("hello");
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        view.SetSelection(1, 4);

        Assert.True(view.RequestTextReplacement("i"));
        Assert.Equal("hio", editor.GetPlainText());
        Assert.True(editor.ExecuteCommand(RichEditCommand.Undo));
        Assert.Equal("hello", editor.GetPlainText());

        view.RequestRedoForTest();
        Assert.Equal("hio", editor.GetPlainText());
    }

    [Fact(Timeout = 600000)]
    public void Deleting_A_Code_Removes_Formatting_Without_Deleting_Text()
    {
        StandardRichEdit editor = BoldEditor("hello");
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        view.SetSelection(0, 0);

        Assert.True(view.RequestTokenRemoval());
        Assert.Equal("hello", editor.GetPlainText());
        Assert.Equal("hello", view.Text);
        Assert.True(editor.ExecuteCommand(RichEditCommand.Undo));
        Assert.Equal("[Bold ON]hello[Bold OFF]", view.Text);
    }

    [Fact(Timeout = 600000)]
    public void ReadOnly_And_Unsafe_Link_Intents_Are_Rejected()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("hello");
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        var range = new RichTextRange(editor.Document.Start, editor.Document.End);

        Assert.False(controller.ExecuteIntent(new ApplyFormatCodeInlineIntent(
            range, InlineStyleDelta.WithLink("javascript:alert(1)"))));
        Assert.Contains("FCEDIT022", controller.Status);

        editor.IsReadOnly = true;
        Assert.False(controller.ExecuteIntent(new ReplaceFormatCodeTextIntent(range, "changed")));
        Assert.Equal("hello", editor.GetPlainText());
    }

    [Fact(Timeout = 600000)]
    public void Insert_Code_Palette_Applies_Validated_Formatting()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("hello");
        editor.Selection = new RichTextRange(editor.Document.Start, editor.Document.End);
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);

        Assert.True(controller.ExecutePaletteEntry(FormatCodePaletteEntry.Bold));
        Assert.Equal("[Bold ON]hello[Bold OFF]", view.Text);
        Assert.False(controller.ExecutePaletteEntry(FormatCodePaletteEntry.Link, "file:///secret"));
    }

    [Fact(Timeout = 600000)]
    public void Scalar_Code_Property_Can_Be_Edited_Without_Parsing_Its_Label()
    {
        var editor = new StandardRichEdit
        {
            Document = RichTextDocument.FromParagraphs(
                [RichTextParagraph.Create("link", new InlineStyle
                {
                    LinkHref = "https://old.example.test",
                })]),
        };
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        FormatCodeToken link = Assert.Single(
            controller.Projection!.Tokens,
            token => token.EditDescriptor?.Property == FormatCodeProperty.Link &&
                token.DisplayText.StartsWith("[Link \"", StringComparison.Ordinal));

        Assert.True(controller.EditTokenProperty(link, "https://new.example.test"));
        Assert.Contains("https://new.example.test", view.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://old.example.test", view.Text, StringComparison.Ordinal);
    }
    [Fact(Timeout = 600000)]
    public void Initial_Projection_Uses_Canonical_Bracket_Text()
    {
        StandardRichEdit editor = BoldEditor("Hello World!");
        var view = new TestFormatCodeView();

        using var controller = CreateController(editor, view);

        Assert.Equal("[Bold ON]Hello World![Bold OFF]", view.Text);
        Assert.NotNull(controller.Projection);
    }

    [Fact(Timeout = 600000)]
    public void Document_And_Caret_Edits_Update_The_View()
    {
        var editor = new StandardRichEdit();
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);

        editor.SetPlainText("Hello");
        Assert.Equal("Hello", view.Text);

        editor.Selection = RichTextRange.Caret(editor.Document.End);
        editor.ExecuteCommand(RichEditCommand.InsertText, "!");
        Assert.Equal("Hello!", view.Text);
    }

    [Fact(Timeout = 600000)]
    public void Empty_Selection_Formatting_Is_Shown_As_Pending_Overlay()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("Hello");
        editor.Selection = RichTextRange.Caret(editor.Document.End);
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);

        editor.ExecuteCommand(RichEditCommand.Bold);

        FormatCodeToken pending = Assert.Single(controller.Projection!.PendingTokens);
        Assert.Equal(FormatCodeTokenKind.PendingCode, pending.Kind);
        Assert.Equal("[Pending Bold ON]", pending.DisplayText);
        Assert.Equal("Hello", view.Text);
    }

    [Fact(Timeout = 600000)]
    public void Moving_Caret_Clears_Stale_Pending_Overlay()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("Hello");
        editor.Selection = RichTextRange.Caret(editor.Document.End);
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        editor.ExecuteCommand(RichEditCommand.Bold);
        Assert.Single(controller.Projection!.PendingTokens);

        editor.Selection = RichTextRange.Caret(editor.Document.Start);

        Assert.Empty(controller.Projection!.PendingTokens);
    }

    [Fact(Timeout = 600000)]
    public void Editor_Selection_Follows_Into_View_And_Preserves_Direction()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("abcdef");
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);

        RichTextPosition start = editor.Document.Start;
        RichTextPosition end = editor.Document.End;
        editor.Selection = new RichTextRange(end, start);

        Assert.Equal(6, view.SelectionAnchor);
        Assert.Equal(0, view.SelectionFocus);
    }

    [Fact(Timeout = 600000)]
    public void View_Selection_Maps_Back_Without_Reentrant_Event_Loop()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("abcdef");
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        int editorEvents = 0;
        int viewEvents = 0;
        editor.SelectionChanged += (_, _) => editorEvents++;
        view.SelectionChanged += (_, _) => viewEvents++;

        view.SetSelection(5, 2);

        Assert.Equal(MoveRight(editor.Document, 5), editor.Selection.Anchor);
        Assert.Equal(MoveRight(editor.Document, 2), editor.Selection.Focus);
        Assert.Equal(1, editorEvents);
        Assert.Equal(1, viewEvents);
    }

    [Fact(Timeout = 600000)]
    public void Code_Navigation_Highlights_Affected_Source_Without_Moving_Focus()
    {
        StandardRichEdit editor = BoldEditor("x");
        var view = new TestFormatCodeView();
        var host = new TestHost();
        using var session = new UiSession(host, new TestDispatcher(), new TestClock());
        var root = new TestRoot(editor, view);
        session.AddRoot(root);
        session.SetFocus(view);
        using var controller = CreateController(editor, view);

        view.Activate(2);

        Assert.Same(view, session.FocusedElement);
        Assert.Equal(editor.Document.Start, editor.Selection.Focus);
        Assert.NotNull(editor.SecondarySelection);
    }

    [Fact(Timeout = 600000)]
    public void Synchronization_Does_Not_Replace_Document_Or_Consume_Undo_History()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("Hello");
        editor.Selection = RichTextRange.Caret(editor.Document.End);
        editor.ExecuteCommand(RichEditCommand.InsertText, "!");
        RichTextDocument edited = editor.Document;
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);

        view.SetSelection(0, view.Text.Length);
        view.Activate(0);

        Assert.Same(edited, editor.Document);
        Assert.True(editor.GetCommandState(RichEditCommand.Undo).IsEnabled);
        Assert.True(editor.ExecuteCommand(RichEditCommand.Undo));
        Assert.Equal("Hello", editor.GetPlainText());
    }

    [Fact(Timeout = 600000)]
    public void Document_Replacement_Reuses_Controller_And_View()
    {
        var editor = new StandardRichEdit();
        var view = new TestFormatCodeView();
        using var controller = CreateController(editor, view);
        long before = controller.Generation;

        editor.Document = RichTextDocument.FromPlainText("Opened");

        Assert.Equal("Opened", view.Text);
        Assert.True(controller.Generation > before);
        Assert.Same(controller.Projection, view.Projection);
    }

    [Fact(Timeout = 600000)]
    public void Dispose_Unsubscribes_And_Leaves_Last_Projection_Intact()
    {
        var editor = new StandardRichEdit();
        editor.SetPlainText("Before");
        var view = new TestFormatCodeView();
        var controller = CreateController(editor, view);
        controller.Dispose();

        editor.SetPlainText("After");

        Assert.Equal("Before", view.Text);
        Assert.Throws<ObjectDisposedException>(() => controller.Refresh());
    }

    [Fact(Timeout = 600000)]
    public async Task Superseded_Background_Result_Cannot_Overwrite_Newer_Document()
    {
        var editor = new StandardRichEdit();
        var view = new TestFormatCodeView();
        var scheduler = new ControlledScheduler();
        using var controller = new WriterFormatCodesController(
            editor, view, new TestDispatcher(), scheduler: scheduler);

        editor.SetPlainText(new string('A', FormatCodeProjectionPolicy.MaxSynchronousSourceCharacters + 1));
        Task firstWork = controller.CurrentWork!;
        editor.SetPlainText(new string('B', FormatCodeProjectionPolicy.MaxSynchronousSourceCharacters + 1));
        Task secondWork = controller.CurrentWork!;

        scheduler.Publish(0);
        await firstWork;
        Assert.NotEqual(new string('A', FormatCodeProjectionPolicy.MaxSynchronousSourceCharacters + 1), view.Text);

        scheduler.Publish(1);
        await secondWork;
        Assert.Equal('B', view.Text[0]);
        Assert.Equal(FormatCodeProjectionPolicy.MaxSynchronousSourceCharacters + 1, view.Text.Length);
        Assert.False(controller.IsProjectionPending);
    }

    private static WriterFormatCodesController CreateController(
        StandardRichEdit editor,
        TestFormatCodeView view) =>
        new(editor, view, new TestDispatcher());

    private static StandardRichEdit BoldEditor(string text)
    {
        var editor = new StandardRichEdit
        {
            Document = RichTextDocument.FromParagraphs(
                [RichTextParagraph.Create(text, new InlineStyle { Bold = true })]),
        };
        return editor;
    }

    private static RichTextPosition MoveRight(RichTextDocument document, int count)
    {
        RichTextPosition position = document.Start;
        for (int i = 0; i < count; i++)
            position = document.PositionRightOf(position);
        return position;
    }

    private sealed class TestFormatCodeView : UiFormatCodeView
    {
        public void Activate(int offset) => ActivateAt(offset);

        public void RequestRedoForTest() => RequestRedo();
    }

    private sealed class TestDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action callback) => callback();
    }

    private sealed class ControlledScheduler : IWriterFormatCodesScheduler
    {
        private readonly List<(FormatCodeProjection Result, TaskCompletionSource<FormatCodeProjection> Completion)> _work = [];

        public Task<FormatCodeProjection> Schedule(
            Func<FormatCodeProjection> projection,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<FormatCodeProjection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add((projection(), completion));
            return completion.Task;
        }

        public void Publish(int index) => _work[index].Completion.SetResult(_work[index].Result);
    }

    private sealed class TestRoot : UiElement
    {
        public TestRoot(params UiElement[] children)
        {
            foreach (UiElement child in children)
                AddChild(child);
        }
    }

    private sealed class TestClock : IUiClock
    {
        public UiTimestamp Now => new(TimeSpan.Zero);
    }

    private sealed class TestHost : IUiHost
    {
        public BSize ViewportSize => new(800, 600);

        public double Scale => 1;

        public BRenderList CreateRenderList(int capacity = 0) => new(capacity);

        public void Invalidate(UiInvalidation invalidation)
        {
        }

        public void Present(BRenderList renderList)
        {
        }
    }
}
