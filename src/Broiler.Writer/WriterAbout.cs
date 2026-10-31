using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.UI.AboutDialog.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

/// <summary>About dialog composition shared by the desktop and browser Writer.</summary>
public static class WriterAbout
{
    private static readonly Lazy<BImageResource> Artwork = new(() =>
    {
        using Stream stream = typeof(WriterAbout).Assembly.GetManifestResourceStream("Broiler.Writer.About.png")
            ?? throw new InvalidOperationException("The embedded About picture is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return BImageResource.FromEncoded(bytes.ToArray(), "image/png");
    });

    public static void Show(UiWindow owner, BSize viewport, Assembly productAssembly, Action<Uri>? openUrl = null)
    {
        if (owner.IsDisposed || owner.IsClosed)
            return;

        var dialog = new StandardAboutDialog();
        // Use Writer's assembly even when hosted by another application or a test runner.
        // Broiler.UI preserves the prerelease label and omits the SDK's commit hash.
        var components = new[] { productAssembly, typeof(WriterAbout).Assembly, typeof(StandardAboutDialog).Assembly };
        dialog.PopulateFromAssemblies(productAssembly, components);
        dialog.ProductName = "Broiler Writer";
        dialog.Title = "About Broiler Writer";
        if (OperatingSystem.IsBrowser())
        {
            // Match Cells: the rotated title-bar close glyph triggers whole-frame
            // software rasterization in the browser. OK and Escape still dismiss it.
            dialog.CanClose = false;
        }
        dialog.Picture = Artwork.Value;
        dialog.PreferredSize = new BSize(820, 420);
        dialog.PictureAltText = "Broiler chicken writing at a desk";
        dialog.ProductUrl = new Uri("https://broiler-platform.github.io/");
        dialog.ProductUrlRequested += (_, url) =>
        {
            try
            {
                (openUrl ?? OpenDesktopUrl)(url);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                dialog.WebsiteButton.Text = "Unable to open website";
            }
        };

        double width = Math.Min(dialog.PreferredSize.Width, Math.Max(0, viewport.Width - 32));
        double height = Math.Min(dialog.PreferredSize.Height, Math.Max(0, viewport.Height - 32));
        _ = dialog.ShowModal(owner, new BRect(
            Math.Max(0, (viewport.Width - width) / 2),
            Math.Max(0, (viewport.Height - height) / 2), width, height));
    }

    private static void OpenDesktopUrl(Uri url)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new NotSupportedException("This host must supply website navigation.");
        using Process? process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
