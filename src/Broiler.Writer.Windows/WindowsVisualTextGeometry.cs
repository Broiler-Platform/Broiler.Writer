#if BROILER_DOCUMENTS_SOURCE_GEOMETRY
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Documents.Pagination;
using Broiler.Graphics.Text;
using Broiler.Native.Windows.Direct2D;
using static Broiler.Native.Windows.Direct2D.DWriteNative;
using static Broiler.Native.Windows.Direct2D.DirectWriteTextMetricsProviderApi;

namespace Broiler.Writer;

/// <summary>Asks the Windows text renderer for the logical edges of a visual text range.</summary>
[SupportedOSPlatform("windows7.0")]
internal static class WindowsVisualTextGeometry
{
    // dwrite.h, IDWriteTextLayout: GetMetrics (60), GetOverhangMetrics, GetClusterMetrics,
    // DetermineMinWidth, HitTestPoint and HitTestTextPosition.
    private const int HitTestTextPositionSlot = VtblGetMetrics + 5;
    private const float LayoutExtent = 1_048_576f;
    private static readonly Lazy<ComPtr?> Factory = new(CreateFactory);

    internal static IReadOnlyList<LayoutVisualTextRange>? Measure(
        string text, BFontStyle font, IReadOnlyList<LayoutDisplayRange> ranges)
    {
        if (font.Face is not null || Factory.Value is not { } factory)
            return null;
        var createFormat = ComVtable.Method<CreateTextFormatProc>(factory.Pointer, VtblCreateTextFormat);
        string locale = CultureInfo.CurrentUICulture.Name;
        float size = double.IsFinite(font.Size) && font.Size > 0 ? (float)Math.Min(font.Size, float.MaxValue) : 1f;
        int hr = createFormat(factory.Pointer, ResolveFamily(font.FamilyName), IntPtr.Zero, (DWRITE_FONT_WEIGHT)(uint)font.Weight,
            font.Slant switch
            {
                BFontSlant.Italic => DWRITE_FONT_STYLE.ITALIC,
                BFontSlant.Oblique => DWRITE_FONT_STYLE.OBLIQUE,
                _ => DWRITE_FONT_STYLE.NORMAL,
            }, DWRITE_FONT_STRETCH.NORMAL, size, string.IsNullOrWhiteSpace(locale) ? "en-us" : locale, out IntPtr formatPointer);
        if (hr < 0 || formatPointer == IntPtr.Zero)
            return null;
        using var format = new ComPtr(formatPointer);
        var createLayout = ComVtable.Method<CreateTextLayoutProc>(factory.Pointer, VtblCreateTextLayout);
        hr = createLayout(factory.Pointer, text, (uint)text.Length, format.Pointer,
            LayoutExtent, LayoutExtent, out IntPtr layoutPointer);
        if (hr < 0 || layoutPointer == IntPtr.Zero)
            return null;

        using var layout = new ComPtr(layoutPointer);
        var hitTest = ComVtable.Method<HitTestTextPosition>(layout.Pointer, HitTestTextPositionSlot);
        var result = new LayoutVisualTextRange[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (range.Length <= 0 || range.Offset < 0 || range.Offset > text.Length - range.Length ||
                hitTest(layout.Pointer, (uint)range.Offset, 0, out float start, out _, out _) < 0 ||
                hitTest(layout.Pointer, (uint)(range.Offset + range.Length - 1), 1, out float end, out _, out _) < 0)
                return null;
            result[i] = new LayoutVisualTextRange(start, end);
        }
        return result;
    }

    // Match the family selection in the Direct2D text format cache.
    private static string ResolveFamily(string familyName)
    {
        foreach (string candidate in familyName.Split(','))
        {
            string family = candidate.Trim().Trim('"', '\'').Trim();
            if (family.Length == 0)
                continue;
            return family.ToLowerInvariant() switch
            {
                "sans-serif" or "system-ui" or "ui-sans-serif" or "ui-rounded" => "Segoe UI",
                "serif" or "ui-serif" => "Times New Roman",
                "monospace" or "monospaced" or "ui-monospace" => "Consolas",
                "cursive" => "Segoe Script",
                "fantasy" => "Impact",
                _ => family,
            };
        }

        return "Segoe UI";
    }

    private static ComPtr? CreateFactory()
    {
        int hr = NativeMethods.DWriteCreateFactory(DWRITE_FACTORY_TYPE.SHARED, IID_IDWriteFactory, out IntPtr factory);
        return hr >= 0 && factory != IntPtr.Zero ? new ComPtr(factory) : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HitTestTextPosition(IntPtr self, uint position, int trailing, out float x, out float y,
        out HitTestMetrics metrics);

    [StructLayout(LayoutKind.Sequential)]
    private struct HitTestMetrics
    {
        public uint Position, Length;
        public float Left, Top, Width, Height;
        public uint BidiLevel;
        public int IsText, IsTrimmed;
    }
}
#endif
