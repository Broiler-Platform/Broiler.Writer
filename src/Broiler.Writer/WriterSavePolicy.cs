using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Documents.Rtf;

namespace Broiler.Writer;

internal sealed record WriterPreparedSave(string Name, RichTextDocument Document, byte[] Bytes, DocumentWriteResult Result)
{
    internal bool RequiresReview => WriterSavePolicy.RequiresReview(Result);
}

internal static class WriterSavePolicy
{
    internal static bool RequiresReview(DocumentWriteResult result) =>
        result.Status == DocumentResultStatus.Partial ||
        result.Diagnostics.Any(note => note.Severity != DocumentDiagnosticSeverity.Info);

    internal static void EnsureComplete(DocumentWriteResult result)
    {
        if (result.Status == DocumentResultStatus.Rejected || result.DestinationState != DocumentDestinationState.Committed)
            throw new IOException(result.Diagnostics.FirstOrDefault(note => note.Severity != DocumentDiagnosticSeverity.Info)?.Message
                ?? "The document codec did not complete the save.");
    }

    internal static DocumentWriteResult ReviewRtfLists(RichTextDocument document, ReadOnlyMemory<byte> bytes,
        DocumentWriteResult result, DocumentReadOptions? readOptions = null)
    {
        if (result.Status == DocumentResultStatus.Rejected || result.DestinationState != DocumentDestinationState.Committed ||
            !document.Paragraphs.Any(paragraph => paragraph.Style.ListKind != ListKind.None))
            return result;

        // Check the actual output so a codec update that preserves lists can
        // pass without requiring a Writer-side version check.
        DocumentReadResult readBack = RtfReader.Read(bytes, readOptions);
        if (!WriterDocumentLoadPolicy.MayReplace(readBack, rejectLimits: true) ||
            readBack.Document.ParagraphCount != document.ParagraphCount)
            return WithWarning(result, "writer.save.rtf.list-unverified", "Writer could not verify list formatting in the generated RTF. Choose another format to preserve more of the document.");

        for (int index = 0; index < document.ParagraphCount; index++)
        {
            RichTextParagraph source = document.Paragraphs[index];
            if (source.Style.ListKind == ListKind.None)
                continue;

            RichTextParagraph written = readBack.Document.Paragraphs[index];
            if (!string.Equals(source.Text, written.Text, StringComparison.Ordinal))
                return WithWarning(result, "writer.save.rtf.list-unverified", $"Writer could not verify list formatting in paragraph {index + 1} of the generated RTF.");
            if (source.Style.ListKind != written.Style.ListKind)
                return WithWarning(result, "writer.save.rtf.list-changed", $"The generated RTF changes bullet or numbered list formatting in paragraph {index + 1}. Try saving as DOCX to keep the list.");
        }

        return result;
    }

