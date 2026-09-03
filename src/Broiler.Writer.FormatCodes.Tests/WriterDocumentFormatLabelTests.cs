using Broiler.UI.FileDialog;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Enforces the naming rule the format rights registers set: a label a user reads
/// may name the format and may not name a vendor or a vendor's product.
/// </summary>
/// <remarks>
/// <para>
/// The rule is DOCX-IP-006's, approved 2026-09-03, and it is checked here rather
/// than in <c>Broiler.Documents</c> for two reasons. The labels are composed here,
/// so this is where they can be read. And a guard placed there would not have run:
/// that component's aggregate detector requires a <c>src/Broiler.Cli</c> directory
/// this repository does not have, so every aggregate-scoped guard it owns is
/// skipped rather than executed.
/// </para>
/// <para>
/// What the rule is not: a ban on the word "Word" anywhere. "WordprocessingML" is
/// the name of the markup and is written freely in technical prose. This checks
/// the strings a format list and a save dialog show, which are the ones that
/// imply a relationship with a product.
/// </para>
/// </remarks>
public sealed class WriterDocumentFormatLabelTests
{
    /// <summary>Vendor and product names no format label may contain.</summary>
    private static readonly string[] ProhibitedNames =
    [
        "Word", "Microsoft", "WordPad", "Office", "Acrobat", "LibreOffice", "OpenOffice",
    ];

    [Fact(Timeout = 600000)]
    public void No_Format_Label_Names_A_Vendor_Or_Its_Product()
    {
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault();

        string[] offending = formats.Formats
            .Select(format => format.DisplayName)
            .Where(name => ProhibitedNames.Any(
                prohibited => name.Contains(prohibited, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact(Timeout = 600000)]
    public void No_Dialog_Filter_Names_A_Vendor_Or_Its_Product()
    {
        // The display name is what the rule is written about, but a filter is what
        // a user actually reads, and it is built from the display name plus the
        // patterns. Checking both means the rule cannot be satisfied in the model
        // and broken in the dialog.
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault();

        string[] offending = formats.CreateOpenFilters()
            .Concat(formats.CreateSaveFilters())
            .Select(filter => filter.Name)
            .Where(name => ProhibitedNames.Any(
                prohibited => name.Contains(prohibited, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact(Timeout = 600000)]
    public void The_Docx_Format_Is_Labelled_As_The_Register_Approves()
    {
        // The positive half. DOCX-IP-006's approved Save As label is
        // "DOCX Document (*.docx)", and the filter is assembled from the display
        // name and the codec's extensions rather than written out anywhere, so
        // this is the assertion that the assembled string is the approved one.
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault();

        WriterDocumentFormat docx = Assert.Single(
            formats.Formats.Where(format => format.MatchesExtension(".docx")));

        Assert.Equal("DOCX Document", docx.DisplayName);
        Assert.Equal("DOCX Document (*.docx)", docx.FilterName);

        UiFileDialogFilter filter = Assert.Single(
            formats.CreateSaveFilters().Where(f => f.Pattern.Contains(".docx", StringComparison.Ordinal)));
        Assert.Equal("DOCX Document (*.docx)", filter.Name);
    }
}
