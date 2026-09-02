using System;
using System.IO;
using System.Linq;
using System.Text;
using Broiler.Graphics;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.UI;
using Broiler.UI.Toolbar;
using Xunit;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Coverage for what the window says about the document, and where it says it.
/// </summary>
/// <remarks>
/// The document's name used to be a heading inside the window and nowhere else - the caption said
/// "Broiler Writer" whatever was open, and nothing anywhere tracked whether the document had
/// unsaved changes. Both are the window's job, so both are asserted here.
/// </remarks>
public sealed class WriterShellTests
{
    [Fact(Timeout = 600000)]
    public void A_Fresh_Document_Names_Itself_In_The_Caption()
    {
        using WriterApp app = CreateApp();

        Assert.Equal("Untitled document", app.DocumentName);
        Assert.False(app.IsModified);
        Assert.Equal("Untitled document — Broiler Writer", app.WindowTitle);
    }

    [Fact(Timeout = 600000)]
    public void The_Caption_Reaches_The_Head_That_Owns_The_Window()
    {
        string? lastTitle = null;
        using WriterApp app = CreateApp(setWindowTitle: title => lastTitle = title);

        app.LoadDocument(RtfStream("hello"), "report.rtf");

        Assert.Equal("report.rtf — Broiler Writer", lastTitle);
    }

    [Fact(Timeout = 600000)]
    public void Opening_A_Document_Does_Not_Mark_It_Modified()
    {
        using WriterApp app = CreateApp();

        Assert.True(app.LoadDocument(RtfStream("hello"), "report.rtf"));

        // The editor raises DocumentChanged for a programmatic load exactly as it does for a
        // keystroke. Without the suppression around the load, every file would arrive dirty.
        Assert.Equal("report.rtf", app.DocumentName);
        Assert.False(app.IsModified);
        Assert.DoesNotContain("•", app.WindowTitle);
    }

    [Fact(Timeout = 600000)]
    public void Editing_Marks_The_Document_And_The_Caption()
    {
        using WriterApp app = CreateApp();
        app.LoadDocument(RtfStream("hello"), "report.rtf");

        Type_(app, "x");

        Assert.True(app.IsModified);
        Assert.Equal("• report.rtf — Broiler Writer", app.WindowTitle);
    }

    [Fact(Timeout = 600000)]
    public void Saving_Clears_The_Mark()
    {
        using WriterApp app = CreateApp();
        app.LoadDocument(RtfStream("hello"), "report.rtf");
        Type_(app, "x");
        Assert.True(app.IsModified);

        using var sink = new MemoryStream();
        Assert.True(app.WriteDocument(sink, "report.rtf"));

        Assert.False(app.IsModified);
        Assert.Equal("report.rtf — Broiler Writer", app.WindowTitle);
    }

    [Fact(Timeout = 600000)]
    public void The_Status_Line_Is_Two_Halves_And_Not_Seven_Segments()
    {
        using WriterApp app = CreateApp();

        string[] drawn = DrawnText(app);

        // Facts on the left, state on the right, and nothing restating what is already on screen:
        // the pane says whether it is open by being open, and the last command run is not a status.
        Assert.Contains(drawn, text => text.Contains("paragraphs", StringComparison.Ordinal) && text.Contains('·'));
        Assert.Contains(drawn, text => text.Contains("100%", StringComparison.Ordinal));
        Assert.DoesNotContain(drawn, text => text.Contains("Formatting Codes shown", StringComparison.Ordinal));
        Assert.DoesNotContain(drawn, text => text.Contains(" | ", StringComparison.Ordinal));
    }

    [Fact(Timeout = 600000)]
    public void A_Failed_Save_Still_Gets_Said()
    {
        using WriterApp app = CreateApp();

        // Trimming the status line took away the running commentary, not the reporting. A save to
        // a stream that refuses to be written is the cleanest way to produce a real failure: it
        // does not depend on any codec's judgement about what it was handed.
        using var readOnly = new MemoryStream(new byte[16], writable: false);
        Assert.False(app.WriteDocument(readOnly, "report.rtf"));

        Assert.StartsWith("Save failed", app.LastAction, StringComparison.Ordinal);
        Assert.Contains(DrawnText(app), text => text.Contains(app.LastAction, StringComparison.Ordinal));
    }

