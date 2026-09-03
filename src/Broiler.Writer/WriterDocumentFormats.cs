using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Broiler.Documents;
using Broiler.Documents.Docx;
using Broiler.Documents.Html;
using Broiler.Documents.Markdown;
using Broiler.Documents.Odt;
using Broiler.Documents.Rtf;
using Broiler.UI.FileDialog;

namespace Broiler.Writer;

/// <summary>
/// The document formats one Writer instance offers, composed by the host rather
/// than discovered.
/// </summary>
/// <remarks>
/// <para>
/// The Writer used to hard-code its codec catalog and its two filter arrays, so
/// every head — desktop, Android, WebAssembly — got exactly the same formats and
/// adding one to the shared core added it everywhere. Composition roots build
/// this instead: <see cref="CreateDefault"/> is the format set every head
/// carries, and anything beyond that is registered by the head that wants it
/// and reaches no other (PDF roadmap §10.1).
/// </para>
/// <para>
/// The instance is not shared between Writer instances: <see cref="CreateDefault"/>
/// builds fresh codecs each call, so two Writers in one process never reach the
/// same codec object.
/// </para>
/// </remarks>
public sealed class WriterDocumentFormats
{
    private readonly ReadOnlyCollection<WriterDocumentFormat> _formats;

    public WriterDocumentFormats(IEnumerable<WriterDocumentFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        WriterDocumentFormat[] array = formats.ToArray();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WriterDocumentFormat format in array)
        {
            if (format is null)
                throw new ArgumentException("The format collection contains a null entry.", nameof(formats));

            // Two formats claiming one extension make the save dispatch ambiguous
            // and the open filters misleading, so it is refused at composition
            // rather than resolved by registration order at runtime.
            foreach (string extension in format.FileExtensions)
            {
                if (!extensions.Add(extension))
                    throw new ArgumentException(
                        $"Two registered formats both claim '{extension}'; the second is {format.DisplayName}.",
                        nameof(formats));
            }
        }

        _formats = Array.AsReadOnly(array);
    }

    /// <summary>
    /// The formats every Writer head carries: RTF, DOCX, ODT, HTML and Markdown,
    /// all readable and writable. Each call composes its own codec instances.
    /// </summary>
    /// <remarks>
    /// RTF stays first because <see cref="DefaultExtension"/> is the first savable
    /// format's extension, so reordering this list renames every untitled save.
    /// The display names are a claims decision rather than a matter of taste:
    /// DOCX-IP-006 forbids a format label naming a vendor or its product, which
    /// is why this list says "DOCX Document" where every other word processor
    /// says "Word Document". <c>WriterDocumentFormatLabelTests</c> enforces it.
    /// ODT belongs here rather than in a per-head registration: it takes no
    /// dependency beyond the ZIP and XML stack DOCX already uses, so no head has
    /// a package-size, trimming, or AOT gate to pass for it the way every head
    /// does for PDF. Its outstanding ODF rights row governs what may be *claimed*
    /// for the format, which is a question for the component's roadmap and its
    /// marketing copy rather than for this composition.
    /// </remarks>
    public static WriterDocumentFormats CreateDefault() => new(
    [
        new WriterDocumentFormat(new RtfDocumentCodec(), "Rich Text Format"),
        new WriterDocumentFormat(new DocxDocumentCodec(), "DOCX Document"),
        new WriterDocumentFormat(new OdtDocumentCodec(), "OpenDocument Text"),
        new WriterDocumentFormat(new HtmlDocumentCodec(), "HTML"),
        new WriterDocumentFormat(new MarkdownDocumentCodec(), "Markdown"),
    ]);

    public IReadOnlyList<WriterDocumentFormat> Formats => _formats;

    /// <summary>This set plus <paramref name="additional"/>, leaving this one unchanged.</summary>
    public WriterDocumentFormats With(params WriterDocumentFormat[] additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        return new WriterDocumentFormats(_formats.Concat(additional));
    }

    /// <summary>
    /// A catalog over the codecs enabled for opening. A codec registered for
    /// saving only is absent, so it cannot be selected by a content probe either.
    /// </summary>
    public DocumentCodecCatalog CreateOpenCatalog() =>
        new(_formats.Where(static format => format.CanOpen).Select(static format => format.Codec));

    /// <summary>The format that writes <paramref name="extension"/>, or null when none does.</summary>
    public WriterDocumentFormat? FindForSave(string? extension) =>
        _formats.FirstOrDefault(format => format.CanSave && format.MatchesExtension(extension));

    /// <summary>The extension a document name without one is completed with.</summary>
    public string DefaultExtension =>
        _formats.FirstOrDefault(static format => format.CanSave)?.DefaultExtension ??
        _formats.FirstOrDefault()?.DefaultExtension ??
        ".rtf";

    /// <summary>
    /// The Open dialog filters: an aggregate of everything openable, then one
    /// filter per format.
    /// </summary>
    public UiFileDialogFilter[] CreateOpenFilters()
    {
        WriterDocumentFormat[] openable = _formats.Where(static format => format.CanOpen).ToArray();
        if (openable.Length == 0)
            return [];

        var filters = new List<UiFileDialogFilter>(openable.Length + 1)
        {
            new(
                "All supported documents",
                string.Join(";", openable.Select(static format => format.FilterPattern)),
                DefaultExtension),
        };
        filters.AddRange(openable.Select(static format => format.CreateFilter()));
        return filters.ToArray();
    }

    /// <summary>The Save dialog filters, one per format enabled for saving.</summary>
    public UiFileDialogFilter[] CreateSaveFilters() =>
        _formats.Where(static format => format.CanSave).Select(static format => format.CreateFilter()).ToArray();

    /// <summary>
    /// The extensions a save can name, for the message a save to anything else
    /// fails with. A format registered for opening only is deliberately absent.
    /// </summary>
    public string DescribeSaveExtensions()
    {
        string[] extensions = _formats
            .Where(static format => format.CanSave)
            .Select(static format => format.DefaultExtension)
            .ToArray();

        return extensions.Length switch
        {
            0 => "no format is registered for saving",
            1 => extensions[0],
            _ => string.Join(", ", extensions[..^1]) + ", or " + extensions[^1],
        };
    }
}
