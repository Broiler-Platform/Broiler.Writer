using System;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Text;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Label;
using Broiler.UI.Label.Standard;
using Broiler.UI.Window;

namespace Broiler.Writer;

/// <summary>Coordinates document replacement with the same prompt on every Writer head.</summary>
public sealed class WriterDocumentChanges(
    UiWindow owner,
    Func<BSize> viewport,
    Func<bool> isModified,
    Func<string> documentName,
    Action<Action<bool>> save)
{
    private bool _pending;

    public void Run(Action action)
    {
        if (_pending || owner.IsDisposed || owner.IsClosed || owner.Session?.ModalElement is not null ||
            owner.Session?.IsBlockedByExternalModal == true)
            return;

        if (!isModified())
        {
            action();
            return;
        }

        _pending = true;
        bool finished = false;
        var dialog = new StandardDialog
        {
            Title = "Unsaved changes",
            PreferredSize = new BSize(460, 210),
        };
        var name = new StandardLabel
        {
            Text = documentName(),
            Font = new BFontStyle("Segoe UI", 15, BFontWeight.SemiBold),
            Trimming = UiTextTrimming.CharacterEllipsis,
        };
        var message = new StandardLabel
        {
            Text = "Save changes before continuing?",
            Font = new BFontStyle("Segoe UI", 14),
        };
        var detail = new StandardLabel
        {
            Text = "Discard will lose your unsaved changes.",
            Font = new BFontStyle("Segoe UI", 13),
        };
        var saveButton = new StandardButton { Text = "Save" };
        var discardButton = new StandardButton { Text = "Discard" };
        var cancelButton = new StandardButton { Text = "Cancel" };
        saveButton.Clicked += (_, _) => dialog.Accept();
        discardButton.Clicked += (_, _) => dialog.Reject();
        cancelButton.Clicked += (_, _) => dialog.Cancel();
        dialog.AddChild(new PromptContent(name, message, detail, saveButton, discardButton, cancelButton));
        dialog.ResultCompleted += (_, e) =>
        {
            if (owner.IsDisposed || owner.IsClosed)
            {
                _pending = false;
                return;
            }

            if (e.Result.Kind == UiDialogResultKind.Accepted)
            {
                // A cancelled picker or failed write must never complete the pending action.
                save(success => Finish(success && !isModified()));
            }
            else
                Finish(e.Result.Kind == UiDialogResultKind.Rejected);
        };

        BSize size = viewport();
        double width = Math.Min(460, Math.Max(0, size.Width - 24));
        double height = Math.Min(210, Math.Max(0, size.Height - 24));
        dialog.ShowModal(owner, new BRect((size.Width - width) / 2, (size.Height - height) / 2, width, height));

        void Finish(bool proceed)
        {
            if (finished)
                return;
            finished = true;
            _pending = false;
            if (proceed && !owner.IsDisposed && !owner.IsClosed)
                action();
        }
    }

    private sealed class PromptContent : UiElement
    {
        private readonly UiElement[] _elements;

        public PromptContent(params UiElement[] elements)
        {
            _elements = elements;
            foreach (UiElement element in elements)
                AddChild(element);
        }

        protected override BSize MeasureCore(BSize availableSize)
        {
            foreach (UiElement element in _elements)
                element.Measure(availableSize);
            return availableSize;
        }

        protected override void ArrangeCore(BRect bounds)
        {
            for (int i = 0; i < 3; i++)
                _elements[i].Arrange(new BRect(bounds.Left, bounds.Top + i * 28, bounds.Width, 26));

            double gap = 8;
            double width = Math.Max(0, Math.Min(96, (bounds.Width - gap * 2) / 3));
            double left = bounds.Right - width * 3 - gap * 2;
            for (int i = 0; i < 3; i++)
                _elements[i + 3].Arrange(new BRect(left + i * (width + gap), bounds.Bottom - 32, width, 32));
        }
    }
}
