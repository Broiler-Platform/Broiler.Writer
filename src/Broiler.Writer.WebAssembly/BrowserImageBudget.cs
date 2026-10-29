using System;

namespace Broiler.Writer.WebAssembly;

internal static class BrowserImageBudget
{
    internal const long MaximumImagePixels = 16_000_000;
    internal const long MaximumDocumentPixels = 32_000_000;
    internal static void Add(ReadOnlySpan<byte> encoded, ref long totalPixels)
    {
        bool supported = encoded.StartsWith(new byte[] { 137, 80, 78, 71 }) || encoded.StartsWith(new byte[] { 255, 216 }) || encoded.StartsWith("BM"u8);
        if (!supported || !WriterImageFormats.TryReadPixelSize(encoded, out int width, out int height))
            throw new NotSupportedException("The browser edition previews embedded PNG, JPEG and BMP images with readable dimensions.");
        long pixels = (long)width * height;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || pixels > MaximumImagePixels || pixels > MaximumDocumentPixels - totalPixels)
            throw new NotSupportedException("Embedded images exceed the browser pixel budget (16 MP per image, 32 MP total, 8192 pixels per side).");
        totalPixels += pixels;
    }
}
