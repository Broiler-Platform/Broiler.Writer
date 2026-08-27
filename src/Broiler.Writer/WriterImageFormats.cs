using System;
using System.Buffers.Binary;
using System.IO;
using Broiler.Graphics;

namespace Broiler.Writer;

/// <summary>
/// Recognizes the image files the Writer will insert and reads their pixel size
/// straight out of the encoded header.
/// </summary>
/// <remarks>
/// The size is read here rather than by decoding, so choosing an insertion size
/// costs no pixel buffer and works before — or without — a renderer that can
/// upload the image. A format whose header is not understood still inserts; it
/// simply arrives at the default size.
/// </remarks>
internal static class WriterImageFormats
{
    /// <summary>The size an image of unknown dimensions is inserted at.</summary>
    private const double DefaultExtent = 200;

    /// <summary>
    /// The media type for an image file, from its extension and confirmed
    /// against the bytes. Returns null when this is not an image format the
    /// document codecs carry.
    /// </summary>
    public static string? ContentTypeFor(string path, ReadOnlySpan<byte> data)
    {
        string? signature = ContentTypeForSignature(data);
        if (signature is not null)
            return signature;

        // A file whose bytes are unrecognized but whose extension is a known
        // image type is still worth carrying: the renderer, not this table,
        // decides what decodes.
        return Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" or "jpe" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" or "dib" => "image/bmp",
            "tif" or "tiff" => "image/tiff",
            "webp" => "image/webp",
            _ => null,
        };
    }

    /// <summary>
    /// The size to insert an image at: its own pixel size, scaled down to
    /// <paramref name="maxWidth"/> so a photo straight from a camera does not
    /// arrive several thousand units wide.
    /// </summary>
    public static BSize MeasureDisplaySize(ReadOnlySpan<byte> data, double maxWidth)
    {
        if (!TryReadPixelSize(data, out int width, out int height) || width <= 0 || height <= 0)
            return new BSize(DefaultExtent, DefaultExtent);

        if (maxWidth <= 0 || width <= maxWidth)
            return new BSize(width, height);

        double scale = maxWidth / width;
        return new BSize(maxWidth, Math.Max(1, Math.Round(height * scale)));
    }

    /// <summary>Reads the pixel dimensions from an encoded image header.</summary>
    public static bool TryReadPixelSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (StartsWith(data, [0x89, (byte)'P', (byte)'N', (byte)'G']))
            return TryReadPngSize(data, out width, out height);
        if (StartsWith(data, [0xFF, 0xD8]))
            return TryReadJpegSize(data, out width, out height);
        if (StartsWith(data, "GIF8"u8))
            return TryReadGifSize(data, out width, out height);
        if (StartsWith(data, "BM"u8))
            return TryReadBmpSize(data, out width, out height);

        return false;
    }

    private static string? ContentTypeForSignature(ReadOnlySpan<byte> data)
    {
        if (StartsWith(data, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (StartsWith(data, [0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (StartsWith(data, "GIF87a"u8) || StartsWith(data, "GIF89a"u8))
            return "image/gif";
        if (StartsWith(data, "BM"u8))
            return "image/bmp";
        if (StartsWith(data, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(data, [0x4D, 0x4D, 0x00, 0x2A]))
            return "image/tiff";
        if (data.Length >= 12 && StartsWith(data, "RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    /// <summary>PNG: the IHDR chunk is first and holds width then height, big-endian.</summary>
    private static bool TryReadPngSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24 || !data[12..16].SequenceEqual("IHDR"u8))
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        return true;
    }

    /// <summary>JPEG: walk the marker segments to the start-of-frame that states the size.</summary>
    private static bool TryReadJpegSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        int offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;
            if (offset + 2 > data.Length)
                return false;

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
            if (length < 2)
                return false;

            // SOF0..SOF15, excluding the DHT/JPG/DAC markers interleaved in that range.
            bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame)
            {
                if (offset + 7 > data.Length)
                    return false;

                height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..(offset + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..(offset + 7)]);
                return width > 0 && height > 0;
            }

            offset += length;
        }

        return false;
    }

    /// <summary>GIF: the logical screen descriptor follows the six-byte signature, little-endian.</summary>
    private static bool TryReadGifSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 10)
            return false;

        width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
        return true;
    }

    /// <summary>BMP: the DIB header states the size; a negative height means top-down rows.</summary>
    private static bool TryReadBmpSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 26)
            return false;

        width = BinaryPrimitives.ReadInt32LittleEndian(data[18..22]);
        height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..26]));
        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
