using System;

namespace Broiler.Writer.FormatCodes;

/// <summary>Shared desktop/browser layout policy for the editor and Formatting Codes pane.</summary>
public static class WriterFormatCodesLayout
{
    public const double SplitterThickness = 8;
    public const double MinimumEditorHeight = 120;
    public const double MinimumPaneHeight = 88;

    public static WriterFormatCodesLayoutResult Calculate(
        double workspaceHeight,
        double editorFraction,
        bool paneVisible)
    {
        workspaceHeight = Math.Max(0, workspaceHeight);
        if (!paneVisible || workspaceHeight < SplitterThickness + MinimumPaneHeight)
            return new WriterFormatCodesLayoutResult(workspaceHeight, 0, 0);

        double available = Math.Max(0, workspaceHeight - SplitterThickness);
        double editorMinimum = Math.Min(MinimumEditorHeight, available);
        double paneMinimum = Math.Min(MinimumPaneHeight, Math.Max(0, available - editorMinimum));
        double maximumEditor = Math.Max(editorMinimum, available - paneMinimum);
        double editor = Math.Clamp(available * editorFraction, editorMinimum, maximumEditor);
        return new WriterFormatCodesLayoutResult(editor, SplitterThickness, available - editor);
    }
}

public readonly record struct WriterFormatCodesLayoutResult(
    double EditorHeight,
    double SplitterHeight,
    double PaneHeight);
