using Broiler.Graphics;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.UI;
using Broiler.UI.ComboBox.Standard;
using Broiler.UI.Toolbar;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// The Writer's toolbar does not wrap. What does not fit goes behind the chevron at its end, so
/// no command is drawn past the edge and left unreachable.
/// </summary>
/// <remarks>
/// These used to run at the Windows head's own 1120px, because the bar of text buttons was wider
/// than any window it opened in. Icon buttons brought its natural width down to well under that,
/// so the head no longer overflows at all - which is the point of the change, and means the
/// overflow behaviour has to be exercised at a width that still triggers it. 1120 is now the case
/// that must NOT overflow, and <see cref="NarrowWidth"/> is the one that must.
/// </remarks>
public sealed class WriterToolbarOverflowTests
{
    /// <summary>The client size the Windows head asks for. The whole bar fits here now.</summary>
    private const double WindowsHeadWidth = 1120;
    private const double WindowsHeadHeight = 780;

    /// <summary>
    /// Narrow enough that the last group overflows. Chosen by reading the bar's own DesiredSize
    /// (896) rather than guessed at, and left a wide margin below it so a future icon a few pixels
    /// wider does not silently stop this from overflowing.
    /// </summary>
    private const double NarrowWidth = 800;

    /// <summary>
    /// Narrower still: enough that the zoom picker itself lands in the drop-down. The picker is
    /// the eighth control on the bar, so most of it has to overflow before the picker does.
    /// </summary>
    private const double PickerOverflowWidth = 340;

