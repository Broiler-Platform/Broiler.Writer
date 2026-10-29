using Broiler.Writer.WebAssembly;
using Broiler.Documents;
using Broiler.Documents.Model;

namespace Broiler.Writer.FormatCodes.Tests;
public class BrowserDocumentStateTests
{
    [Fact] public void DownloadIsNotAConfirmedSave()
    {
        var state = Dirty(); var token = state.BeginExport();
        state.CompleteExport(token, BrowserExportOutcome.ExportInitiated);
        Assert.True(state.IsModified); Assert.False(state.IsExporting);
    }
    [Fact] public void ConfirmedCurrentRevisionCanBeSaved()
    {
        var state = Dirty(); state.CompleteExport(state.BeginExport(), BrowserExportOutcome.SavedToHandle);
        Assert.False(state.IsModified);
    }
    [Fact] public void EditingDuringWriteCannotBeMarkedSaved()
    {
        var state = Dirty(); var token = state.BeginExport(); state.Changed();
        state.CompleteExport(token, BrowserExportOutcome.SavedToHandle);
        Assert.True(state.IsModified);
    }
    [Fact] public void ConversionLossKeepsOriginalModified()
    {
        var state = Dirty(); state.CompleteExport(state.BeginExport(), BrowserExportOutcome.SavedToHandle, completeRepresentation: false);
        Assert.True(state.IsModified);
    }
    [Fact] public void StaleCompletionCannotAffectReplacementDocument()
    {
        var state = Dirty(); var token = state.BeginExport(); state.Replaced(); state.Changed();
        Assert.False(state.CompleteExport(token, BrowserExportOutcome.SavedToHandle)); Assert.True(state.IsModified);
    }
    [Fact] public void FailureAndCancellationKeepContentDirty()
    {
        foreach (var outcome in new[] { BrowserExportOutcome.Canceled, BrowserExportOutcome.Failed }) {
            var state = Dirty(); state.CompleteExport(state.BeginExport(), outcome); Assert.True(state.IsModified);
        }
    }
    [Fact] public void OverlappingExportsAreRejected()
    {
        var state = Dirty(); state.BeginExport(); Assert.Throws<InvalidOperationException>(() => state.BeginExport());
    }
    [Fact] public void BrowserBudgetTruncationCannotReplaceTheDocument()
    {
        var result = new DocumentReadResult(RichTextDocument.FromPlainText("truncated"),
            new[] { DocumentDiagnostic.Warning("markdown.limit.run", "Truncated by limit") }, DocumentResultStatus.Partial);
        Assert.True(WriterDocumentLoadPolicy.MayReplace(result));
        Assert.False(WriterDocumentLoadPolicy.MayReplace(result, rejectLimits: true));
    }
    private static BrowserDocumentState Dirty() { var state = new BrowserDocumentState(); state.Changed(); return state; }
}