    [Fact(Timeout = 600000)]
    public void A_Problem_Retires_Once_The_Next_Open_Succeeds()
    {
        using WriterApp app = CreateApp();
        using var readOnly = new MemoryStream(new byte[16], writable: false);
        app.WriteDocument(readOnly, "report.rtf");

        app.LoadDocument(RtfStream("hello"), "report.rtf");

        Assert.DoesNotContain(DrawnText(app), text => text.Contains("Save failed", StringComparison.Ordinal));
    }

    [Fact(Timeout = 600000)]
    public void The_Formatting_Codes_Pane_Is_Headed_And_Goes_Away_Whole()
    {
        using WriterApp app = CreateApp();

        Assert.Contains("Formatting Codes", DrawnText(app));

        app.Menu.CommandDispatcher!.TryExecute("view.formatting-codes");

        // Header, splitter and pane leave together, so the document takes the whole space.
        Assert.DoesNotContain("Formatting Codes", DrawnText(app));
    }

    [Fact(Timeout = 600000)]
    public void The_Document_Starts_Directly_Under_The_Toolbar()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        // The heading that used to sit here is gone, so nothing but a margin separates the two.
        double gap = app.Editor.Bounds.Top - app.Toolbar.Bounds.Bottom;
        Assert.InRange(gap, 0, 20);
    }

    [Fact(Timeout = 600000)]
    public void Resting_On_An_Icon_Button_Asks_For_A_Tick_And_Then_Shows_A_Tip()
    {
        using WriterApp app = CreateApp();
        app.RenderFrame();

        UiElement save = Assert.Single(
            app.Toolbar.Children.Where(child => child.GetSemanticNode().Name == "Save"));
        Assert.Equal("Save (Ctrl+S)", save.ToolTipText);

        Assert.False(app.WantsTick);
        app.Dispatch(PointerMove(Middle(save.Bounds)));

        // An icon says nothing about itself, so the tip is the only thing that names the command -
        // and it needs the head to keep ticking until its delay is up.
        Assert.True(app.WantsTick);
    }

    [Fact(Timeout = 600000)]
    public void The_Compact_Bar_Builds_And_Groups_Only_What_It_Carries()
    {
        // Android is the only head that builds this, and no other test runs its code path: a
        // group break named for a control the compact bar never added throws at construction, on
        // device, and nowhere else. Building it here is what catches that.
        var host = new WriterUiHost(() => new BSize(480, 820), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { }, compactMode: true);
        app.RenderFrame();

        string[] names = app.Toolbar.Children
            .Select(child => child.GetSemanticNode().Name)
            .ToArray();

        Assert.Equal(["New", "Open", "Save", "Undo", "B", "I", "U"], names);
        Assert.DoesNotContain("Font...", names);

        // Every break the compact bar sets has to be for a control it actually has.
        foreach (UiElement child in app.Toolbar.Children)
            Assert.NotEqual(UiToolbarBreak.Separator, app.Toolbar.GetBreakBefore(child));
    }

    // --- Harness -----------------------------------------------------------

    private static WriterApp CreateApp(Action<string>? setWindowTitle = null)
    {
        var host = new WriterUiHost(
            () => new BSize(1120, 780),
            () => 1,
            () => { },
            _ => { });
        return new WriterApp(host, () => { }, setWindowTitle: setWindowTitle);
    }

    /// <summary>Types into the document the way a keystroke would, so DocumentChanged is raised.</summary>
    private static void Type_(WriterApp app, string text) =>
        app.Editor.SetPlainText(app.Editor.GetPlainText() + text);

    private static Stream RtfStream(string body) =>
        new MemoryStream(Encoding.ASCII.GetBytes(@"{\rtf1\ansi " + body + "}"));

    private static string[] DrawnText(WriterApp app) =>
        app.RenderFrame().Commands
            .OfType<BRenderCommand.DrawText>()
            .Select(command => command.Text.Text)
            .ToArray();

    private static BPoint Middle(BRect bounds) =>
        new(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));

    private static UiInputEvent PointerMove(BPoint position) =>
        UiInputEvent.FromMouseMove(
            new MouseMoveEvent(
                new InputEventHeader(
                    InputDeviceId.FromOpaqueValue("shell-tests-mouse"),
                    new InputTimestamp(1, TimeSpan.TicksPerSecond, "shell"),
                    1),
                InputPoint.ClientDeviceIndependentPixels(position.X, position.Y),
                MouseButtons.None,
                Source: InputEventSource.Synthetic));
}
