namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterProjectBoundaryTests
{
    [Fact]
    public void FormatCodes_Does_Not_Reference_The_User_Interface()
    {
        var assembly = typeof(IWriterFormatCodesScheduler).Assembly;

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Broiler.Writer.Core" ||
            reference.Name!.StartsWith("Broiler.UI", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Broiler.Input", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Broiler.Graphics", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(WriterAbout))]
    [InlineData(typeof(WriterIcons))]
    [InlineData(typeof(WriterZoom))]
    [InlineData(typeof(WriterZoomStep))]
    [InlineData(typeof(WriterFormatCodesController))]
    [InlineData(typeof(WriterFormatCodesLayout))]
    [InlineData(typeof(WriterFormatCodesLayoutResult))]
    [InlineData(typeof(WriterFormatCodesShortcut))]
    public void Shared_User_Interface_Belongs_To_Core(Type type)
    {
        Assert.Equal(typeof(WriterApp).Assembly, type.Assembly);
        Assert.Equal("Broiler.Writer", type.Namespace);
    }
}
