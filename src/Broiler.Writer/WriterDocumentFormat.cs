using System;
using System.Collections.Generic;
using Broiler.Documents;
using Broiler.UI.FileDialog;

namespace Broiler.Writer;

/// <summary>What a composition root lets the Writer do with one document format.</summary>
/// <remarks>
/// A codec's own <see cref="DocumentCodec.CanRead"/>/<see cref="DocumentCodec.CanWrite"/>
/// say what it implements; this says what the host chose to offer. The two are
/// deliberately separate — a codec whose writer exists but has not cleared its
/// release gate is registered for opening only, and no save filter, save
/// destination, or save dispatch can reach it.
/// </remarks>
[Flags]
public enum WriterFormatCapabilities
{
    /// <summary>Registered but offered for nothing. Useful only in tests.</summary>
    None = 0,

    /// <summary>The format appears in the Open dialog and can be selected for reading.</summary>
    Open = 1 << 0,

    /// <summary>The format appears in the Save dialog and can be written.</summary>
    Save = 1 << 1,

    /// <summary>Both.</summary>
    OpenAndSave = Open | Save,
}

/// <summary>
/// One document codec as a host composed it: the codec, the name the file
/// dialogs show, and the capabilities the host enabled.
/// </summary>
public sealed class WriterDocumentFormat
{
    public WriterDocumentFormat(
        DocumentCodec codec,
        string displayName,
        WriterFormatCapabilities capabilities = WriterFormatCapabilities.OpenAndSave)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A document format needs a display name.", nameof(displayName));
        if (codec.Descriptor.FileExtensions.Count == 0)
            throw new ArgumentException(
                $"The {codec.Descriptor.Name} codec declares no file extension, so the Writer cannot offer it in a file dialog.",
                nameof(codec));

        // A host can offer less than a codec implements, never more: enabling a
        // capability the codec does not have would produce a filter the dispatch
        // then has to refuse.
        if ((capabilities & WriterFormatCapabilities.Open) != 0 && !codec.CanRead)
            throw new ArgumentException(
                $"The {codec.Descriptor.Name} codec does not implement reading, so it cannot be registered for opening.",
                nameof(capabilities));
        if ((capabilities & WriterFormatCapabilities.Save) != 0 && !codec.CanWrite)
            throw new ArgumentException(
                $"The {codec.Descriptor.Name} codec does not implement writing, so it cannot be registered for saving.",
                nameof(capabilities));

        Codec = codec;
        DisplayName = displayName.Trim();
        Capabilities = capabilities;
    }

    public DocumentCodec Codec { get; }

    /// <summary>The format name a file dialog shows, without the pattern suffix.</summary>
    public string DisplayName { get; }

    public WriterFormatCapabilities Capabilities { get; }

    public bool CanOpen => (Capabilities & WriterFormatCapabilities.Open) != 0;

    public bool CanSave => (Capabilities & WriterFormatCapabilities.Save) != 0;

    public IReadOnlyList<string> FileExtensions => Codec.Descriptor.FileExtensions;

    /// <summary>The extension a name without one is completed with.</summary>
    public string DefaultExtension => FileExtensions[0];

    /// <summary>The dialog pattern, e.g. <c>*.html;*.htm</c>.</summary>
    public string FilterPattern => string.Join(";", Patterns());

    /// <summary>The dialog label, e.g. <c>HTML (*.html, *.htm)</c>.</summary>
    public string FilterName => DisplayName + " (" + string.Join(", ", Patterns()) + ")";

    public UiFileDialogFilter CreateFilter() => new(FilterName, FilterPattern, DefaultExtension);

    public bool MatchesExtension(string? extension) => Codec.Descriptor.MatchesExtension(extension);

    private IEnumerable<string> Patterns()
    {
        foreach (string extension in FileExtensions)
            yield return "*" + extension;
    }
}
