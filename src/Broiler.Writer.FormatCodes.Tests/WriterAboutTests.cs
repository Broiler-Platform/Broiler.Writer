using System.Reflection;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Broiler.UI.AboutDialog.Standard;
using Broiler.UI.Dialog;

namespace Broiler.Writer.FormatCodes.Tests;

public sealed class WriterAboutTests
{
    [Theory]
    [InlineData(1200, 800, "Enter", UiDialogResultKind.Accepted)]
    [InlineData(380, 300, "Escape", UiDialogResultKind.Cancelled)]
    public void About_Menu_Shows_Writer_Metadata_And_Restores_Editor_Focus(
        double width, double height, string key, UiDialogResultKind result)
    {
        using var host = new WriterUiHost(() => new BSize(width, height), () => 1, () => { }, _ => { });
        using var app = new WriterApp(host, () => { });
        app.RenderFrame();

        Assert.True(app.Menu.CommandDispatcher!.TryExecute("help.about"));
        using var dialog = Assert.IsType<StandardAboutDialog>(app.Session.FocusedElement);
        app.RenderFrame();

        string version = typeof(WriterApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal("Broiler Writer", dialog.ProductName);
        Assert.Equal(version, dialog.ProductVersion);
        Assert.Equal(version, dialog.ComponentVersions["Broiler.Writer.Core"]);
        Assert.Contains("Broiler.UI.AboutDialog.Standard", dialog.ComponentVersions.Keys);
        Assert.True(dialog.IsModal);
        Assert.InRange(dialog.Bounds.Left, 0, width);
        Assert.InRange(dialog.Bounds.Right, 0, width);
        Assert.InRange(dialog.Bounds.Top, 0, height);
        Assert.InRange(dialog.Bounds.Bottom, 0, height);
        Assert.True(dialog.OkButton.Bounds.Bottom <= dialog.Bounds.Bottom);

        app.Session.SetFocus(dialog.ComponentList);
        var header = new InputEventHeader(InputDeviceId.FromOpaqueValue("writer-about-test"),
            new InputTimestamp(1, 1000, "test"), 1);
        app.Dispatch(UiInputEvent.FromKeyboardKey(new KeyboardKeyEvent(header,
            KeyboardKey.FromName(key), KeyboardKeyTransition.Down, KeyboardModifierState.None,
            NativeKeyCode: 0, ScanCode: 0, RepeatCount: 1, IsExtended: false, WasDown: false)));

        Assert.False(dialog.IsPresented);
        Assert.Equal(result, dialog.CompletedResult.Kind);
        Assert.Same(app.Editor, app.Session.FocusedElement);

        Assert.True(app.Menu.CommandDispatcher.TryExecute("help.about"));
        using var reopened = Assert.IsType<StandardAboutDialog>(app.Session.FocusedElement);
        Assert.NotSame(dialog, reopened);
        Assert.True(reopened.Cancel());
    }
}
