using Broiler.Documents;
using Broiler.Graphics;
using Broiler.UI.Dialog.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// The read diagnostics were kept and never shown. The status bar could say a
/// document opened with two notes; the only way to learn what they were was to
/// set BROILER_WRITER_DOCUMENT_LOG and read stderr.
/// </summary>
public sealed class WriterNotesTests
{
    private static readonly DocumentDiagnostic[] Two =
    [
        DocumentDiagnostic.Warning("docx.image.shape", "A DOCX drawing held no embedded picture and was skipped."),
        DocumentDiagnostic.Info("docx.read.summary", "DOCX read produced 10 paragraph(s)."),
    ];

    [Fact(Timeout = 600000)]
    public void Lists_Every_Note_With_Its_Code_And_Message()
    {
        string text = WriterNotes.Describe(Two);

        Assert.Contains("2 notes", text, StringComparison.Ordinal);
        foreach (DocumentDiagnostic diagnostic in Two)
        {
            Assert.Contains(diagnostic.Code, text, StringComparison.Ordinal);
            Assert.Contains(diagnostic.Message, text, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 600000)]
    public void Names_The_Severity_So_A_Warning_Reads_Apart_From_A_Summary()
    {
        string text = WriterNotes.Describe(Two);

        Assert.Contains("warning", text, StringComparison.Ordinal);
        Assert.Contains("info", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void Counts_One_Note_In_The_Singular()
    {
        Assert.Contains("1 note:", WriterNotes.Describe([Two[0]]), StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void Says_So_Rather_Than_Showing_An_Empty_List()
    {
        Assert.Contains("nothing to report", WriterNotes.Describe([]), StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void The_Dialog_Is_Titled_For_The_Document_It_Reports_On()
    {
        using StandardDialog dialog = WriterNotes.CreateDialog("test2.docx", Two);

        Assert.Equal("Notes for test2.docx", dialog.Title);
    }
}
