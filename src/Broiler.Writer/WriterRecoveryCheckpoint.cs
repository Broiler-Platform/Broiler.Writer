using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.Model;
using Broiler.Documents.Rtf;
using Broiler.Graphics.Color;

namespace Broiler.Writer;

internal static class WriterRecoveryCheckpoint
{
    internal const string Rtf = "rtf";
    internal const string DocxV1 = "docx-v1";

    internal sealed record Candidate(string Format, byte[] Bytes, string Notes);

    internal static Candidate Capture(RichTextDocument document, DocumentWriteOptions writeOptions,
        DocumentReadOptions readOptions, Action<RichTextDocument>? validateRestoredDocument = null)
    {
        try
        {
            return CaptureWith(Rtf, new RtfDocumentCodec());
        }
        catch (InvalidDataException rtfError)
        {
            try
            {
                return CaptureWith(DocxV1, new DocxDocumentCodec());
            }
            catch (InvalidDataException docxError)
            {
                throw new InvalidDataException($"Recovery checkpoint failed. RTF: {rtfError.Message} DOCX: {docxError.Message}", docxError);
            }
        }

        Candidate CaptureWith(string format, DocumentCodec codec)
        {
            using var stream = new MemoryStream();
            DocumentWriteResult write = codec.Write(document, stream, writeOptions);
            if (stream.Length > readOptions.Limits.MaxDocumentBytes)
                throw new InvalidDataException("The recovery file exceeds the size limit.");

            byte[] bytes = stream.ToArray();
            if (format == DocxV1)
                write = WriterSavePolicy.ReviewDocxNestedPictures(bytes, write);
            DocumentReadResult readBack = Read(format, bytes, readOptions);
            Validate(document, write, readBack);
            validateRestoredDocument?.Invoke(readBack.Document);
            string notes = string.Join("\n", write.Diagnostics.Concat(readBack.Diagnostics)
                .Where(note => note.Severity != DocumentDiagnosticSeverity.Info)
                .Select(note => note.Code + ": " + note.Message));
            return new Candidate(format, bytes, notes);
        }
    }

    internal static DocumentReadResult Read(string format, byte[] bytes, DocumentReadOptions options)
    {
        if (bytes.LongLength > options.Limits.MaxDocumentBytes)
            throw new InvalidDataException("The recovery file exceeds the size limit.");
        if (format == Rtf && bytes.AsSpan().StartsWith("{\\rtf"u8))
            return RtfReader.Read(bytes, options);
        if (format != DocxV1 || !IsDocx(bytes))
            throw new InvalidDataException("The recovery file does not match its format.");

        using var stream = new MemoryStream(bytes, writable: false);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > 2000)
                throw new InvalidDataException("The recovery package has too many entries.");

            long remaining = options.Limits.MaxDocumentBytes * 4;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length > remaining)
                    throw new InvalidDataException("The recovery package exceeds the expansion limit.");
                remaining -= entry.Length;
            }
        }
        stream.Position = 0;
        return new DocxDocumentCodec().Read(stream, options);
    }

    internal static bool IsDocx(ReadOnlySpan<byte> bytes) => bytes.StartsWith("PK\u0003\u0004"u8);

    internal static void Validate(RichTextDocument original, DocumentWriteResult write, DocumentReadResult readBack)
    {
        if (write.DestinationState != DocumentDestinationState.Committed ||
            write.Status != DocumentResultStatus.Success ||
            readBack.Status != DocumentResultStatus.Success ||
            !WriterDocumentLoadPolicy.MayReplace(readBack, rejectLimits: true))
        {
            string? diagnostic = write.Diagnostics.Concat(readBack.Diagnostics)
                .FirstOrDefault(note => note.Severity != DocumentDiagnosticSeverity.Info)?.Message;
            throw new InvalidDataException(diagnostic is null
                ? "The codec could not produce a complete recovery checkpoint."
                : "The codec could not produce a complete recovery checkpoint. " + diagnostic);
        }

        RichTextDocument recovered = readBack.Document;
        if (recovered.PlainText != original.PlainText ||
            recovered.ParagraphCount != original.ParagraphCount)
            throw new InvalidDataException("Recovery changed the document text or paragraph structure.");

        if (WriterParagraphFidelity.HasFormatting(original) &&
            WriterParagraphFidelity.DescribeFirstChange(original, recovered) is string paragraphChange)
            throw new InvalidDataException(paragraphChange);
        if (WriterPageFidelity.HasStructure(original) &&
            WriterPageFidelity.DescribeFirstChange(original, recovered) is string pageChange)
            throw new InvalidDataException(pageChange);
        if (original.Tables.Count > 0 && WriterTables.DescribeFirstSaveChange(original, recovered) is string tableChange)
            throw new InvalidDataException(tableChange);
        if (WriterGraphicsFidelity.HasGraphics(original) && WriterGraphicsFidelity.DescribeFirstChange(original, recovered) is string graphicsChange)
            throw new InvalidDataException(graphicsChange);

        if (recovered.Tables.Count != original.Tables.Count ||
            recovered.Shapes.Count != original.Shapes.Count ||
            recovered.Notes.Count != original.Notes.Count ||
            recovered.PageBackground != original.PageBackground ||
            recovered.ShowsRevisions != original.ShowsRevisions ||
            (!original.RunningContent.IsEmpty && recovered.RunningContent.IsEmpty && recovered.Sections.Count == 0))
            throw new InvalidDataException("Recovery changed the document structure or page formatting.");

        for (int index = 0; index < original.ParagraphCount; index++)
        {
            RichTextParagraph source = original.Paragraphs[index];
            RichTextParagraph restored = recovered.Paragraphs[index];
            if (source.Style.ListKind != restored.Style.ListKind)
                throw new InvalidDataException($"Recovery changed list formatting in paragraph {index + 1}.");

            if (source.Style.PageBreakBefore != restored.Style.PageBreakBefore ||
                source.Style.Alignment != restored.Style.Alignment ||
                !CharacterFormatting(source).SequenceEqual(CharacterFormatting(restored)))
                throw new InvalidDataException($"Recovery changed formatting in paragraph {index + 1}.");
        }
    }

    // Fonts, dimensions and list definitions may be normalized by the codecs.
    // Compare user-visible attributes that have the same representation on both sides.
    private static IEnumerable<(bool Bold, bool Italic, bool Underline, bool Strike,
        bool Hidden, TextCapitalization Capitalization, string? Link, bool Picture, BColor Foreground, BColor Background,
        string? CharacterStyle, string? Language, BaselineShift? Shift)> CharacterFormatting(RichTextParagraph paragraph)
    {
        foreach (StyleRun run in paragraph.Runs)
        {
            InlineStyle style = run.Style;
            var formatting = (style.Bold, style.Italic, style.Underline, style.Strikethrough,
                style.Hidden, style.Capitalization, style.LinkHref, style.Image is not null,
                style.Foreground, style.Background, style.CharacterStyle,
                style.Language, style.Shift is { IsNone: false } ? style.Shift : null);
            for (int offset = 0; offset < run.Length; offset++)
                yield return formatting;
        }
    }
}
