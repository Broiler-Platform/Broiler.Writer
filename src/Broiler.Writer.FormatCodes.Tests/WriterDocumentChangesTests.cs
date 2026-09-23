using System.Text;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.FileDialog.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterDocumentChangesTests
{
    [Theory]
    [InlineData("file.new")]
    [InlineData("file.open")]
    [InlineData("file.exit")]
    public void Clean_Documents_Continue_Without_A_Prompt(string command)
    {
        int requests = 0;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => requests++, () => requests++);
        app.Menu.CommandDispatcher!.TryExecute(command);
        Assert.Null(app.Session.ModalElement);
        Assert.Equal(command == "file.new" ? 0 : 1, requests);
    }

    [Theory]
    [InlineData("file.new", false)]
    [InlineData("file.open", false)]
    [InlineData("file.exit", false)]
    [InlineData("file.new", true)]
    [InlineData("file.open", true)]
    [InlineData("file.exit", true)]
    public void Cancel_And_Title_Close_Preserve_Document(string command, bool closeChrome)
    {
        int requests = 0;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => requests++, () => requests++);
        app.Editor.SetPlainText("Keep this text");
        app.Menu.CommandDispatcher!.TryExecute(command);
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        Assert.Equal("Unsaved changes", dialog.Title);
        if (closeChrome) dialog.Close(); else dialog.Cancel();
        Assert.Equal("Keep this text", app.Document.PlainText);
        Assert.True(app.IsModified);
        Assert.Equal(0, requests);
        Assert.Same(app.Editor, app.Session.FocusedElement);
    }

    [Theory]
    [InlineData("file.new")]
    [InlineData("file.open")]
    [InlineData("file.exit")]
    public void Discard_Continues_The_Requested_Action_Once(string command)
    {
        int requests = 0;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => requests++, () => requests++);
        app.Editor.SetPlainText("Discard this text");
        app.Menu.CommandDispatcher!.TryExecute(command);
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        // Repeated requests while the prompt is open cannot replace the pending action.
        app.RequestClose();
        Assert.Same(dialog, app.Session.ModalElement);
        dialog.Reject();
        if (command == "file.new")
        {
            Assert.Equal(string.Empty, app.Document.PlainText);
            Assert.False(app.IsModified);
        }
        Assert.Equal(command == "file.new" ? 0 : 1, requests);
    }

    [Fact]
    public void Cancelling_Save_As_Aborts_New_And_Allows_Another_Request()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Keep this text");
        app.Menu.CommandDispatcher!.TryExecute("file.new");
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
        Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Cancel();
        Assert.Equal("Keep this text", app.Document.PlainText);
        Assert.True(app.IsModified);
        app.Menu.CommandDispatcher.TryExecute("file.new");
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Reject();
        Assert.Equal(string.Empty, app.Document.PlainText);
    }

    [Fact]
    public void Save_As_Writes_Changes_Before_Continuing_New()
    {
        string directory = Path.Combine(Path.GetTempPath(), "writer-changes-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "saved.rtf");
            using var host = CreateHost();
            using var app = new WriterApp(host, () => { });
            app.Editor.SetPlainText("Saved before replacement");
            app.Menu.CommandDispatcher!.TryExecute("file.new");
            Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(path);
            Assert.Contains("Saved before replacement", File.ReadAllText(path));
            Assert.Equal(string.Empty, app.Document.PlainText);
            Assert.False(app.IsModified);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Failed_Save_Does_Not_Close_Or_Clear_Changes()
    {
        int closes = 0;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => closes++);
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(@"{\rtf1 original}"));
        Assert.True(app.LoadDocument(source, "unsupported.writer-test-extension"));
        app.Editor.SetPlainText("Keep after failed save");
        app.RequestClose();
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
        Assert.Equal(0, closes);
        Assert.True(app.IsModified);
        Assert.Equal("Keep after failed save", app.Document.PlainText);
        Assert.StartsWith("Save failed:", app.LastAction);
        app.RequestClose();
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Reject();
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void External_Save_Waits_For_Explicit_Completion(bool success)
    {
        int closes = 0;
        Action<bool>? completed = null;
        using var host = CreateHost();
        using var app = new WriterApp(host, () => closes++, requestSaveDocument: (_, saveAs, done) =>
        {
            Assert.True(saveAs);
            completed = done;
        });
        app.Editor.SetPlainText("External save");
        app.RequestClose();
        Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
        Assert.Equal(0, closes);
        Assert.NotNull(completed);
        app.RequestClose();
        Assert.Null(app.Session.ModalElement);
        if (success)
        {
            using var output = new MemoryStream();
            Assert.True(app.WriteDocument(output, "saved.rtf"));
        }
        completed(success);
        completed(success); // A duplicated host callback must not repeat the action.
        Assert.Equal(success ? 1 : 0, closes);
        Assert.Equal(!success, app.IsModified);
    }

    [Theory]
    [InlineData(1120, 780)]
    [InlineData(320, 300)]
    public void Prompt_Buttons_Fit_And_Escape_From_A_Button_Cancels(int width, int height)
    {
        using var host = CreateHost(width, height);
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Keep this text");
        app.RequestClose();
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        app.RenderFrame();
        var buttons = dialog.Children.Single().Children.OfType<StandardButton>().ToArray();
        Assert.Equal(new[] { "Save", "Discard", "Cancel" }, buttons.Select(b => b.Text));
        foreach (var button in buttons)
        {
            Assert.InRange(button.Bounds.Left, dialog.Bounds.Left, dialog.Bounds.Right);
            Assert.InRange(button.Bounds.Right, dialog.Bounds.Left, dialog.Bounds.Right);
            Assert.InRange(button.Bounds.Bottom, dialog.Bounds.Top, dialog.Bounds.Bottom);
        }
        app.Session.SetFocus(buttons[1]);
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("document-change-test"),
            new InputTimestamp(1, 1000, "test"), 1);
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header,
            KeyboardKey.FromName("Escape"), KeyboardKeyTransition.Down, KeyboardModifierState.None,
            NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1, IsExtended: false, WasDown: false)));
        Assert.Equal(UiDialogResultKind.Cancelled, dialog.CompletedResult.Kind);
        Assert.True(app.IsModified);
    }

    private static WriterUiHost CreateHost(int width = 1120, int height = 780) =>
        new(() => new BSize(width, height), () => 1, () => { }, _ => { });
}