    [Fact(Timeout = 600000)]
    public void The_Bar_Overflows_Rather_Than_Clipping()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        Assert.Equal(UiToolbarOverflow.Menu, app.Toolbar.Overflow);
        Assert.NotEmpty(app.Toolbar.OverflowItems);
    }

    [Fact(Timeout = 600000)]
    public void Nothing_Left_On_The_Bar_Is_Drawn_Past_Its_Edge()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        double edge = app.Toolbar.OverflowItems.Count > 0
            ? app.Toolbar.OverflowButtonBounds.Left
            : app.Toolbar.Bounds.Right;

        foreach (UiElement child in app.Toolbar.Children)
        {
            if (child.Visibility != UiVisibility.Visible || app.Toolbar.OverflowItems.Contains(child))
                continue;

            Assert.True(
                child.Bounds.Right <= edge + 0.01,
                $"a toolbar control ends at {child.Bounds.Right}, past the {edge} the bar has room for");
        }
    }

    [Fact(Timeout = 600000)]
    public void Every_Control_Is_Either_On_The_Bar_Or_In_The_Drop_Down()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        foreach (UiElement child in app.Toolbar.Children)
        {
            if (child.Visibility != UiVisibility.Visible)
                continue;

            Assert.True(
                child.Bounds.Width > 0 || app.Toolbar.OverflowItems.Contains(child),
                "a toolbar control was neither placed on the bar nor moved into the drop-down");
        }
    }

    [Fact(Timeout = 600000)]
    public void The_Overflowed_Commands_Are_Reachable_Through_The_Chevron()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        // By name rather than by drawn text: Outdent is an icon now, and its caption survives only
        // as the name it reports - which is exactly what a screen reader and the drop-down use.
        Assert.DoesNotContain(app.Toolbar.Children, child => IsOnTheBar(app, child, "Outdent"));

        Click(app, Middle(app.Toolbar.OverflowButtonBounds));

        Assert.True(app.Toolbar.IsOverflowOpen);
        Assert.Contains(app.Toolbar.OverflowItems, item => IsLabelled(app, item, "Outdent"));
    }

    [Fact(Timeout = 600000)]
    public void A_Command_Chosen_From_The_Drop_Down_Runs_And_Shuts_It()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();
        Click(app, Middle(app.Toolbar.OverflowButtonBounds));
        app.RenderFrame();

        UiElement indent = Assert.Single(
            app.Toolbar.OverflowItems.Where(item => IsLabelled(app, item, "Indent")));
        Click(app, Middle(indent.Bounds));

        Assert.False(app.Toolbar.IsOverflowOpen);
        Assert.Equal("Indent", app.LastAction);
    }

    [Fact(Timeout = 600000)]
    public void A_Window_Wide_Enough_For_Everything_Shows_No_Chevron()
    {
        using WriterApp app = CreateApp(WindowsHeadWidth, WindowsHeadHeight);
        app.RenderFrame();

        Assert.Empty(app.Toolbar.OverflowItems);
        Assert.DoesNotContain("»", DrawnText(app));
        Assert.Contains(app.Toolbar.Children, child => IsOnTheBar(app, child, "Outdent"));
    }

    [Fact(Timeout = 600000)]
    public void The_Zoom_Picker_Still_Works_From_Inside_The_Drop_Down()
    {
        // Narrow enough that the zoom group overflows too. The picker has a list
        // of its own, and a list inside a list is the case the bar has to route
        // rather than read as a press on itself.
        using WriterApp app = CreateApp(PickerOverflowWidth, 820);
        app.RenderFrame();
        StandardComboBox picker = Assert.Single(app.Toolbar.OverflowItems.OfType<StandardComboBox>());

        Click(app, Middle(app.Toolbar.OverflowButtonBounds));
        app.RenderFrame();
        Click(app, Middle(picker.Bounds));
        app.RenderFrame();

        Assert.True(picker.IsDropDownOpen, "the picker's own list did not open");
        Assert.True(app.Toolbar.IsOverflowOpen, "the bar shut the drop-down the picker lives in");

        int level = WriterZoom.IndexOf(2);
        BRect list = picker.PopupBounds;
        Click(app, new BPoint(
            list.Left + (list.Width / 2),
            list.Top + (picker.ItemHeight * level) + (picker.ItemHeight / 2)));

        Assert.Equal(2, app.Zoom);
        Assert.False(picker.IsDropDownOpen);
        Assert.False(app.Toolbar.IsOverflowOpen);
    }

    // --- Harness -----------------------------------------------------------

    private static WriterApp CreateApp(double width = NarrowWidth, double height = WindowsHeadHeight)
    {
        var host = new WriterUiHost(
            () => new BSize(width, height),
            () => 1,
            () => { },
            _ => { });
        return new WriterApp(host, () => { });
    }

    /// <summary>Whether a control is on the bar under the given name, rather than in the drop-down.</summary>
    private static bool IsOnTheBar(WriterApp app, UiElement child, string text) =>
        child.Visibility == UiVisibility.Visible &&
        !app.Toolbar.OverflowItems.Contains(child) &&
        child.Bounds.Width > 0 &&
        IsLabelled(app, child, text);

    private static string[] DrawnText(WriterApp app) =>
        app.RenderFrame().Commands
            .OfType<BRenderCommand.DrawText>()
            .Select(command => command.Text.Text)
            .ToArray();

    /// <summary>
    /// Whether an item in the drop-down is the one labelled <paramref name="text"/>.
    /// The toolbar holds bare <see cref="UiElement"/>s, and its buttons carry their
    /// label in the semantic name rather than on a type this assembly can see.
    /// </summary>
    private static bool IsLabelled(WriterApp app, UiElement item, string text)
    {
        _ = app;
        return string.Equals(item.GetSemanticNode().Name, text, StringComparison.Ordinal);
    }

    private static BPoint Middle(BRect rect) => new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

    private static void Click(WriterApp app, BPoint point)
    {
        app.Dispatch(Mouse(point, MouseButtonTransition.Down));
        app.Dispatch(Mouse(point, MouseButtonTransition.Up));
    }

    private static UiInputEvent Mouse(BPoint point, MouseButtonTransition transition) =>
        UiInputEvent.FromMouseButton(new MouseButtonEvent(
            new InputEventHeader(
                InputDeviceId.FromOpaqueValue("writer-toolbar-mouse"),
                new InputTimestamp(1, 1_000, "test"),
                1),
            InputPoint.ClientDeviceIndependentPixels(point.X, point.Y),
            transition == MouseButtonTransition.Down ? MouseButtons.Left : MouseButtons.None,
            MouseButton.Left,
            transition));
}
