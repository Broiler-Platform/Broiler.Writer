using System;

namespace Broiler.Writer.WebAssembly;

internal enum BrowserExportOutcome { ExportInitiated, SavedToHandle, Canceled, Failed }

/// <summary>Only a confirmed write of the current revision may clear modified state.</summary>
internal sealed class BrowserDocumentState
{
    internal bool IsModified { get; private set; }
    internal long Generation { get; private set; }
    internal long Revision { get; private set; }
    internal bool IsExporting => _pendingToken != 0;
    private int _nextToken, _pendingToken;
    private long _exportRevision, _exportGeneration;
    internal void Changed() { Revision++; IsModified = true; }
    internal void Replaced(bool modified = false)
    {
        Generation++; Revision++; IsModified = modified;
        _pendingToken = 0;
    }
    internal int BeginExport()
    {
        if (IsExporting) throw new InvalidOperationException("A file operation is already in progress.");
        _pendingToken = ++_nextToken; _exportRevision = Revision; _exportGeneration = Generation;
        return _pendingToken;
    }
    internal bool CompleteExport(int token, BrowserExportOutcome outcome, bool completeRepresentation = true)
    {
        if (token == 0 || token != _pendingToken) return false;
        _pendingToken = 0;
        if (outcome == BrowserExportOutcome.SavedToHandle && completeRepresentation && Generation == _exportGeneration && Revision == _exportRevision)
            IsModified = false;
        return true;
    }
}
