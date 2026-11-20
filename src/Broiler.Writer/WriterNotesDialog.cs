using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Broiler.Documents;
using Broiler.Documents.Model;
using Broiler.Graphics;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Text;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

/// <summary>
/// Presents file-format diagnostics, save review and editing-view notes.
/// </summary>
internal static class WriterNotes
{
    /// <summary>
    /// A dialog listing <paramref name="diagnostics"/>. StandardDialog is sealed
    /// and arranges every child across its whole client area, so the content is
    /// one element that lays itself out rather than a subclass.
    /// </summary>
    public static StandardDialog CreateDialog(string fileName, IReadOnlyList<DocumentDiagnostic> diagnostics, bool reviewSave = false)
    {
        string title = (reviewSave ? "Review format changes for " : "Notes for ") + fileName;
        string description = reviewSave
            ? "Some document content or formatting may change in this file."
                + (diagnostics.Count == 0 ? string.Empty : "\n\n" + Describe(diagnostics))
            : Describe(diagnostics);
        return CreateDialog(title, description, reviewSave);
    }

    internal static StandardDialog CreateViewDialog(RichTextDocument document, bool showsHiddenText = true, bool isPaginated = true) =>
        CreateDialog("Editing view notes", DescribeView(document, showsHiddenText, isPaginated), reviewSave: false);

    internal static string DescribeView(RichTextDocument document, bool showsHiddenText = true, bool isPaginated = true)
    {
        IReadOnlyList<DocumentDiagnostic> notes = WriterViewDiagnostics.Collect(document, showsHiddenText, isPaginated);
        return notes.Count == 0 ? WriterViewDiagnostics.EmptyMessage : Describe(notes);
    }

    private static StandardDialog CreateDialog(string title, string description, bool reviewSave)
    {
        var dialog = new StandardDialog
        {
            Title = title,
            PreferredSize = new BSize(560, 320),
            TitleFont = new BFontStyle("Segoe UI", 14, BFontWeight.SemiBold),
        };

        // Read-only rather than a label so the list scrolls when a document
        // reports more notes than fit, and so a code can be selected and copied
        // into a bug report.
        var text = new StandardRichEdit
        {
            IsReadOnly = true,
            Font = new BFontStyle("Segoe UI", 13),
        };
        text.SetPlainText(description);
        if (reviewSave)
        {
            dialog.AddChild(new NotesContent(text,
                [CreateButton("Save anyway", () => dialog.Accept()),
                 CreateButton("Save as...", () => dialog.Reject()),
                 CreateButton("Cancel", () => dialog.Cancel())]));
        }
        else
        {
            dialog.AddChild(new NotesContent(text, [CreateButton("Close", () => dialog.Accept())]));
        }
        return dialog;
    }

    private static StandardButton CreateButton(string label, Action action)
    {
        var button = new StandardButton { Text = label };
        button.Clicked += (_, _) => action();
        return button;
    }

    /// <summary>
    /// One entry per note: severity, code, then the message. The code is included
    /// because it is the part worth searching for or quoting - the message says
    /// what happened, the code says which rule produced it.
    /// </summary>
    internal static string Describe(IReadOnlyList<DocumentDiagnostic> diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
            return "This document opened with nothing to report.";

        var builder = new StringBuilder();
        builder.Append(diagnostics.Count.ToString(CultureInfo.InvariantCulture))
            .Append(diagnostics.Count == 1 ? " note:" : " notes:");

        foreach (DocumentDiagnostic diagnostic in diagnostics)
        {
            builder.Append(NL).Append(NL)
                .Append(diagnostic.Severity.ToString().ToLowerInvariant())
                .Append("  ")
                .Append(diagnostic.Code)
                .Append(NL)
                .Append("    ")
                .Append(diagnostic.Message);
        }

        return builder.ToString();
    }

    private const string NL = "\n";

    /// <summary>The dialog's body: the note list above, a Close button below it.</summary>
    private sealed class NotesContent : UiElement
    {
        private const double ButtonHeight = 30;
        private const double ButtonWidth = 112;
        private const double Gap = 10;

        private readonly StandardRichEdit _text;
        private readonly StandardButton[] _buttons;

        public NotesContent(StandardRichEdit text, StandardButton[] buttons)
        {
            _text = text;
            _buttons = buttons;
            AddChild(_text);
            foreach (StandardButton button in buttons)
                AddChild(button);
        }

        protected override BSize MeasureCore(BSize availableSize)
        {
            _text.Measure(new BSize(availableSize.Width, Math.Max(0, availableSize.Height - ButtonHeight - Gap)));
            foreach (StandardButton button in _buttons)
                button.Measure(new BSize(ButtonWidth, ButtonHeight));
            return availableSize;
        }

        protected override void ArrangeCore(BRect finalRect)
        {
            double textHeight = Math.Max(0, finalRect.Height - ButtonHeight - Gap);
            _text.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, textHeight));
            double gap = Math.Min(Gap, finalRect.Width / _buttons.Length);
            double width = Math.Min(ButtonWidth, (finalRect.Width - gap * (_buttons.Length - 1)) / _buttons.Length);
            double left = finalRect.Right - width * _buttons.Length - gap * (_buttons.Length - 1);
            foreach (StandardButton button in _buttons)
            {
                button.Arrange(new BRect(left, finalRect.Top + textHeight + Gap, width, ButtonHeight));
                left += width + gap;
            }
        }
    }
}
