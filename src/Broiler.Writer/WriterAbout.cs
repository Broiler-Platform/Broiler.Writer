using System;
using System.Reflection;
using Broiler.Graphics.Geometry;
using Broiler.UI.AboutDialog.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

/// <summary>About dialog composition shared by the desktop and browser Writer.</summary>
public static class WriterAbout
{
    public static void Show(UiWindow owner, BSize viewport, Assembly productAssembly)
    {
        if (owner.IsDisposed || owner.IsClosed)
            return;

        var dialog = new StandardAboutDialog();
        // Use Writer's assembly even when hosted by another application or a test runner.
        // Broiler.UI preserves the prerelease label and omits the SDK's commit hash.
        dialog.PopulateFromAssemblies(productAssembly);
        dialog.ProductName = "Broiler Writer";
        dialog.Title = "About Broiler Writer";

        double width = Math.Min(dialog.PreferredSize.Width, Math.Max(0, viewport.Width - 32));
        double height = Math.Min(dialog.PreferredSize.Height, Math.Max(0, viewport.Height - 32));
        _ = dialog.ShowModal(owner, new BRect(
            Math.Max(0, (viewport.Width - width) / 2),
            Math.Max(0, (viewport.Height - height) / 2), width, height));
    }
}
