using Broiler.Graphics;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Mouse;
using Broiler.UI;
using Broiler.UI.Menu;
using Broiler.Writer.FormatCodes;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Choosing the size a document is read at. The ladder is shared policy, so the
/// toolbar picker, the View menu, Ctrl+plus and Ctrl+wheel all land on the same
/// levels, and the editor is the one place the number lives.
/// </summary>
public sealed class WriterZoomTests
{
    // --- The ladder --------------------------------------------------------

    [Fact(Timeout = 600000)]
    public void The_Levels_Run_Small_To_Large_And_Include_The_Stated_Size()
    {
        Assert.NotEmpty(WriterZoom.Levels);
        for (int i = 1; i < WriterZoom.Levels.Count; i++)
            Assert.True(WriterZoom.Levels[i] > WriterZoom.Levels[i - 1], "the levels are not in order");

        Assert.Contains(WriterZoom.Default, WriterZoom.Levels);
        Assert.Equal(WriterZoom.Levels[0], WriterZoom.Minimum);
        Assert.Equal(WriterZoom.Levels[^1], WriterZoom.Maximum);
    }

    [Theory(Timeout = 600000)]
    [InlineData(0.001)]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_Zoom_Under_The_Ladder_Is_Clamped_To_Its_Foot(double requested) =>
        Assert.Equal(WriterZoom.Minimum, WriterZoom.Normalize(requested));

    [Fact(Timeout = 600000)]
    public void A_Zoom_Over_The_Ladder_Is_Clamped_To_Its_Head() =>
        Assert.Equal(WriterZoom.Maximum, WriterZoom.Normalize(400));

