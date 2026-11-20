using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Graphics.Geometry;
using Broiler.UI.Dialog.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterViewDiagnosticsTests
{
    [Fact]
    public void Notes_Describe_The_Applicable_Layout_Limitations_Once()
    {
        var style = ParagraphStyle.Default with
        {
            PageBreakBefore = true,
            KeepWithNext = true,
            TabStops = new ParagraphTabStops([new ParagraphTabStop(72, TabAlignment.Decimal, ".")]),
            ListKind = ListKind.Numbered,
            List = new ListReference(new ListDefinition([new ListLevel()]), 0, RestartAt: 5),
            SpacingAfter = 9,
        };
        RichTextParagraph paragraph = RichTextParagraph.Plain("Costs\t10.00").WithParagraphStyle(style);
        var running = RunningContent.Empty.WithHeader(PageSelection.Even, [RichTextParagraph.Plain("Letterhead")]);
        var section = new DocumentSection(0, 2, 2, 18, runningContent: running, hidden: true);
        var document = RichTextDocument.FromParagraphs([paragraph, paragraph]).WithSections([section]);

        IReadOnlyList<DocumentDiagnostic> notes = WriterViewDiagnostics.Collect(document);

        Assert.Equal(
            ["writer.view.hidden-section"],
            notes.Select(note => note.Code));
        Assert.All(notes, note => Assert.Equal(DocumentDiagnosticSeverity.Info, note.Severity));
        Assert.Same(paragraph, document.Paragraphs[0]);
        Assert.Same(section, Assert.Single(document.Sections));
    }

    [Fact]
    public void Notes_Leave_Basic_Editor_Formatting_Alone()
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Basic list").WithParagraphStyle(ParagraphStyle.Default with
            {
                ListKind = ListKind.Bullet,
                Alignment = TextAlignment.Center,
                LeftIndent = 0,
                RightIndent = 0,
            }),
        ]);

        Assert.Empty(WriterViewDiagnostics.Collect(document));
        Assert.Equal(WriterViewDiagnostics.EmptyMessage, WriterNotes.DescribeView(document));
    }

    [Fact]
    public void Notes_Report_Imported_Hidden_Text()
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("hidden", InlineStyle.Default with { Hidden = true })
        ]);

        Assert.Equal("writer.view.hidden-text", Assert.Single(WriterViewDiagnostics.Collect(document)).Code);
    }

    [Fact]
    public void View_Notes_Are_Available_For_An_Edit_Without_File_Diagnostics()
    {
        using var host = new WriterUiHost(() => new BSize(1200, 800), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("New page").WithParagraphStyle(ParagraphStyle.Default with { PageBreakBefore = true }),
        ]);
        app.Editor.Document = document;

        Assert.Empty(app.LastReadDiagnostics);
        Assert.True(app.Menu.CommandDispatcher!.TryExecute("view.notes"));
        var dialog = Assert.IsType<StandardDialog>(app.Session.ModalElement);
        Assert.Equal("Editing view notes", dialog.Title);
        Assert.DoesNotContain("writer.view.pagination", WriterNotes.DescribeView(app.Document));
        dialog.Accept();
        Assert.Same(document, app.Document);
        Assert.True(app.IsModified);
    }
}
