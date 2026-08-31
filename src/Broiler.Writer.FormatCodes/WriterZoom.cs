using System;
using System.Collections.Generic;
using System.Globalization;
using Broiler.Input.Keyboard;

namespace Broiler.Writer.FormatCodes;

/// <summary>What a zoom gesture asked for.</summary>
public enum WriterZoomStep
{
    /// <summary>The gesture was not a zoom one.</summary>
    None = 0,

    /// <summary>The next level up.</summary>
    In,

    /// <summary>The next level down.</summary>
    Out,

    /// <summary>Back to the size the document states.</summary>
    Reset,
}

/// <summary>
/// The zoom levels a Writer offers and the policy around them, shared by the
/// desktop and browser heads the way <see cref="WriterFormatCodesShortcut"/> is,
/// so 150% means the same thing and is reached the same way wherever a document
/// is opened.
/// </summary>
/// <remarks>
/// The levels are a ladder rather than a free scale because that is what the
/// toolbar, the menu and Ctrl+plus all step along; a free scale would leave the
/// three disagreeing about what "one step" means. Nothing here reads or writes a
/// document: zoom is how a document is being read, not part of it.
/// </remarks>
public static class WriterZoom
{
    /// <summary>Reading a document at exactly the size it states.</summary>
    public const double Default = 1;

    /// <summary>
    /// How close two zooms must be to count as the same level. The ladder is
    /// written as decimal literals and stepped rather than accumulated, so this
    /// only has to absorb the last bits of a parsed percentage.
    /// </summary>
    private const double Tolerance = 1e-9;

    /// <summary>
    /// The levels offered, smallest first. A quarter size shows a whole page of
    /// an A4 document at a glance; four times it is enough to read a footnote set
    /// in six point.
    /// </summary>
    public static IReadOnlyList<double> Levels { get; } = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>The smallest level, which is also the floor an arbitrary zoom is clamped to.</summary>
    public static double Minimum => Levels[0];

    /// <summary>The largest level, which is also the ceiling an arbitrary zoom is clamped to.</summary>
    public static double Maximum => Levels[^1];

    /// <summary>
    /// A zoom brought into range. Anything that is not a number reads as the
    /// stated size rather than as an error: there is no zoom to fall back to
    /// other than the one the document asked for.
    /// </summary>
    public static double Normalize(double zoom) =>
        double.IsFinite(zoom) ? Math.Clamp(zoom, Minimum, Maximum) : Default;

    /// <summary>Whether two zooms are the same level.</summary>
    public static bool Same(double left, double right) => Math.Abs(left - right) <= Tolerance;

    /// <summary>The position of <paramref name="zoom"/> in <see cref="Levels"/>, or -1 when it is between two.</summary>
    public static int IndexOf(double zoom)
    {
        for (int i = 0; i < Levels.Count; i++)
        {
            if (Same(Levels[i], zoom))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The first level above <paramref name="zoom"/>, or the largest when it is
    /// already there. A zoom that sits between two levels steps to the one above
    /// it rather than snapping down first.
    /// </summary>
    public static double In(double zoom)
    {
        double current = Normalize(zoom);
        foreach (double level in Levels)
        {
            if (level > current + Tolerance)
                return level;
        }

        return Maximum;
    }

    /// <summary>The first level below <paramref name="zoom"/>, or the smallest when it is already there.</summary>
    public static double Out(double zoom)
    {
        double current = Normalize(zoom);
        for (int i = Levels.Count - 1; i >= 0; i--)
        {
            if (Levels[i] < current - Tolerance)
                return Levels[i];
        }

        return Minimum;
    }

    /// <summary>Applies a step to a zoom.</summary>
    public static double Apply(double zoom, WriterZoomStep step) => step switch
    {
        WriterZoomStep.In => In(zoom),
        WriterZoomStep.Out => Out(zoom),
        WriterZoomStep.Reset => Default,
        _ => Normalize(zoom),
    };

    /// <summary>How a zoom is written wherever the user sees it: a whole percentage.</summary>
    public static string Describe(double zoom) =>
        Math.Round(Normalize(zoom) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Reads back what <see cref="Describe"/> writes, and the same figure without
    /// its sign, so a percentage that came from a menu, a combo box or a typed
    /// setting all land on the same zoom.
    /// </summary>
    public static bool TryParse(string? text, out double zoom)
    {
        zoom = Default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) ||
            !double.IsFinite(percent) ||
            percent <= 0)
        {
            return false;
        }

        zoom = Normalize(percent / 100);
        return true;
    }

    /// <summary>
    /// The step a key press asks for, or <see cref="WriterZoomStep.None"/>. Ctrl
    /// with plus, minus and zero, matched by name and by native code alike: a
    /// head that names its keys (Linux, the browser) and one that only numbers
    /// them (Windows) both have to reach the same step.
    /// </summary>
    /// <remarks>
    /// A held key repeats on purpose - zooming several levels is one gesture, not
    /// one press per level - so unlike the Formatting Codes shortcuts this does
    /// not refuse a repeat.
    /// </remarks>
    public static WriterZoomStep StepFor(
        string? keyName,
        int nativeKeyCode,
        KeyboardModifierState modifiers,
        bool isDown)
    {
        if (!isDown ||
            (modifiers & KeyboardModifierState.Control) == KeyboardModifierState.None ||
            (modifiers & KeyboardModifierState.Alt) != KeyboardModifierState.None)
        {
            return WriterZoomStep.None;
        }

        if (IsKey(keyName, nativeKeyCode, OemPlus, "+", "=", "Equal", "Plus") ||
            IsKey(keyName, nativeKeyCode, NumpadAdd, "Add", "NumpadAdd"))
        {
            return WriterZoomStep.In;
        }

        if (IsKey(keyName, nativeKeyCode, OemMinus, "-", "_", "Minus") ||
            IsKey(keyName, nativeKeyCode, NumpadSubtract, "Subtract", "NumpadSubtract"))
        {
            return WriterZoomStep.Out;
        }

        if (IsKey(keyName, nativeKeyCode, Digit0, "0", "Digit0") ||
            IsKey(keyName, nativeKeyCode, Numpad0, "Numpad0"))
        {
            return WriterZoomStep.Reset;
        }

        return WriterZoomStep.None;
    }

    /// <summary>
    /// The step a wheel notch asks for while Ctrl is held, which is how a mouse
    /// zooms. <paramref name="controlHeld"/> is passed rather than read off the
    /// wheel event because not every head fills a pointer event's modifiers in.
    /// </summary>
    public static WriterZoomStep StepForWheel(bool controlHeld, double notches)
    {
        if (!controlHeld || !double.IsFinite(notches) || notches == 0)
            return WriterZoomStep.None;

        return notches > 0 ? WriterZoomStep.In : WriterZoomStep.Out;
    }

    // The Win32 virtual-key codes, which the browser reports as keyCode too.
    private const int OemPlus = 0xBB;
    private const int OemMinus = 0xBD;
    private const int NumpadAdd = 0x6B;
    private const int NumpadSubtract = 0x6D;
    private const int Digit0 = 0x30;
    private const int Numpad0 = 0x60;

    private static bool IsKey(string? keyName, int nativeKeyCode, int virtualKey, params string[] names)
    {
        if (nativeKeyCode == virtualKey)
            return true;

        if (string.IsNullOrEmpty(keyName))
            return false;

        if (string.Equals(
                keyName,
                "VirtualKey:" + virtualKey.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string name in names)
        {
            if (string.Equals(keyName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