    internal static DocumentWriteResult ReviewDocxNestedPictures(byte[] bytes, DocumentWriteResult result)
    {
        if (result.Status == DocumentResultStatus.Rejected || result.DestinationState != DocumentDestinationState.Committed)
            return result;

        XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        var packageRoot = new Uri("https://package.invalid/word/");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry part in archive.Entries.Where(IsNestedStory))
            {
                using Stream content = part.Open();
                string[] imageIds = XDocument.Load(content).Descendants(drawing + "blip")
                    .Select(blip => (string?)blip.Attribute(officeRelationships + "embed"))
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (imageIds.Length == 0)
                    continue;

                ZipArchiveEntry? relationships = archive.GetEntry("word/_rels/" + part.Name + ".rels");
                if (relationships is not null)
                {
                    using Stream relationshipContent = relationships.Open();
                    var resolvedIds = XDocument.Load(relationshipContent)
                        .Descendants(packageRelationships + "Relationship")
                        .Where(IsInternalImage)
                        .Select(relationship => (string?)relationship.Attribute("Id"))
                        .OfType<string>().ToHashSet(StringComparer.Ordinal);
                    if (imageIds.All(resolvedIds.Contains))
                        continue;
                }

                return WithWarning(result, "writer.save.docx.picture-relationship",
                    $"A picture in {part.Name} cannot be displayed because the generated DOCX is missing its image relationship or media. Choose another format to preserve the picture.");

                bool IsInternalImage(XElement relationship) =>
                    (string?)relationship.Attribute("Type") == officeRelationships.NamespaceName + "/image" &&
                    (string?)relationship.Attribute("TargetMode") != "External" &&
                    Uri.TryCreate(packageRoot, (string?)relationship.Attribute("Target"), out Uri? target) &&
                    target.Host == packageRoot.Host && target.Scheme == packageRoot.Scheme &&
                    archive.GetEntry(Uri.UnescapeDataString(target.AbsolutePath).TrimStart('/')) is not null;
            }
        }
        catch (Exception error) when (error is InvalidDataException or XmlException)
        {
            return WithWarning(result, "writer.save.docx.picture-unverified",
                "Writer could not verify nested pictures in the generated DOCX package.");
        }

        return result;

        static bool IsNestedStory(ZipArchiveEntry entry) =>
            entry.FullName == "word/" + entry.Name &&
            (entry.Name is "footnotes.xml" or "endnotes.xml" ||
             entry.Name.EndsWith(".xml", StringComparison.Ordinal) &&
             (entry.Name.StartsWith("header", StringComparison.Ordinal) || entry.Name.StartsWith("footer", StringComparison.Ordinal)));
    }

    internal static DocumentWriteResult ReviewMarkdownCapitalization(RichTextDocument document, DocumentWriteResult result) =>
        result.Status != DocumentResultStatus.Rejected && result.DestinationState == DocumentDestinationState.Committed &&
        document.Paragraphs.Any(paragraph => paragraph.Runs.Any(run => run.Style.Capitalization != TextCapitalization.None))
            ? WithWarning(result, "writer.save.markdown.capitalization",
                "Markdown drops all-caps and small-caps formatting. Choose another format to preserve it.")
            : result;

    internal static DocumentWriteResult ReviewCharacterFormatting(string extension, RichTextDocument document, DocumentWriteResult result)
    {
        if (result.Status == DocumentResultStatus.Rejected || result.DestinationState != DocumentDestinationState.Committed)
            return result;

        extension = extension.ToLowerInvariant();
        var styles = document.Paragraphs.SelectMany(paragraph => paragraph.Runs).Select(run => run.Style);
        bool markdown = extension is ".md" or ".markdown";
        bool html = extension is ".html" or ".htm";
        if ((markdown || html) && styles.Any(style => style.Hidden))
            result = WithWarning(result, "writer.save.hidden-text", "This format writes hidden text as visible text.");

        if (markdown && styles.Any(style => style.Shift is { IsNone: false }))
            result = WithWarning(result, "writer.save.baseline-shift", "Markdown drops superscript, subscript, and other baseline shifts.");

        if ((markdown || html || extension == ".rtf") && styles.Any(style => !string.IsNullOrEmpty(style.Language)))
            result = WithWarning(result, "writer.save.text-language", "This format drops the language of individual runs.");
        else if (extension == ".odt" && styles.Any(style => style.Language is string tag && HasUnsupportedOdtLanguage(tag)))
            result = WithWarning(result, "writer.save.text-language", "This ODT writer can lose language subtags other than a two-letter country code.");

        if ((markdown || html || extension == ".odt") && styles.Any(style => !string.IsNullOrEmpty(style.CharacterStyle)))
            result = WithWarning(result, "writer.save.character-style", "This format drops the names of character styles.");
        return result;
    }

    internal static DocumentWriteResult ReviewParagraphFormatting(DocumentCodec? codec, RichTextDocument document,
        byte[] bytes, DocumentWriteResult result, DocumentReadOptions? readOptions = null)
    {
        bool review = WriterParagraphFidelity.HasFormatting(document);
        review |= WriterPageFidelity.HasStructure(document) || document.Tables.Count > 0 || WriterGraphicsFidelity.HasGraphics(document);
        if (codec is null || !codec.CanRead || result.Status == DocumentResultStatus.Rejected ||
            result.DestinationState != DocumentDestinationState.Committed || !review)
            return result;

        const string unverified = "Writer could not verify the paragraph formatting in the generated file.";
        string? change;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            DocumentReadResult reopened = codec.Read(stream, readOptions);
            change = WriterDocumentLoadPolicy.MayReplace(reopened, rejectLimits: true)
                ? WriterParagraphFidelity.DescribeFirstChange(document, reopened.Document) : unverified;
            if (change is null && WriterPageFidelity.HasStructure(document))
                change = WriterPageFidelity.DescribeFirstChange(document, reopened.Document);
            if (change is null && document.Tables.Count > 0)
                change = WriterTables.DescribeFirstSaveChange(document, reopened.Document);
            if (change is null && WriterGraphicsFidelity.HasGraphics(document))
                change = WriterGraphicsFidelity.DescribeFirstChange(document, reopened.Document);
        }
        catch (Exception error) when (error is DocumentException or InvalidDataException or XmlException)
        {
            change = unverified;
        }
        return change is null ? result : WithWarning(result, "writer.save.paragraph-formatting", change);
    }

    private static bool HasUnsupportedOdtLanguage(string tag)
    {
        string[] parts = tag.Split('-', 2);
        return parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter) ||
            parts.Length == 2 && (parts[1].Length != 2 || !parts[1].All(char.IsAsciiLetter));
    }

    private static DocumentWriteResult WithWarning(DocumentWriteResult result, string code, string message) =>
        new(result.BytesWritten, result.Diagnostics.Append(DocumentDiagnostic.Warning(code, message)),
            DocumentResultStatus.Partial, result.DestinationState);

    internal static void CommitFile(string path, byte[] bytes)
    {
        string stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }
}
