using System;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.UI.RichEdit;

namespace Broiler.Writer;

internal static class WriterCharacterFormatting
{
    internal static bool TryParseColor(string input, out BColor color)
    {
        color = BColor.Empty;
        ReadOnlySpan<char> value = input.AsSpan().Trim();
        if (!value.IsEmpty && value[0] == '#')
            value = value[1..];
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;

        color = new BColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static bool TryGetExternalLink(string target, out Uri? url)
    {
        url = null;
        if (target.Length > 2048 || target.Any(char.IsControl) ||
            !Uri.TryCreate(target, UriKind.Absolute, out Uri? candidate) ||
            candidate.Scheme is not ("http" or "https" or "mailto") || !candidate.IsWellFormedOriginalString())
            return false;

        url = candidate;
        return true;
    }

    internal static RichTextRange LinkRange(RichTextDocument document, RichTextRange selection)
    {
        if (!selection.IsEmpty)
            return selection;

        RichTextPosition caret = selection.Focus;
        RichTextPosition paragraphStart = document.ParagraphStart(caret);
        RichTextPosition paragraphEnd = document.ParagraphEnd(caret);
        string? target = document.InlineStyleAt(caret).LinkHref;
        if (string.IsNullOrEmpty(target))
            return selection;

        RichTextPosition start = caret;
        while (start != paragraphStart && document.InlineStyleAt(start).LinkHref == target)
            start = document.PositionLeftOf(start);
        RichTextPosition end = caret;
        while (end != paragraphEnd)
        {
            RichTextPosition next = document.PositionRightOf(end);
            if (document.InlineStyleAt(next).LinkHref != target)
                break;
            end = next;
        }
        return new RichTextRange(start, end);
    }

    internal static bool ApplyLink(UiRichEdit editor, RichTextRange range, string displayText, string target)
    {
        if (!editor.GetCommandState(RichEditCommand.InsertText).IsEnabled || !TryGetExternalLink(target, out _))
            return false;

        RichTextDocument document = editor.Document;
        RichTextDocument selected = document.Slice(range);
        if (!range.IsEmpty && displayText == selected.PlainText)
            return editor.ApplyInlineStyleRange(range, InlineStyleDelta.WithLink(target), editor.Selection);
        if (!CanReplaceLinkText(selected) || string.IsNullOrWhiteSpace(displayText) || displayText.Any(char.IsControl))
            return false;

        InlineStyle style = range.IsEmpty ? editor.CaretInlineStyle : selected.Paragraphs[0].StyleAt(0);
        RichTextDocument content = RichTextDocument.Empty.InsertText(RichTextDocument.Start, displayText,
            style with { LinkHref = target, Image = null, Note = null, PageField = null }).Document;
        editor.Selection = range;
        return editor.InsertDocument(content);
    }

    internal static bool CanReplaceLinkText(RichTextDocument selected) =>
        selected.ParagraphCount == 1 &&
        selected.Paragraphs[0].Runs.All(run => run.Style is { Image: null, Note: null, PageField: null });
}
