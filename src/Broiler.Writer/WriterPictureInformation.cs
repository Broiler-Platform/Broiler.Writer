using System;
using System.Globalization;
using Broiler.Documents.Model;
using Broiler.UI.RichEdit;

namespace Broiler.Writer;

internal static class WriterPictureInformation
{
    internal static string Describe(UiRichEdit editor)
    {
        var picture = WriterGraphics.InlineAt(editor);
        var shape = WriterGraphics.Selected(editor);
        InlineImage? image = picture is not null && picture.Range == editor.Selection ? picture.Image : shape?.Image;
        if (image is null) return "Formatting Codes";
        string media = image.Resource.TryGetEncoded(out var bytes, out var type)
            ? $"{type}, {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes" : "decoded pixels";
        return $"Formatting Codes · [{(shape is null ? "Image" : "Floating Image")} {Dimension(shape?.Width ?? image.Width)} × {Dimension(shape?.Height ?? image.Height)} pt; {media}" +
            (string.IsNullOrWhiteSpace(image.Name) ? "" : $"; name=\"{SingleLine(image.Name)}\"") +
            (string.IsNullOrWhiteSpace(image.AltText) ? "" : $"; alt=\"{SingleLine(image.AltText)}\"") +
            (image.IsMissingPayload ? "; payload unavailable" : "") + "]";
    }

    private static string Dimension(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "auto";
    private static string SingleLine(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
}
