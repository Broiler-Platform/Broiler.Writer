using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Broiler.Documents;
using Broiler.Documents.Model;

namespace Broiler.Writer.WebAssembly;

internal sealed partial class BrowserWriterDemo
{
    internal string RecoverySnapshot()
    {
        _dispatcher.Drain();
        DocumentWriteResult result = WriteDocument("Checkpoint.rtf", _editor.Document, out byte[] bytes);
        EnsureWritableResult(result, bytes);
        var readBack = ReadDocument("Checkpoint.rtf", bytes);
        if (!WriterDocumentLoadPolicy.MayReplace(readBack, rejectLimits: true) || readBack.Document.PlainText != _editor.Document.PlainText)
            throw new InvalidDataException("RTF checkpoint did not preserve document text. Export the current document separately.");
        string notes = string.Join("\n", result.Diagnostics.Concat(readBack.Diagnostics).Select(note => note.Code + ": " + note.Message));
        return JsonSerializer.Serialize(new WriterRecoverySnapshot {
            name = _documentName, format = "rtf", payload = Convert.ToBase64String(bytes),
            generation = _documentState.Generation, revision = _documentState.Revision, notes = notes,
        }, WriterRecoveryJsonContext.Default.WriterRecoverySnapshot);
    }

    internal bool RestoreRecovery(string name, string payload)
    {
        _dispatcher.Drain();
        try {
            if (_documentState.IsExporting || _choosingExportName) throw new InvalidOperationException("Finish the current export before restoring a draft.");
            if (payload.Length > ((MaximumFileBytes + 2) / 3) * 4) throw new InvalidDataException("Recovery exceeds the browser file limit.");
            byte[] bytes = Convert.FromBase64String(payload);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Recovery exceeds the browser file limit.");
            var result = ReadDocument("Checkpoint.rtf", bytes);
            if (!WriterDocumentLoadPolicy.MayReplace(result, rejectLimits:true) || result.Document.PlainText.Length > MaximumTextCharacters || result.Document.ParagraphCount > 10000)
                throw new InvalidDataException("Recovery document is rejected or exceeds browser limits.");
            long pixels = 0;
            foreach (var image in result.Document.Paragraphs.SelectMany(paragraph => paragraph.Runs).Select(run => run.Style.Image)
                .Concat(result.Document.Shapes.Select(shape => shape.Image)).Where(image => image is not null)) {
                BrowserImageBudget.Add(image!.Data.Span, ref pixels);
                using var decoded = Broiler.Graphics.Imaging.BBitmap.Decode(image.Data.Span);
            }
            ReplaceDocument(() => { _editor.Document = result.Document; _editor.Selection = RichTextRange.Caret(RichTextDocument.Start); });
            _resources = result.Resources; _currentDocumentName = DisplayFileName(name); _hasSavedName = true;
            SetDocumentName(_currentDocumentName, modified:true);
            ReportOperation("Recovered", "Local draft restored as modified content. Export a separate backup; review recovery format notes.", result.Diagnostics);
            _session.SetFocus(_editor); RefreshUi(); return true;
        } catch (Exception error) when (IsDocumentException(error)) {
            ReportOperation("Failed", "Recovery failed; current document retained. " + error.Message); return false;
        }
    }
}

internal sealed class WriterRecoverySnapshot
{
    public string name { get; set; } = "";
    public string format { get; set; } = "rtf";
    public string payload { get; set; } = "";
    public string notes { get; set; } = "";
    public long generation { get; set; }
    public long revision { get; set; }
}

[JsonSerializable(typeof(WriterRecoverySnapshot))]
internal partial class WriterRecoveryJsonContext : JsonSerializerContext { }