    [Theory(Timeout = 600000)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_Zoom_That_Is_Not_A_Number_Reads_As_The_Stated_Size(double requested) =>
        Assert.Equal(WriterZoom.Default, WriterZoom.Normalize(requested));

    [Fact(Timeout = 600000)]
    public void Stepping_In_And_Out_Walks_The_Ladder()
    {
        double zoom = WriterZoom.Default;
        int index = WriterZoom.IndexOf(zoom);

        zoom = WriterZoom.In(zoom);

        Assert.Equal(WriterZoom.Levels[index + 1], zoom);
        Assert.Equal(WriterZoom.Levels[index], WriterZoom.Out(zoom));
    }

    [Fact(Timeout = 600000)]
    public void Stepping_Past_Either_End_Stays_At_That_End()
    {
        Assert.Equal(WriterZoom.Maximum, WriterZoom.In(WriterZoom.Maximum));
        Assert.Equal(WriterZoom.Minimum, WriterZoom.Out(WriterZoom.Minimum));
    }

    [Fact(Timeout = 600000)]
    public void A_Zoom_Between_Two_Levels_Steps_To_The_One_Either_Side_Of_It()
    {
        // Nothing in the shipped UI produces one, but a stored or typed setting can.
        const double Between = 1.1;

        Assert.Equal(1.25, WriterZoom.In(Between));
        Assert.Equal(1, WriterZoom.Out(Between));
    }

    [Fact(Timeout = 600000)]
    public void Reset_Goes_Back_To_The_Size_The_Document_States() =>
        Assert.Equal(WriterZoom.Default, WriterZoom.Apply(WriterZoom.Maximum, WriterZoomStep.Reset));

    // --- Reading and writing a level ---------------------------------------

    [Fact(Timeout = 600000)]
    public void A_Level_Is_Written_As_A_Whole_Percentage()
    {
        Assert.Equal("100%", WriterZoom.Describe(1));
        Assert.Equal("125%", WriterZoom.Describe(1.25));
        Assert.Equal("25%", WriterZoom.Describe(0.25));
    }

    [Fact(Timeout = 600000)]
    public void Every_Level_Reads_Back_As_Itself()
    {
        foreach (double level in WriterZoom.Levels)
        {
            Assert.True(WriterZoom.TryParse(WriterZoom.Describe(level), out double parsed));
            Assert.Equal(level, parsed);
        }
    }

    [Theory(Timeout = 600000)]
    [InlineData("150")]
    [InlineData("150%")]
    [InlineData("  150 % ")]
    public void A_Percentage_Is_Read_With_Or_Without_Its_Sign(string text)
    {
        Assert.True(WriterZoom.TryParse(text, out double zoom));
        Assert.Equal(1.5, zoom);
    }

    [Theory(Timeout = 600000)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("big")]
    [InlineData("-50%")]
    [InlineData("0%")]
    [InlineData(null)]
    public void Anything_Else_Is_Refused_Rather_Than_Guessed_At(string? text)
    {
        Assert.False(WriterZoom.TryParse(text, out double zoom));
        Assert.Equal(WriterZoom.Default, zoom);
    }

    // --- The gestures ------------------------------------------------------

    [Theory(Timeout = 600000)]
    [InlineData("=", 0xBB, WriterZoomStep.In)]
    [InlineData("+", 0xBB, WriterZoomStep.In)]
    [InlineData("Equal", 0, WriterZoomStep.In)]
    [InlineData("Add", 0x6B, WriterZoomStep.In)]
    [InlineData("-", 0xBD, WriterZoomStep.Out)]
    [InlineData("Minus", 0, WriterZoomStep.Out)]
    [InlineData("Subtract", 0x6D, WriterZoomStep.Out)]
    [InlineData("0", 0x30, WriterZoomStep.Reset)]
    [InlineData("Numpad0", 0x60, WriterZoomStep.Reset)]
    public void Ctrl_With_Plus_Minus_Or_Zero_Is_A_Zoom(string name, int code, WriterZoomStep expected) =>
        Assert.Equal(
            expected,
            WriterZoom.StepFor(name, code, KeyboardModifierState.Control, isDown: true));

    [Theory(Timeout = 600000)]
    [InlineData("VirtualKey:187", WriterZoomStep.In)]
    [InlineData("VirtualKey:189", WriterZoomStep.Out)]
    [InlineData("VirtualKey:48", WriterZoomStep.Reset)]
    public void A_Head_That_Only_Numbers_Its_Keys_Reaches_The_Same_Step(string name, WriterZoomStep expected)
    {
        // The Windows head names every key "VirtualKey:<code>" and carries the
        // code alongside; the browser and Linux heads name them properly.
        Assert.Equal(expected, WriterZoom.StepFor(name, 0, KeyboardModifierState.Control, isDown: true));
    }

    [Fact(Timeout = 600000)]
    public void Plus_Without_Ctrl_Is_Just_A_Plus() =>
        Assert.Equal(
            WriterZoomStep.None,
            WriterZoom.StepFor("+", 0xBB, KeyboardModifierState.None, isDown: true));

    [Fact(Timeout = 600000)]
    public void Ctrl_Alt_Plus_Is_Left_Alone_Because_It_Is_Someone_Elses_Chord() =>
        Assert.Equal(
            WriterZoomStep.None,
            WriterZoom.StepFor("+", 0xBB, KeyboardModifierState.Control | KeyboardModifierState.Alt, isDown: true));

    [Fact(Timeout = 600000)]
    public void Releasing_The_Key_Zooms_Nothing() =>
        Assert.Equal(
            WriterZoomStep.None,
            WriterZoom.StepFor("+", 0xBB, KeyboardModifierState.Control, isDown: false));

    [Theory(Timeout = 600000)]
    [InlineData(true, 1, WriterZoomStep.In)]
    [InlineData(true, -1, WriterZoomStep.Out)]
    [InlineData(true, 0, WriterZoomStep.None)]
    [InlineData(false, 1, WriterZoomStep.None)]
    public void The_Wheel_Zooms_Only_While_Ctrl_Is_Held(bool control, double notches, WriterZoomStep expected) =>
        Assert.Equal(expected, WriterZoom.StepForWheel(control, notches));

    // --- The application ---------------------------------------------------

    [Fact(Timeout = 600000)]
    public void A_Fresh_Writer_Reads_At_The_Stated_Size_And_Says_So()
    {
        using WriterApp app = CreateApp();

        Assert.Equal(WriterZoom.Default, app.Zoom);
        Assert.Contains("100%", DrawnText(app));
    }

    [Fact(Timeout = 600000)]
    public void The_Toolbar_Carries_A_Zoom_Picker_And_Its_Two_Steps()
    {
        using WriterApp app = CreateApp();

        string[] labels = DrawnText(app);

        Assert.Contains("100%", labels);
        Assert.Contains("+", labels);
        Assert.Contains("-", labels);
    }

    [Fact(Timeout = 600000)]
    public void The_Picker_Shows_The_Level_The_Document_Is_Read_At()
    {
        using WriterApp app = CreateApp();

        app.Menu.CommandDispatcher!.TryExecute("view.zoom.150");

        string[] labels = DrawnText(app);
        Assert.Contains("150%", labels);
        Assert.DoesNotContain("100%", labels);
    }

    [Fact(Timeout = 600000)]
    public void The_View_Menu_Offers_The_Ladder_And_Marks_The_Current_Level()
    {
        using WriterApp app = CreateApp();
        UiMenuItem zoom = ZoomMenu(app);

        Assert.Equal(WriterZoom.Levels.Count, zoom.Children.Count);
        foreach (UiMenuItem item in zoom.Children)
            Assert.True(item.IsCheckable, item.Text + " is not checkable");

        Assert.Equal("100%", Assert.Single(zoom.Children.Where(item => item.IsChecked)).Text);
    }

    [Fact(Timeout = 600000)]
    public void Choosing_A_Level_From_The_Menu_Moves_The_Check_With_It()
    {
        using WriterApp app = CreateApp();

        app.Menu.CommandDispatcher!.TryExecute("view.zoom.200");
        app.RenderFrame();

        Assert.Equal(2, app.Zoom);
        Assert.Equal("200%", Assert.Single(ZoomMenu(app).Children.Where(item => item.IsChecked)).Text);
    }

    [Fact(Timeout = 600000)]
    public void The_Menus_Zoom_In_And_Out_Step_The_Ladder()
    {
        using WriterApp app = CreateApp();

        app.Menu.CommandDispatcher!.TryExecute("view.zoom.in");
        Assert.Equal(WriterZoom.In(WriterZoom.Default), app.Zoom);

        app.Menu.CommandDispatcher.TryExecute("view.zoom.out");
        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void Actual_Size_Comes_Back_From_Anywhere_On_The_Ladder()
    {
        using WriterApp app = CreateApp();

        app.Menu.CommandDispatcher!.TryExecute("view.zoom.400");
        app.Menu.CommandDispatcher.TryExecute("view.zoom.reset");

        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void Ctrl_Plus_And_Ctrl_Minus_Zoom_From_The_Keyboard()
    {
        using WriterApp app = CreateApp();

        app.Dispatch(Key("+", 0xBB, KeyboardModifierState.Control));
        Assert.Equal(WriterZoom.In(WriterZoom.Default), app.Zoom);
        Assert.Contains("Zoom", app.LastAction);

        app.Dispatch(Key("-", 0xBD, KeyboardModifierState.Control));
        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void Ctrl_Zero_Comes_Back_To_The_Stated_Size()
    {
        using WriterApp app = CreateApp();

        app.Dispatch(Key("+", 0xBB, KeyboardModifierState.Control));
        app.Dispatch(Key("+", 0xBB, KeyboardModifierState.Control));
        Assert.NotEqual(WriterZoom.Default, app.Zoom);

        app.Dispatch(Key("0", 0x30, KeyboardModifierState.Control));

        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void Ctrl_And_The_Wheel_Zooms_Rather_Than_Scrolls()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        // The Windows head does not fill a wheel event's modifiers in, so Ctrl is
        // remembered from the key event that pressed it.
        app.Dispatch(Key("Control", 0x11, KeyboardModifierState.Control));
        app.Dispatch(Wheel(1));

        Assert.Equal(WriterZoom.In(WriterZoom.Default), app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void The_Wheel_On_Its_Own_Leaves_The_Zoom_Alone()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        app.Dispatch(Key("Control", 0x11, KeyboardModifierState.Control));
        app.Dispatch(Key("Control", 0x11, KeyboardModifierState.None, KeyboardKeyTransition.Up));
        app.Dispatch(Wheel(-3));

        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void Zooming_Is_How_The_Document_Is_Read_And_Never_Changes_It()
    {
        using WriterApp app = CreateApp();
        string before = app.Document.PlainText;

        app.Menu.CommandDispatcher!.TryExecute("view.zoom.400");
        app.RenderFrame();

        Assert.Equal(before, app.Document.PlainText);
    }

    [Fact(Timeout = 600000)]
    public void The_Toolbars_Plus_And_Minus_Zoom_When_They_Are_Clicked()
    {
        using WriterApp app = CreateApp();

        Click(app, PointOn(app, "+"));
        Assert.Equal(WriterZoom.In(WriterZoom.Default), app.Zoom);

        Click(app, PointOn(app, "-"));
        Assert.Equal(WriterZoom.Default, app.Zoom);
    }

    [Fact(Timeout = 600000)]
    public void The_Picker_Is_On_Screen_In_The_Window_The_Writer_Opens_In()
    {
        // This toolbar is wider than the window the Windows head opens, and it
        // clips rather than wraps, so a zoom control placed after the formatting
        // groups is drawn off the edge and can never be clicked.
        const double WindowsHeadWidth = 1120;
        using WriterApp app = CreateApp(WindowsHeadWidth, 780);

        BRenderCommand.DrawText picker = Assert.Single(
            app.RenderFrame().Commands.OfType<BRenderCommand.DrawText>()
                .Where(command => command.Text.Text == "100%"));

        Assert.True(
            picker.Origin.X < WindowsHeadWidth,
            $"the zoom picker was drawn at x={picker.Origin.X}, past the {WindowsHeadWidth} the window is wide");
    }

    [Fact(Timeout = 600000)]
    public void The_Picker_Drops_Down_The_Whole_Ladder()
    {
        using WriterApp app = CreateApp();

        Click(app, PointOn(app, "100%"));

        string[] labels = DrawnText(app);
        foreach (double level in WriterZoom.Levels)
            Assert.Contains(WriterZoom.Describe(level), labels);
    }

    // --- Harness -----------------------------------------------------------

    private static WriterApp CreateApp(double width = 1200, double height = 800)
    {
        var host = new WriterUiHost(
            () => new BSize(width, height),
            () => 1,
            () => { },
            _ => { });
        return new WriterApp(host, () => { });
    }

    private static string[] DrawnText(WriterApp app) =>
        app.RenderFrame().Commands
            .OfType<BRenderCommand.DrawText>()
            .Select(command => command.Text.Text)
            .ToArray();

    private static UiMenuItem ZoomMenu(WriterApp app)
    {
        UiMenuItem view = Assert.Single(app.Menu.Items.Where(item => item.Id == "view"));
        return Assert.Single(view.Children.Where(item => item.Id == "zoom"));
    }

    private static InputEventHeader Header(string device) =>
        new(InputDeviceId.FromOpaqueValue(device), new InputTimestamp(1, 1_000, "test"), 1);

    private static UiInputEvent Key(
        string name,
        int nativeKeyCode,
        KeyboardModifierState modifiers,
        KeyboardKeyTransition transition = KeyboardKeyTransition.Down) =>
        UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(
            Header("writer-zoom-keyboard"),
            KeyboardKey.FromName(name),
            transition,
            modifiers,
            nativeKeyCode,
            ScanCode: 0,
            RepeatCount: 1,
            IsExtended: false,
            WasDown: false));

    /// <summary>
    /// A point inside the control whose only label is <paramref name="text"/>.
    /// The label is drawn inside its control, so its origin is a point on it -
    /// which is how a test reaches a toolbar control the app does not expose.
    /// </summary>
    private static BPoint PointOn(WriterApp app, string text)
    {
        BRenderCommand.DrawText drawn = Assert.Single(
            app.RenderFrame().Commands.OfType<BRenderCommand.DrawText>()
                .Where(command => command.Text.Text == text));
        return new BPoint(drawn.Origin.X + 2, drawn.Origin.Y + 2);
    }

    private static void Click(WriterApp app, BPoint point)
    {
        app.Dispatch(Mouse(point, MouseButtonTransition.Down));
        app.Dispatch(Mouse(point, MouseButtonTransition.Up));
    }

    private static UiInputEvent Mouse(BPoint point, MouseButtonTransition transition) =>
        UiInputEvent.FromMouseButton(new MouseButtonEvent(
            Header("writer-zoom-mouse"),
            InputPoint.ClientDeviceIndependentPixels(point.X, point.Y),
            transition == MouseButtonTransition.Down ? MouseButtons.Left : MouseButtons.None,
            MouseButton.Left,
            transition));

    private static UiInputEvent Wheel(double notches) =>
        UiInputEvent.FromMouseWheel(new MouseWheelEvent(
            Header("writer-zoom-mouse"),
            InputPoint.ClientDeviceIndependentPixels(400, 300),
            MouseButtons.None,
            MouseWheelAxis.Vertical,
            notches));
}
