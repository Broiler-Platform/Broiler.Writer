using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Resources;

namespace Broiler.Writer.WebAssembly;

internal sealed partial class BrowserWriterDemo
{
    internal string RecoverySnapshot()
    {
        _dispatcher.Drain();
        RichTextDocument document = _editor.Document;
        ValidateRecoveryText(document);
        var resources = DocumentConversionContextBuilder.Continuing(_resources, DocumentResourcePolicy.AllowOwnDocuments);
        document = _pictureEncoding.EncodeDecodedPictures(document, resources, out _);
        var checkpoint = WriterRecoveryCheckpoint.Capture(document, new DocumentWriteOptions(resources: resources.Build()),
            BrowserReadOptions, ValidateRecoveryDocument);
        return JsonSerializer.Serialize(new WriterRecoverySnapshot {
            name = _documentName, format = checkpoint.Format, payload = Convert.ToBase64String(checkpoint.Bytes),
            generation = _documentState.Generation, revision = _documentState.Revision, notes = checkpoint.Notes,
        }, WriterRecoveryJsonContext.Default.WriterRecoverySnapshot);
    }

    internal bool RestoreRecovery(string name, string payload, string format = WriterRecoveryCheckpoint.Rtf)
    {
        _dispatcher.Drain();
        try {
            if (_documentState.IsExporting || _choosingExportName) throw new InvalidOperationException("Finish the current export before restoring a draft.");
            if (payload.Length > ((MaximumFileBytes + 2) / 3) * 4) throw new InvalidDataException("Recovery exceeds the browser file limit.");
            byte[] bytes = Convert.FromBase64String(payload);
            var result = WriterRecoveryCheckpoint.Read(format, bytes, BrowserReadOptions);
            if (!WriterDocumentLoadPolicy.MayReplace(result, rejectLimits: true) ||
                (format == WriterRecoveryCheckpoint.DocxV1 && result.Status != DocumentResultStatus.Success))
                throw new InvalidDataException("The recovery document could not be read completely.");
            ValidateRecoveryDocument(result.Document);
            string restoredName = DisplayFileName(name);
            ReplaceDocument(() => { _editor.Document = result.Document; _editor.Selection = RichTextRange.Caret(RichTextDocument.Start); });
            _resources = result.Resources; _currentDocumentName = restoredName; _hasSavedName = true;
            SetDocumentName(_currentDocumentName, modified:true);
            ReportOperation("Recovered", "Local draft restored as modified content. Export a separate backup; review recovery format notes.", result.Diagnostics);
            _session.SetFocus(_editor); RefreshUi(); return true;
        } catch (Exception error) when (IsDocumentException(error)) {
            ReportOperation("Failed", "Recovery failed; current document retained. " + error.Message); return false;
        }
    }

    private static void ValidateRecoveryText(RichTextDocument document)
    {
        if (document.PlainText.Length > MaximumTextCharacters || document.ParagraphCount > BrowserReadOptions.Limits.MaxParagraphCount)
            throw new InvalidDataException("The recovery document exceeds the browser text or paragraph limit.");
    }

    private static void ValidateRecoveryDocument(RichTextDocument document)
    {
        ValidateRecoveryText(document);
        long pixels = 0;
        foreach (InlineImage image in RecoveryImages(document))
        {
            BrowserImageBudget.Add(image.Data.Span, ref pixels);
            try
            {
                using var decoded = Broiler.Graphics.Imaging.BBitmap.Decode(image.Data.Span);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new InvalidDataException("An embedded recovery image could not be decoded.", error);
            }
        }
    }

    private static IEnumerable<InlineImage> RecoveryImages(RichTextDocument document)
    {
        var visited = new HashSet<RichTextParagraph>(ReferenceEqualityComparer.Instance);
        foreach (InlineImage image in ParagraphImages(document.Paragraphs).Concat(ShapeImages(document.Shapes)))
            yield return image;
        if (document.PageBackground?.Image is ImageFill pageFill)
            yield return pageFill.Picture;

        IEnumerable<PageStyle> pageStyles = document.Sections
            .Where(section => section.PageStyles is not null).SelectMany(section => section.PageStyles!.Styles);
        foreach (PageStyle style in pageStyles)
        {
            if (style.Background?.Image is ImageFill styleFill)
                yield return styleFill.Picture;
        }
        PageSelection[] selections = Enum.GetValues<PageSelection>();
        foreach (RunningContent content in document.Sections.Select(section => section.RunningContent)
            .OfType<RunningContent>().Prepend(document.RunningContent).Concat(pageStyles.Select(style => style.RunningContent))
            .Distinct<RunningContent>(ReferenceEqualityComparer.Instance))
        {
            foreach (PageSelection selection in selections)
            {
                foreach (InlineImage image in ParagraphImages(content.Header(selection)).Concat(ParagraphImages(content.Footer(selection)))
                    .Concat(ShapeImages(content.HeaderShapes(selection))).Concat(ShapeImages(content.FooterShapes(selection))))
                    yield return image;
            }
        }

        IEnumerable<InlineImage> ParagraphImages(IEnumerable<RichTextParagraph> paragraphs, int noteDepth = 0)
        {
            if (noteDepth > 32)
                throw new InvalidDataException("The recovery document exceeds the nested note limit.");

            foreach (RichTextParagraph paragraph in paragraphs)
            {
                if (!visited.Add(paragraph))
                    continue;
                foreach (StyleRun run in paragraph.Runs)
                {
                    if (run.Style.Image is InlineImage image)
                        yield return image;
                    if (run.Style.Note is DocumentNote note)
                    {
                        foreach (InlineImage noteImage in ParagraphImages(note.Paragraphs, noteDepth + 1))
                            yield return noteImage;
                    }
                }
            }
        }

        IEnumerable<InlineImage> ShapeImages(IEnumerable<DocumentShape> shapes)
        {
            foreach (DocumentShape shape in shapes)
            {
                if (shape.Image is InlineImage image)
                    yield return image;
                if (shape.Fill?.Image is ImageFill fill)
                    yield return fill.Picture;
                foreach (InlineImage textImage in ParagraphImages(shape.Paragraphs))
                    yield return textImage;
            }
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
