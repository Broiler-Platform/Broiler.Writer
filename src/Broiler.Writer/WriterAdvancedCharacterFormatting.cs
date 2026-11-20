using System;
using System.Globalization;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.UI.Menu;
using Broiler.UI.Menu.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;

namespace Broiler.Writer;

internal static class WriterAdvancedCharacterFormatting
{
    internal static void AddFormattingItems(StandardCommandDispatcher dispatcher, UiMenuItem format,
        StandardRichEdit editor, Action refresh, Action showLanguage)
    {
        AddStyle("superscript", "Superscript", IsSuperscript,
            on => InlineStyleDelta.WithBaselineShift(on ? BaselineShift.Superscript() : null));
        AddStyle("subscript", "Subscript", IsSubscript,
            on => InlineStyleDelta.WithBaselineShift(on ? BaselineShift.Subscript() : null));
        AddStyle("hidden-text", "Hidden text", style => style.Hidden, InlineStyleDelta.WithHidden);
        dispatcher.Add(new StandardCommand("format.language", showLanguage, CanEdit));
        format.Children.Add(new UiMenuItem("language", "Text language...") { CommandName = "format.language" });

        bool CanEdit() => editor.GetCommandState(RichEditCommand.InsertText).IsEnabled;
        void AddStyle(string id, string title, Func<InlineStyle, bool> hasStyle, Func<bool, InlineStyleDelta> delta)
        {
            string name = "format." + id;
            var item = new UiMenuItem(id, title) { CommandName = name, IsCheckable = true };
            void Update() => item.IsChecked = HasStyle(editor, hasStyle);
            editor.DocumentChanged += (_, _) => Update();
            editor.SelectionChanged += (_, _) => Update();
            dispatcher.Add(new StandardCommand(name, () =>
            {
                editor.ApplyInlineStyleRange(editor.Selection, delta(!HasStyle(editor, hasStyle)));
                Update();
                refresh();
            }, CanEdit));
            format.Children.Add(item);
            Update();
        }
    }

    internal static void SyncInvokedItem(UiMenuItem item, StandardRichEdit editor)
    {
        item.IsChecked = item.CommandName switch
        {
            "format.superscript" => HasStyle(editor, IsSuperscript),
            "format.subscript" => HasStyle(editor, IsSubscript),
            "format.hidden-text" => HasStyle(editor, style => style.Hidden),
            "view.hidden-text" => editor.ShowHiddenText,
            _ => item.IsChecked,
        };
    }

    private static bool IsSuperscript(InlineStyle style) => style.Shift?.Position == BaselinePosition.AutoSuperscript;
    private static bool IsSubscript(InlineStyle style) => style.Shift?.Position == BaselinePosition.AutoSubscript;

    private static bool HasStyle(StandardRichEdit editor, Func<InlineStyle, bool> hasStyle)
    {
        RichTextRange selection = editor.Selection;
        if (selection.IsEmpty)
            return hasStyle(editor.CaretInlineStyle);

        RichTextDocument document = editor.Document;
        if (selection.Start != RichTextDocument.Start || selection.End != document.End)
            document = document.Slice(selection);

        bool hasText = false;
        foreach (RichTextParagraph paragraph in document.Paragraphs)
        {
            foreach (StyleRun run in paragraph.Runs)
            {
                if (!hasStyle(run.Style))
                    return false;
                hasText = true;
            }
        }
        return hasText;
    }

    internal static void AddViewItem(StandardCommandDispatcher dispatcher, UiMenuItem view, StandardRichEdit editor, Action refresh)
    {
        const string command = "view.hidden-text";
        var item = new UiMenuItem("show-hidden-text", "Show hidden text")
        {
            CommandName = command,
            IsCheckable = true,
            IsChecked = editor.ShowHiddenText,
        };
        dispatcher.Add(new StandardCommand(command, () =>
        {
            editor.ShowHiddenText = !editor.ShowHiddenText;
            item.IsChecked = editor.ShowHiddenText;
            refresh();
        }));
        view.Children.Add(item);
    }

    internal static bool IsLanguageTag(string tag)
    {
        if (tag.Length is < 2 or > 63 || !tag.Split('-').All(part => part.Length is >= 1 and <= 8 && part.All(char.IsAsciiLetterOrDigit)))
            return false;
        try
        {
            return CultureInfo.GetCultureInfo(tag).Name.Length != 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
