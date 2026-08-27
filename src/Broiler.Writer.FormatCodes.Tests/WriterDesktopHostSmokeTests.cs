using Broiler.Graphics;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.FormatCodeView.Standard;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterDesktopHostSmokeTests
{
    [Fact(Timeout = 600000)]
    public void Desktop_Host_Renders_Toggles_And_Cycles_Focus()
    {
        var host = new Broiler.Writer.WriterUiHost(
            () => new BSize(1200, 800),
            () => 1,
            () => { },
            _ => { });
        using var app = new Broiler.Writer.WriterApp(host, () => { });

        BRenderList initial = app.RenderFrame();
        Assert.True(initial.Count > 0);

        app.Dispatch(Key("F6", KeyboardModifierState.None));
        Assert.IsType<StandardFormatCodeView>(app.Session.FocusedElement);

        KeyboardModifierState toggle = KeyboardModifierState.Control | KeyboardModifierState.Shift;
        app.Dispatch(Key("F3", toggle));
        Assert.IsNotType<StandardFormatCodeView>(app.Session.FocusedElement);
        app.Dispatch(Key("F3", toggle));

        BRenderList restored = app.RenderFrame();
        Assert.True(restored.Count > 0);
    }

    private static UiInputEvent Key(string name, KeyboardModifierState modifiers)
    {
        var header = new InputEventHeader(
            InputDeviceId.FromOpaqueValue("formatting-codes-smoke-keyboard"),
            new InputTimestamp(1, 1_000, "test"),
            1);
        return UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(
            header,
            KeyboardKey.FromName(name),
            KeyboardKeyTransition.Down,
            modifiers,
            NativeKeyCode: 0,
            ScanCode: 0,
            RepeatCount: 1,
            IsExtended: false,
            WasDown: false));
    }
}
