using Broiler.Documents;
using System;
using System.Linq;

namespace Broiler.Writer;

public static class WriterDocumentLoadPolicy
{
    /// <summary>A rejected/empty partial conversion must never replace the open document.</summary>
    public static bool MayReplace(DocumentReadResult result, bool rejectLimits = false) => result.IsUsable &&
        (result.Document.PlainText.Length > 0 || result.Status == DocumentResultStatus.Success) &&
        (!rejectLimits || !result.Diagnostics.Any(note => note.Code.Contains(".limit", StringComparison.OrdinalIgnoreCase) || note.Code == DocumentDiagnosticCodes.InputTooLarge));
}
