using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Broiler.Documents.Model;
using Broiler.Documents.Resources;
using Broiler.Graphics.Imaging;
using Broiler.Media;

namespace Broiler.Writer;

/// <summary>
/// Encodes as PNG the pictures a document holds as decoded samples, so the
/// format a save writes has bytes to put in the file.
/// </summary>
/// <remarks>
/// <para>
/// An opened PDF gives the model its pictures as samples. The page drew each
/// one through a filter, a colour space and perhaps a mask, and the decoded
/// result is what arrives. Every writer needs an encoding, and the resource gate
/// will not make one up for a writer: the bytes would not be the document's,
/// and the format would be that writer's guess. It leaves the choice to the
/// host. A Writer asked to save is that host, and a picture that was on screen
/// but missing from the saved file is the worse result.
/// </para>
/// <para>
/// PNG, because it is lossless: the samples written are the samples read,
/// transparency included. Only where the session's decision about the picture
/// permits <see cref="DocumentResourceOperations.Transform"/>, since a
/// re-encoding is one, and only where a head registered an image codec. Anything
/// else leaves the picture as it was, for the writer to report.
/// </para>
/// <para>
/// Made for the save and for nothing else. The editor keeps the samples it
/// draws, and neither the document nor its undo history changes. The encoding
/// is admitted into the session's resource decisions under an id of its own
/// rather than borrowing the decoded picture's approval, and it is remembered
/// per picture, so saving the same document again encodes nothing twice.
/// </para>
/// <para>
/// Only the body is searched. It is where a PDF read puts its pictures, and no
/// other reader produces decoded samples.
/// </para>
/// </remarks>
internal sealed class WriterPictureEncoding
{
    /// <summary>The media type every encoding here produces.</summary>
    public const string MediaType = "image/png";

    // Keyed weakly by the decoded payload, so an encoding lives exactly as long
    // as the picture it was made from, and a closed document takes its PNGs with it.
    private readonly ConditionalWeakTable<BImageResource, BImageResource> _encodings = new();

    /// <summary>
    /// Returns <paramref name="document"/> with each decoded picture in its body
    /// replaced by a PNG encoding admitted into <paramref name="resources"/>, and
    /// in <paramref name="encoded"/> how many distinct pictures that was.
    /// </summary>
    public RichTextDocument EncodeDecodedPictures(
        RichTextDocument document,
        DocumentConversionContextBuilder resources,
        out int encoded)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resources);

        encoded = 0;
        if (!BImageCodecs.IsRegistered)
            return document;

        DocumentConversionContext decided = resources.Build();
        var replacements = new Dictionary<InlineImage, InlineImage?>(ReferenceEqualityComparer.Instance);
        RichTextParagraph[]? paragraphs = null;

        for (int index = 0; index < document.ParagraphCount; index++)
        {
            // The runs of the paragraph as it was. An encoded picture takes the
            // place of a decoded one character for character, so the offsets
            // stay good while the paragraph is rebuilt around them.
            RichTextParagraph paragraph = document.Paragraphs[index];
            int offset = 0;
            foreach (StyleRun run in document.Paragraphs[index].Runs)
            {
                if (run.Style.Image is InlineImage image)
                {
                    if (!replacements.TryGetValue(image, out InlineImage? replacement))
                    {
                        replacement = Encode(image, decided, resources);
                        replacements[image] = replacement;
                        if (replacement is not null)
                            encoded++;
                    }

                    if (replacement is not null)
                    {
                        string text = paragraph.Text.Substring(offset, run.Length);
                        paragraph = paragraph
                            .RemoveRange(offset, run.Length)
                            .InsertText(offset, text, run.Style with { Image = replacement });
                    }
                }

                offset += run.Length;
            }

            if (!ReferenceEquals(paragraph, document.Paragraphs[index]))
            {
                paragraphs ??= [.. document.Paragraphs];
                paragraphs[index] = paragraph;
            }
        }

        if (paragraphs is null)
            return document;

        // The paragraph count is unchanged, so every table still spans the
        // paragraphs it did, and the rest of the document comes across whole.
        return RichTextDocument.FromParagraphs(paragraphs)
            .WithRunningContent(document.RunningContent)
            .WithShapes(document.Shapes)
            .WithPageGeometry(document.PageGeometry)
            .WithTables(document.Tables)
            .WithStyleDefaults(document.StyleDefaults);
    }

    /// <summary>
    /// The picture as PNG, admitted into <paramref name="resources"/>; or null
    /// where it already has bytes, may not be transformed, or would not encode.
    /// </summary>
    private InlineImage? Encode(InlineImage image, DocumentConversionContext decided, DocumentConversionContextBuilder resources)
    {
        if (image.TryGetEncoded(out _, out _) ||
            !image.Resource.TryGetPixels(out BPixelBuffer? pixels) ||
            !decided.IsAllowed(image.ResourceId, DocumentResourceOperations.Transform, image.Resource) ||
            !decided.TryGetEntry(image.ResourceId, out DocumentResourceEntry? entry))
        {
            return null;
        }

        if (!_encodings.TryGetValue(image.Resource, out BImageResource? png))
        {
            try
            {
                using var bitmap = new BBitmap(pixels!.Width, pixels.Height, (byte[])pixels.Rgba.Clone(), takeOwnership: true);
                png = BImageResource.FromEncoded(bitmap.Encode(), MediaType, pixels.Width, pixels.Height);
            }
            catch (Exception exception) when (
                exception is MediaException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A codec that cannot write PNG after all. The picture stays as
                // it was, and the writer says that it left it out.
                return null;
            }

            _encodings.AddOrUpdate(image.Resource, png);
        }

        return resources.AdmitImage(
            new InlineImage(png, default, image.Width, image.Height, image.AltText, image.Name, image.Presentation),
            entry!.Provenance,
            entry.Disposition);
    }
}
