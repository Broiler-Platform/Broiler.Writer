using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.Model;
using Broiler.Documents.Rtf;
using Broiler.Graphics.Geometry;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.FileDialog.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterSaveReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_Precedes_Replacement_And_Keeps_Editing_State(bool approve)
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "target.md");
            File.WriteAllText(path, "previous file");
            RichTextDocument source = SetStyledDocument(app);

            OpenSavePicker(app).Accept(path);
            var review = Assert.IsType<StandardDialog>(app.Session.ModalElement);
            Assert.Contains("target.md", review.Title);
            Assert.Equal("previous file", File.ReadAllText(path));
            if (approve)
                review.Accept();
            else
                review.Cancel();

            Assert.Contains(approve ? "Styled document" : "previous file", File.ReadAllText(path));
            Assert.Same(source, app.Document);
            Assert.True(app.IsModified);
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
            Assert.True(app.Menu.CommandDispatcher!.TryExecute("help.save-notes"));
            Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_As_From_Review_Can_Complete_A_Guarded_Close()
    {
        using var host = CreateHost();
        int closes = 0;
        using var app = new WriterApp(host, () => closes++);
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string markdown = Path.Combine(directory.FullName, "target.md");
            string rtf = Path.Combine(directory.FullName, "target.rtf");
            SetStyledDocument(app);
            app.RequestClose();
            Assert.IsType<StandardDialog>(app.Session.ModalElement).Accept();
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(markdown);

            Assert.IsType<StandardDialog>(app.Session.ModalElement).Reject();
            Assert.Equal(0, closes);
            Assert.False(File.Exists(markdown));
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(rtf);

            Assert.True(File.Exists(rtf));
            Assert.False(app.IsModified);
            Assert.Equal(1, closes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_Policy_Rejects_Incomplete_Bytes_And_Reviews_Partial_Results()
    {
        var incomplete = new DocumentWriteResult(4, status: DocumentResultStatus.Partial,
            destinationState: DocumentDestinationState.PartialDestination);
        Assert.Throws<IOException>(() => WriterSavePolicy.EnsureComplete(incomplete));

        var partial = new DocumentWriteResult(4, status: DocumentResultStatus.Partial);
        WriterSavePolicy.EnsureComplete(partial);
        Assert.True(WriterSavePolicy.RequiresReview(partial));
        Assert.False(WriterSavePolicy.RequiresReview(new DocumentWriteResult(4,
            [DocumentDiagnostic.Info("test.summary", "Written")])));
    }

    [Theory]
    [InlineData(ListKind.Bullet)]
    [InlineData(ListKind.Numbered)]
    public void Silent_Rtf_List_Loss_Gets_A_Writer_Diagnostic(ListKind listKind)
    {
        RichTextDocument document = ListDocument(listKind);
        using var stream = new MemoryStream();
        DocumentWriteResult codecWrite = RtfWriter.Write(RichTextDocument.FromPlainText(document.PlainText), stream);
        Assert.Equal(DocumentResultStatus.Success, codecWrite.Status);

        DocumentWriteResult reviewed = WriterSavePolicy.ReviewRtfLists(document, stream.ToArray(), codecWrite);

        Assert.Equal(DocumentResultStatus.Partial, reviewed.Status);
        Assert.Contains(reviewed.Diagnostics, note => note.Code == "writer.save.rtf.list-changed");
        Assert.Equal(codecWrite.BytesWritten, reviewed.BytesWritten);
        Assert.Equal(codecWrite.DestinationState, reviewed.DestinationState);
    }

    [Fact]
    public void Rtf_Save_Preserves_The_List_Or_Offers_Docx_Before_Replacement()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        try
        {
            string rtf = Path.Combine(directory.FullName, "target.rtf");
            string docx = Path.Combine(directory.FullName, "target.docx");
            File.WriteAllText(rtf, "previous contents");
            app.Editor.Document = ListDocument(ListKind.Numbered);
            using var probe = new MemoryStream();
            RtfWriter.Write(app.Document, probe);
            bool codecPreservesList = RtfReader.Read(probe.ToArray()).Document.Paragraphs[0].Style.ListKind == ListKind.Numbered;

            OpenSavePicker(app).Accept(rtf);
            if (codecPreservesList)
            {
                Assert.Null(app.Session.ModalElement);
                Assert.Equal(ListKind.Numbered, RtfReader.Read(File.ReadAllBytes(rtf)).Document.Paragraphs[0].Style.ListKind);
                Assert.False(app.IsModified);
                return;
            }

            var review = Assert.IsType<StandardDialog>(app.Session.ModalElement);
            Assert.Equal("previous contents", File.ReadAllText(rtf));

            review.Reject();
            Assert.IsType<StandardFileDialog>(app.Session.ModalElement).Accept(docx);

            Assert.Equal("previous contents", File.ReadAllText(rtf));
            Assert.False(app.IsModified);
            using FileStream saved = File.OpenRead(docx);
            var restored = new DocxDocumentCodec().Read(saved).Document;
            Assert.Equal(ListKind.Numbered, restored.Paragraphs[0].Style.ListKind);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Saving_Rtf_To_A_Stream_Keeps_Modified_State_When_The_List_Is_Lost()
    {
        using var host = CreateHost();
        using var app = new WriterApp(host, () => { });
        app.Editor.Document = ListDocument(ListKind.Bullet);
        using var output = new MemoryStream();

        Assert.True(app.WriteDocument(output, "list.rtf"));
        Assert.Equal(ListKind.Bullet, app.Document.Paragraphs[0].Style.ListKind);
        ListKind savedKind = RtfReader.Read(output.ToArray()).Document.Paragraphs[0].Style.ListKind;
        Assert.Equal(savedKind != ListKind.Bullet, app.IsModified);
    }

    private static RichTextDocument ListDocument(ListKind listKind) =>
        RichTextDocument.FromParagraphs([
            RichTextParagraph.Plain("Item").WithParagraphStyle(ParagraphStyle.Default with { ListKind = listKind }),
        ]);

    private static WriterUiHost CreateHost() =>
        new(() => new BSize(1200, 800), () => 1, () => { }, _ => { });

    private static RichTextDocument SetStyledDocument(WriterApp app)
    {
        var document = RichTextDocument.FromParagraphs([
            RichTextParagraph.Create("Styled document", InlineStyle.Default with { Underline = true }),
        ]);
        app.Editor.Document = document;
        return document;
    }

    private static StandardFileDialog OpenSavePicker(WriterApp app)
    {
        app.Menu.CommandDispatcher!.TryExecute("file.save-as");
        return Assert.IsType<StandardFileDialog>(app.Session.ModalElement);
    }
}
