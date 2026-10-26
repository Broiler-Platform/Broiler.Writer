using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Paging;
using Broiler.Graphics.RenderList;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.Menu;
using Broiler.UI.PrintDialog.Standard;
using Xunit;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// File, Print and Ctrl+P: the document laid out and previewed in the print dialog, handed to the
/// head's print host as the pages that were shown, and the outcome reported on the status line.
/// </summary>
public sealed class WriterPrintTests
{
    [Fact]
    public void A_Head_That_Cannot_Print_Offers_No_Print()
    {
        using WriterApp app = CreateApp(printHost: null);

        app.Dispatch(CtrlP());

        Assert.False(app.CanPrint);
        Assert.Null(app.Session.ModalElement);
        Assert.Equal("Printing is not available here", app.LastAction);
    }

    [Fact]
    public void The_File_Menu_Offers_Print_With_Its_Shortcut()
    {
        using WriterApp app = CreateApp(new FakePrintHost());

        UiMenuItem file = Assert.Single(app.Menu.Items.Where(item => item.Id == "file"));
        UiMenuItem print = Assert.Single(file.Children.Where(item => item.Id == "print"));

        Assert.Equal("Print...", print.Text);
        Assert.Equal("Ctrl+P", print.Accelerator);
        Assert.Equal("file.print", print.CommandName);
    }

    [Fact]
    public void Ctrl_P_Previews_The_Document_And_Prints_The_Pages_Shown()
    {
        var printer = new FakePrintHost();
        using WriterApp app = CreateApp(printer);
        app.Editor.SetPlainText("A letter to print.");

        app.Dispatch(CtrlP());

        StandardPrintDialog dialog = Assert.IsType<StandardPrintDialog>(app.Session.ModalElement);
        Assert.True(app.IsPrinting);
        Assert.Equal(1, dialog.Pages!.PageCount);
        BRenderList preview = app.RenderFrame();
        Assert.Contains(preview.Commands.OfType<BRenderCommand.DrawText>(), command => command.Text.Text.Contains("letter", StringComparison.Ordinal));

        Assert.True(dialog.AcceptPrint());
        WaitForPrint(app);

        UiPrintJob job = Assert.Single(printer.Jobs);
        Assert.Same(dialog.Pages, job.Pages);
        Assert.Equal("Untitled document", job.Title);
        Assert.Equal("Office", job.Settings.PrinterName);
        Assert.False(app.IsPrinting);
        Assert.Equal("Printed 1 page", app.LastAction);
    }

    [Fact]
    public void Cancelling_The_Dialog_Prints_Nothing_And_Says_So()
    {
        var printer = new FakePrintHost();
        using WriterApp app = CreateApp(printer);

        app.Dispatch(CtrlP());
        Assert.IsType<StandardPrintDialog>(app.Session.ModalElement).Cancel();
        WaitForPrint(app);

        Assert.Empty(printer.Jobs);
        Assert.Equal("Printing cancelled", app.LastAction);
        Assert.False(app.IsPrinting);
    }

    [Fact]
    public void A_Platform_With_Its_Own_Print_UI_Gets_The_Document_Straight_Away()
    {
        var printer = new FakePrintHost { ShowsSystemPrintDialog = true };
        using WriterApp app = CreateApp(printer);
        app.Editor.SetPlainText("Straight to the system dialog.");

        app.Dispatch(CtrlP());
        WaitForPrint(app);

        Assert.Null(app.Session.ModalElement);
        UiPrintJob job = Assert.Single(printer.Jobs);
        Assert.Null(job.Pages);
        Assert.Equal(1, job.ResolvePages().PageCount);
    }

    [Fact]
    public void A_Printer_That_Refuses_The_Job_Is_Reported_As_A_Problem()
    {
        var printer = new FakePrintHost { Refusal = "the queue is paused" };
        using WriterApp app = CreateApp(printer);

        app.Dispatch(CtrlP());
        Assert.True(Assert.IsType<StandardPrintDialog>(app.Session.ModalElement).AcceptPrint());
        WaitForPrint(app);

        Assert.Equal("Could not print: the queue is paused", app.Problem);
    }

    [Theory]
    [InlineData("P", KeyboardModifierState.Control, true)]
    [InlineData("P", KeyboardModifierState.Control | KeyboardModifierState.Shift, false)]
    [InlineData("P", KeyboardModifierState.Control | KeyboardModifierState.Alt, false)]
    [InlineData("P", KeyboardModifierState.None, false)]
    [InlineData("O", KeyboardModifierState.Control, false)]
    public void Only_Ctrl_P_Prints(string key, KeyboardModifierState modifiers, bool prints)
    {
        Assert.Equal(prints, WriterPrintShortcut.IsPrint(key, 0, modifiers, isDown: true));
        Assert.False(WriterPrintShortcut.IsPrint(key, 0, modifiers, isDown: false));
    }

    /// <summary>
    /// Renders until the job has been reported. The dialog's flow resumes through whatever
    /// synchronization context the caller has - the test framework's here - so the outcome
    /// can land a moment after the call that finished it.
    /// </summary>
    private static void WaitForPrint(WriterApp app)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            app.RenderFrame();
            if (!app.IsPrinting)
                return;

            Thread.Sleep(5);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail("The print job never finished.");
    }

    private static WriterApp CreateApp(IUiPrintHost? printHost)
    {
        var host = new WriterUiHost(() => new BSize(1120, 780), () => 1, () => { }, _ => { });
        return new WriterApp(host, () => { }, printHost: printHost);
    }

    private static UiInputEvent CtrlP()
    {
        var header = new InputEventHeader(
            InputDeviceId.FromOpaqueValue("print-tests-keyboard"),
            new InputTimestamp(1, 1_000, "test"),
            1);
        return UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(
            header,
            KeyboardKey.FromName("P"),
            KeyboardKeyTransition.Down,
            KeyboardModifierState.Control,
            NativeKeyCode: 0,
            ScanCode: 0,
            RepeatCount: 1,
            IsExtended: false,
            WasDown: false));
    }

    private sealed class FakePrintHost : IUiPrintHost
    {
        public bool ShowsSystemPrintDialog { get; init; }

        public string? Refusal { get; init; }

        public List<UiPrintJob> Jobs { get; } = [];

        public IReadOnlyList<UiPrinterInfo> GetPrinters() => [new UiPrinterInfo("Office", IsDefault: true)];

        public UiPrinterCapabilities GetCapabilities(string printerName) => UiPrinterCapabilities.Unknown;

        public Task<UiPrintResult> PrintAsync(UiPrintJob job, CancellationToken cancellationToken = default)
        {
            Jobs.Add(job);
            return Task.FromResult(Refusal is null
                ? UiPrintResult.Printed(job.Settings.SelectPages(job.ResolvePages(cancellationToken).PageCount).Count)
                : UiPrintResult.Failed(Refusal));
        }
    }
}
