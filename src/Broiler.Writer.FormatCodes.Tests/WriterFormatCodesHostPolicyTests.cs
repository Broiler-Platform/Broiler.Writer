using Broiler.Input.Keyboard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterFormatCodesHostPolicyTests
{
    [Theory]
    [InlineData(600, 0.68, 402.56, 8, 189.44)]
    [InlineData(240, 0.50, 120, 8, 112)]
    public void Layout_Reserves_Editor_Splitter_And_Pane(
        double height,
        double fraction,
        double editor,
        double splitter,
        double pane)
    {
        WriterFormatCodesLayoutResult result = WriterFormatCodesLayout.Calculate(height, fraction, true);

        Assert.Equal(editor, result.EditorHeight, 2);
        Assert.Equal(splitter, result.SplitterHeight, 2);
        Assert.Equal(pane, result.PaneHeight, 2);
        Assert.Equal(height, result.EditorHeight + result.SplitterHeight + result.PaneHeight, 2);
    }

    [Fact]
    public void Hidden_Layout_Gives_All_Space_To_Editor()
    {
        WriterFormatCodesLayoutResult result = WriterFormatCodesLayout.Calculate(420, 0.68, false);

        Assert.Equal(new WriterFormatCodesLayoutResult(420, 0, 0), result);
    }

    [Fact]
    public void Desktop_And_Browser_Share_Toggle_And_Focus_Shortcut_Policy()
    {
        KeyboardModifierState toggle = KeyboardModifierState.Control | KeyboardModifierState.Shift;

        Assert.True(WriterFormatCodesShortcut.IsToggle("F3", toggle, true, false));
        Assert.False(WriterFormatCodesShortcut.IsToggle("F3", toggle, true, true));
        Assert.True(WriterFormatCodesShortcut.IsFocusCycle("F6", KeyboardModifierState.None, true, false));
        Assert.True(WriterFormatCodesShortcut.IsReverseFocusCycle(KeyboardModifierState.Shift));
        Assert.False(WriterFormatCodesShortcut.IsFocusCycle("F6", KeyboardModifierState.Control, true, false));
    }
}
