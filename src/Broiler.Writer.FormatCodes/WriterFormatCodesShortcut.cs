using Broiler.Input.Keyboard;

namespace Broiler.Writer.FormatCodes;

/// <summary>Shared keyboard policy for desktop and browser Formatting Codes hosts.</summary>
public static class WriterFormatCodesShortcut
{
    public static bool IsToggle(
        string keyName,
        KeyboardModifierState modifiers,
        bool isDown,
        bool isRepeat) =>
        isDown && !isRepeat &&
        string.Equals(keyName, "F3", System.StringComparison.OrdinalIgnoreCase) &&
        (modifiers & (KeyboardModifierState.Control | KeyboardModifierState.Shift)) ==
        (KeyboardModifierState.Control | KeyboardModifierState.Shift);

    public static bool IsFocusCycle(
        string keyName,
        KeyboardModifierState modifiers,
        bool isDown,
        bool isRepeat) =>
        isDown && !isRepeat &&
        string.Equals(keyName, "F6", System.StringComparison.OrdinalIgnoreCase) &&
        (modifiers & (KeyboardModifierState.Control | KeyboardModifierState.Alt)) ==
        KeyboardModifierState.None;

    public static bool IsReverseFocusCycle(KeyboardModifierState modifiers) =>
        (modifiers & KeyboardModifierState.Shift) != KeyboardModifierState.None;
}
