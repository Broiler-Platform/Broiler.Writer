using Broiler.Documents.Model;
using Broiler.UI.RichEdit;

namespace Broiler.Writer;

internal static class WriterCapitalization
{
    internal static RichEditCommandState GetState(UiRichEdit editor, RichEditCommand command)
    {
        RichEditCommandState state = editor.GetCommandState(command);
        TextCapitalization kind = KindOf(command);
        return kind == TextCapitalization.None || editor.Selection.IsEmpty
            ? state
            : RichEditCommandState.For(state.IsEnabled, SelectionHasCapitalization(editor.Document, editor.Selection, kind));
    }

    internal static bool Execute(UiRichEdit editor, RichEditCommand command)
    {
        TextCapitalization kind = KindOf(command);
        if (kind == TextCapitalization.None)
            return editor.ExecuteCommand(command);

        RichEditCommandState state = GetState(editor, command);
        if (!state.IsEnabled)
            return false;

        if (editor.Selection.IsEmpty)
        {
            // RichEdit sets a typing style without changing the document.
            editor.ExecuteCommand(command);
            return true;
        }

        var delta = InlineStyleDelta.WithCapitalization(state.IsToggled ? TextCapitalization.None : kind);
        return editor.ApplyInlineStyleRange(editor.Selection, delta);
    }

    private static TextCapitalization KindOf(RichEditCommand command) => command switch
    {
        RichEditCommand.AllCaps => TextCapitalization.AllCaps,
        RichEditCommand.SmallCaps => TextCapitalization.SmallCaps,
        _ => TextCapitalization.None,
    };

    private static bool SelectionHasCapitalization(RichTextDocument document, RichTextRange selection, TextCapitalization kind)
    {
        RichTextDocument selected = selection.Start == RichTextDocument.Start && selection.End == document.End
            ? document
            : document.Slice(selection);
        bool foundText = false;
        foreach (RichTextParagraph paragraph in selected.Paragraphs)
        {
            foreach (StyleRun run in paragraph.Runs)
            {
                if (run.Style.Capitalization != kind)
                    return false;
                foundText = true;
            }
        }
        return foundText;
    }
}
