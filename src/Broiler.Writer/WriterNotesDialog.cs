using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Broiler.Documents;
using Broiler.Graphics;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

/// <summary>
/// Shows what a read reported. The status bar can say a document opened with
/// notes, but not what they were, and the only other way to see them was to set
/// BROILER_WRITER_DOCUMENT_LOG and read stderr - a switch for chasing one bad
/// file, not an answer for someone who has just been told their letter opened
/// with two notes against it.
/// </summary>
internal static class WriterNotes
{
    /// <summary>
    /// A dialog listing <paramref name="diagnostics"/>. StandardDialog is sealed
    /// and arranges every child across its whole client area, so the content is
    /// one element that lays itself out rather than a subclass.
    /// </summary>
    public static StandardDialog CreateDialog(string fileName, IReadOnlyList<DocumentDiagnostic> diagnostics)
    {
        var dialog = new StandardDialog
        {
            Title = "Notes for " + fileName,
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
        text.SetPlainText(Describe(diagnostics));

        var close = new StandardButton { Text = "Close" };
        close.Clicked += (_, _) => dialog.Accept();

        dialog.AddChild(new NotesContent(text, close));
        return dialog;
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
        private const double ButtonWidth = 88;
        private const double Gap = 10;

        private readonly StandardRichEdit _text;
        private readonly StandardButton _close;

        public NotesContent(StandardRichEdit text, StandardButton close)
        {
            _text = text;
            _close = close;
            AddChild(_text);
            AddChild(_close);
        }

        protected override BSize MeasureCore(BSize availableSize)
        {
            _text.Measure(new BSize(availableSize.Width, Math.Max(0, availableSize.Height - ButtonHeight - Gap)));
            _close.Measure(new BSize(ButtonWidth, ButtonHeight));
            return availableSize;
        }

        protected override void ArrangeCore(BRect finalRect)
        {
            double textHeight = Math.Max(0, finalRect.Height - ButtonHeight - Gap);
            _text.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, textHeight));
            _close.Arrange(new BRect(
                finalRect.Left + Math.Max(0, finalRect.Width - ButtonWidth),
                finalRect.Top + textHeight + Gap,
                ButtonWidth,
                ButtonHeight));
        }
    }
}
