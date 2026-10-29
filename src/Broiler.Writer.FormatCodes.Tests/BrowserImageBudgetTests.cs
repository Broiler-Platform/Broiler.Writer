using Broiler.Writer.WebAssembly;
using System.Buffers.Binary;

namespace Broiler.Writer.FormatCodes.Tests;
public class BrowserImageBudgetTests
{
    [Fact] public void OversizedHeadersAreRefusedBeforeRasterAllocation()
    {
        long pixels = 0;
        Assert.Throws<NotSupportedException>(() => BrowserImageBudget.Add(PngHeader(20000, 20000), ref pixels));
        Assert.Equal(0, pixels);
    }
    [Fact] public void DocumentImagesShareOneBudget()
    {
        long pixels = 0; BrowserImageBudget.Add(PngHeader(4000, 4000), ref pixels); BrowserImageBudget.Add(PngHeader(4000, 4000), ref pixels);
        Assert.Throws<NotSupportedException>(() => BrowserImageBudget.Add(PngHeader(1, 1), ref pixels));
    }
    [Fact] public void UnknownImageHeadersAreNotDecodedSpeculatively()
    {
        long pixels = 0; Assert.Throws<NotSupportedException>(() => BrowserImageBudget.Add("unknown"u8, ref pixels));
    }
    private static byte[] PngHeader(int width, int height)
    {
        byte[] header = new byte[33]; new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82 }.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), height); return header;
    }
}
