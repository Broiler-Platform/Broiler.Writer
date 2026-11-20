using System;
using System.ComponentModel;
using System.Linq;
using Broiler.Documents.Model;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ColorDialog.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.Window;

namespace Broiler.Writer;

internal static partial class WriterFormattingDialog
{
    internal static void ShowLanguage(UiWindow owner, BSize viewport, UiRichEdit editor, Action refresh)
    {
        RichTextDocument document = editor.Document;
        RichTextRange selection = editor.Selection;
        string?[] languages = selection.IsEmpty
            ? [editor.CaretInlineStyle.Language]
            : document.Slice(selection).Paragraphs.SelectMany(p => p.Runs).Select(r => r.Style.Language).Distinct().Take(2).ToArray();
        var input = new StandardEdit
        {
            Text = languages.Length == 1 ? languages[0] ?? "" : "",
            PlaceholderText = languages.Length > 1 ? "Mixed languages" : "en-US",
        };
        StandardDialog dialog = Create("Text language", 170);
        var message = new StandardLabel { Text = "Enter a language tag such as en-US or tr." };
        void Apply(string? language)
        {
            if (ReferenceEquals(document, editor.Document) && selection == editor.Selection)
            {
                editor.ApplyInlineStyleRange(selection, InlineStyleDelta.WithLanguage(language));
                refresh();
            }
            dialog.Accept();
        }
        void Submit()
        {
            string language = input.Text.Trim();
            if (WriterAdvancedCharacterFormatting.IsLanguageTag(language))
                Apply(language);
            else
                message.Text = "Enter a language tag such as en-US or tr.";
        }
        input.Submitted += (_, _) => Submit();
        dialog.AddChild(new StandardDialogFields([message, input], [Button("Apply", Submit), Button("Clear language", () => Apply(null)), Button("Cancel", () => dialog.Cancel())]));
        Show(dialog, owner, viewport, input);
    }

    internal static void ShowColor(UiWindow owner, BSize viewport, UiRichEdit editor, RichEditCommand command, Action refresh)
    {
        bool highlight = command == RichEditCommand.SetBackground;
        RichTextRange selection = editor.Selection;
        RichTextDocument document = editor.Document;
        BColor[] colors = selection.IsEmpty
            ? [ColorOf(editor.CaretInlineStyle)]
            : document.Slice(selection).Paragraphs.SelectMany(p => p.Runs).Select(r => ColorOf(r.Style)).Distinct().Take(2).ToArray();
        var dialog = new StandardColorDialog
        {
            Title = highlight ? "Highlight" : "Text color",
            CanClose = !OperatingSystem.IsBrowser(),
            SelectedColor = colors.Length == 1 && !colors[0].IsEmpty
                ? colors[0]
                : highlight ? new BColor(255, 255, 0) : BColor.Black,
        };
        dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind != UiDialogResultKind.Accepted ||
                !ReferenceEquals(document, editor.Document) || selection != editor.Selection)
                return;

            BColor color = dialog.SelectedColor;
            editor.ExecuteCommand(command, color.A == 0 ? BColor.Empty : color);
            refresh();
        };

        double width = Math.Min(dialog.PreferredSize.Width, Math.Max(1, viewport.Width - 24));
        double height = Math.Min(dialog.PreferredSize.Height, Math.Max(1, viewport.Height - 24));
        dialog.ShowColorModal(owner, new BRect((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height));

        BColor ColorOf(InlineStyle style) => highlight ? style.Background : style.Foreground;
    }

    internal static void ShowLink(UiWindow owner, BSize viewport, UiRichEdit editor, Action<Uri> openUrl)
    {
        RichTextDocument document = editor.Document;
        RichTextRange selection = editor.Selection;
        RichTextRange range = WriterCharacterFormatting.LinkRange(document, selection);
        RichTextDocument selected = document.Slice(range);
        string[] targets = selected.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Style.LinkHref ?? "").Distinct().Take(2).ToArray();
        var text = new StandardEdit
        {
            Text = selected.PlainText,
            PlaceholderText = "Display text",
            IsReadOnly = !WriterCharacterFormatting.CanReplaceLinkText(selected),
        };
        var target = new StandardEdit
        {
            Text = targets.Length == 1 ? targets[0] : "",
            PlaceholderText = targets.Length > 1 ? "Mixed link targets" : "https://example.com",
        };
        var message = new StandardLabel { Text = targets.Length > 1 ? "The selection has mixed link formatting." : "Enter an HTTP, HTTPS or mailto address." };
        StandardDialog dialog = Create("Link", 255);
        bool IsCurrent() => ReferenceEquals(document, editor.Document) && selection == editor.Selection;
        void Submit()
        {
            if (!IsCurrent())
            {
                message.Text = "The document or selection changed. Reopen the link editor.";
                return;
            }
            if (WriterCharacterFormatting.ApplyLink(editor, range, text.Text, target.Text.Trim()))
                dialog.Accept();
            else
                message.Text = "Enter display text and a valid HTTP, HTTPS or mailto address.";
        }
        void Remove()
        {
            if (IsCurrent())
                editor.ApplyInlineStyleRange(range, InlineStyleDelta.WithLink(null), selection);
            dialog.Accept();
        }
        var open = Button("Open link", () =>
        {
            if (!WriterCharacterFormatting.TryGetExternalLink(targets[0], out Uri? url))
                return;
            try
            {
                openUrl(url!);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                message.Text = "This host could not open the link.";
            }
        });
        open.IsEnabled = targets.Length == 1 && WriterCharacterFormatting.TryGetExternalLink(targets[0], out _);
        var remove = Button("Remove link", Remove);
        remove.IsEnabled = selected.Paragraphs.Any(p => p.Runs.Any(r => !string.IsNullOrEmpty(r.Style.LinkHref)));
        text.Submitted += (_, _) => Submit();
        target.Submitted += (_, _) => Submit();
        dialog.AddChild(new StandardDialogFields([new StandardLabel { Text = "Display text" }, text,
            new StandardLabel { Text = "Target address" }, target, message],
            [Button("Apply", Submit), remove, open, Button("Cancel", () => dialog.Cancel())]));
        Show(dialog, owner, viewport, range.IsEmpty ? text : target);
    }

    private static StandardDialog Create(string title, double height) =>
        new() { Title = title, PreferredSize = new BSize(560, height), CanClose = !OperatingSystem.IsBrowser() };

    private static void Show(StandardDialog dialog, UiWindow owner, BSize viewport, UiElement focus)
    {
        dialog.ShowModal(owner, StandardDialogPlacement.Center(viewport, dialog.PreferredSize));
        focus.Session?.SetFocus(focus);
    }

    private static StandardButton Button(string text, Action action)
    {
        var button = new StandardButton { Text = text };
        button.Clicked += (_, _) => action();
        return button;
    }

}
