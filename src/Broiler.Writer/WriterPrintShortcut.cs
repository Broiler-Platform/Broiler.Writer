using System;
using System.Globalization;
using Broiler.Input.Keyboard;

namespace Broiler.Writer;

/// <summary>Ctrl+P, the key every editor prints with.</summary>
/// <remarks>
/// Broiler.UI has no key-gesture binding yet, so the Writer reads its own shortcuts, as it does
/// the zoom and Formatting Codes ones. Shift or Alt with it is another chord and is left alone.
/// </remarks>
internal static class WriterPrintShortcut
{
    private const int VirtualKeyP = 0x50;

    public static bool IsPrint(string? keyName, int nativeKeyCode, KeyboardModifierState modifiers, bool isDown)
    {
        const KeyboardModifierState chord = KeyboardModifierState.Control | KeyboardModifierState.Shift | KeyboardModifierState.Alt;
        if (!isDown || (modifiers & chord) != KeyboardModifierState.Control)
            return false;

        return nativeKeyCode == VirtualKeyP ||
            string.Equals(keyName, "P", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(keyName, "KeyP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(keyName, "VirtualKey:" + VirtualKeyP.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
