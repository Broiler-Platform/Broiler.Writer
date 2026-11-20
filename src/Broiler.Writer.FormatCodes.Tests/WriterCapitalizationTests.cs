using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.FileDialog.Standard;
using Broiler.UI.Menu;
using Broiler.UI.RichEdit;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterCapitalizationTests
{
    [Fact]
    public void A_Mixed_Reverse_Selection_Can_Be_Formatted_And_Undone()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        var paragraph = RichTextParagraph.Create("one ", InlineStyle.Default with { Capitalization = TextCapitalization.AllCaps })
            .InsertText(4, "two", InlineStyle.Default with { Italic = true });
        app.Editor.Document = RichTextDocument.FromParagraphs([paragraph, RichTextParagraph.Plain("three")]);
        var selection = new RichTextRange(app.Document.End, RichTextDocument.Start);
        app.Editor.Selection = selection;
        UiMenuItem allCaps = FormatItem(app, "all-caps");
        UiMenuItem smallCaps = FormatItem(app, "small-caps");

        Assert.False(allCaps.IsChecked);
        Assert.False(smallCaps.IsChecked);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("format.all-caps"));
        Assert.True(allCaps.IsChecked);
        Assert.Equal(selection, app.Editor.Selection);
        Assert.Equal("one two\nthree", app.Document.PlainText);
        Assert.All(app.Document.Paragraphs.SelectMany(p => p.Runs), run =>
            Assert.Equal(TextCapitalization.AllCaps, run.Style.Capitalization));
        Assert.True(app.Document.Paragraphs[0].StyleAt(4).Italic);

        Assert.True(app.Menu.CommandDispatcher.TryExecute("format.small-caps"));
        Assert.True(smallCaps.IsChecked);
        Assert.False(allCaps.IsChecked);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("edit.undo"));
        Assert.True(allCaps.IsChecked);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("edit.redo"));
        Assert.True(smallCaps.IsChecked);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("format.small-caps"));
        Assert.False(smallCaps.IsChecked);
        Assert.Equal(TextCapitalization.None, app.Document.Paragraphs[1].StyleAt(0).Capitalization);
    }

    [Fact]
    public void Caret_Capitalization_Applies_Only_To_New_Text_And_Clear_Removes_It()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Start ");
        var before = app.Document;
        app.Editor.Selection = RichTextRange.Caret(app.Document.End);

        Assert.True(app.Menu.CommandDispatcher!.TryExecute("format.small-caps"));
        Assert.Same(before, app.Document);
        Assert.True(FormatItem(app, "small-caps").IsChecked);
        Assert.True(app.Editor.ExecuteCommand(RichEditCommand.InsertText, "mIxEd"));
        Assert.Equal("Start mIxEd", app.Document.PlainText);
        Assert.Equal(TextCapitalization.None, app.Document.Paragraphs[0].StyleAt(0).Capitalization);
        Assert.Equal(TextCapitalization.SmallCaps, app.Document.Paragraphs[0].StyleAt(6).Capitalization);

        app.Editor.Selection = new RichTextRange(PositionAt(app.Document, 6), app.Document.End);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("format.clear"));
        Assert.Equal(TextCapitalization.None, app.Document.Paragraphs[0].StyleAt(6).Capitalization);
        Assert.Equal("Start mIxEd", app.Document.PlainText);
    }

    [Theory]
    [InlineData(".rtf")]
    [InlineData(".docx")]
    [InlineData(".odt")]
    [InlineData(".html")]
    public void Both_Capitalization_Modes_Reopen_With_Original_Text(string extension)
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("First second");
        app.Editor.Selection = new RichTextRange(RichTextDocument.Start, PositionAt(app.Document, 5));
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("format.all-caps"));
        app.Editor.Selection = new RichTextRange(PositionAt(app.Document, 6), app.Document.End);
        Assert.True(app.Menu.CommandDispatcher.TryExecute("format.small-caps"));

        string name = "capitalization" + extension;
        using var output = new MemoryStream();
        Assert.True(app.WriteDocument(output, name));
        output.Position = 0;
        Assert.True(app.LoadDocument(output, name));
        Assert.Equal("First second", app.Document.PlainText);
        Assert.Equal(TextCapitalization.AllCaps, app.Document.Paragraphs[0].StyleAt(0).Capitalization);
        Assert.Equal(TextCapitalization.None, app.Document.Paragraphs[0].StyleAt(5).Capitalization);
        Assert.Equal(TextCapitalization.SmallCaps, app.Document.Paragraphs[0].StyleAt(6).Capitalization);
    }

    [Fact]
    public void Markdown_Reviews_Capitalization_Loss_Before_Writing()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.SetPlainText("Mixed");
        app.Editor.Selection = new RichTextRange(RichTextDocument.Start, app.Document.End);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("format.all-caps"));
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "case.md");
            Assert.True(app.Menu.CommandDispatcher.TryExecute("file.save-as"));
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(path);
            Assert.IsType<StandardDialog>(app.Session.ModalElement).Cancel();
            Assert.False(File.Exists(path));
            Assert.Equal(TextCapitalization.AllCaps, app.Document.Paragraphs[0].StyleAt(0).Capitalization);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static UiMenuItem FormatItem(WriterApp app, string id) =>
        Assert.Single(Assert.Single(app.Menu.Items, item => item.Id == "format").Children, item => item.Id == id);

    private static RichTextPosition PositionAt(RichTextDocument document, int character)
    {
        RichTextPosition position = RichTextDocument.Start;
        for (int index = 0; index < character; index++)
            position = document.PositionRightOf(position);
        return position;
    }

    private static WriterUiHost CreateHost() => new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
}
