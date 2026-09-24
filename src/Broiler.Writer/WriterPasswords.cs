using System;
using Broiler.Documents;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Text;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Dialog;
using Broiler.UI.Dialog.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Label;
using Broiler.UI.Label.Standard;

namespace Broiler.Writer;

/// <summary>Why a read needs a password before it can open a document.</summary>
public enum WriterPasswordReason
{
    /// <summary>It does not: the read stands as it is.</summary>
    None,

    /// <summary>The document is protected by a password, and the read gave none.</summary>
    Required,

    /// <summary>The password the read gave did not open it.</summary>
    Incorrect,

    /// <summary>
    /// The document opened, and its permissions keep its content from being
    /// copied - which is what opening it in the Writer does - without its owner's
    /// password.
    /// </summary>
    OwnerRequired,
}

/// <summary>
/// How a composition root lets the Writer open one format's password-protected
/// documents: which of the format's diagnostics mean a password would help, and
/// how a password reaches its reader.
/// </summary>
/// <remarks>
/// <para>
/// The core cannot say either itself. PDF is the format with passwords today, and
/// the desktop heads register it; the core references no PDF package, so that
/// the codec cannot reach Android or WebAssembly as someone else's transitive
/// reference (PDF roadmap §10.1). A head that registers a format able to open
/// protected documents says how here, and the core asks for the password,
/// reads again, and asks again while the answer is wrong.
/// </para>
/// <para>
/// A format with none composed is read once, as before: a document that needs a
/// password is refused with the codec's own reason.
/// </para>
/// </remarks>
public sealed class WriterPasswordSupport
{
    private readonly string _required;
    private readonly string _incorrect;
    private readonly string _ownerRequired;
    private readonly Func<DocumentReadOptions, string, DocumentReadOptions> _withPassword;

    /// <param name="passwordRequiredCode">The code of a read refused because it gave no password.</param>
    /// <param name="passwordIncorrectCode">The code of a read refused because the password was wrong.</param>
    /// <param name="ownerPasswordRequiredCode">
    /// The code of a read refused because the document's permissions withhold
    /// copying, which its owner's password lifts.
    /// </param>
    /// <param name="withPassword">
    /// Read options that open the same document with a password, built from the
    /// options the Writer reads with.
    /// </param>
    public WriterPasswordSupport(
        string passwordRequiredCode,
        string passwordIncorrectCode,
        string ownerPasswordRequiredCode,
        Func<DocumentReadOptions, string, DocumentReadOptions> withPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordRequiredCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordIncorrectCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPasswordRequiredCode);
        ArgumentNullException.ThrowIfNull(withPassword);

        _required = passwordRequiredCode;
        _incorrect = passwordIncorrectCode;
        _ownerRequired = ownerPasswordRequiredCode;
        _withPassword = withPassword;
    }

    /// <summary>
    /// Why <paramref name="result"/> needs a password, or
    /// <see cref="WriterPasswordReason.None"/> when a password would not change it.
    /// Only a refused read can need one.
    /// </summary>
    public WriterPasswordReason ReasonFor(DocumentReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != DocumentResultStatus.Rejected)
            return WriterPasswordReason.None;

        foreach (DocumentDiagnostic diagnostic in result.Diagnostics)
        {
            if (string.Equals(diagnostic.Code, _required, StringComparison.Ordinal))
                return WriterPasswordReason.Required;
            if (string.Equals(diagnostic.Code, _incorrect, StringComparison.Ordinal))
                return WriterPasswordReason.Incorrect;
            if (string.Equals(diagnostic.Code, _ownerRequired, StringComparison.Ordinal))
                return WriterPasswordReason.OwnerRequired;
        }

        return WriterPasswordReason.None;
    }

    /// <summary>The options that read the same document again with <paramref name="password"/>.</summary>
    public DocumentReadOptions WithPassword(DocumentReadOptions options, string password)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(password);
        return _withPassword(options, password)
            ?? throw new InvalidOperationException("The composed password support returned no read options.");
    }
}

/// <summary>
/// The dialog that asks for a document's password, and the one answer it gives.
/// </summary>
/// <remarks>
/// The password lives in the masked field until the dialog is answered; the
/// Writer hands it to the reader once and keeps no copy. A test answers through
/// <see cref="Submit"/> and <see cref="Cancel"/>, which are what the dialog's
/// own buttons, Enter and Escape end in.
/// </remarks>
internal sealed class WriterPasswordPrompt : IDisposable
{
    private readonly Action<string> _submit;
    private readonly Action _cancel;
    private readonly StandardEdit _password;
    private bool _answered;

    public WriterPasswordPrompt(string fileName, WriterPasswordReason reason, Action<string> submit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        FileName = fileName;
        Reason = reason;
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));

        Dialog = new StandardDialog
        {
            Title = "Password for " + fileName,
            PreferredSize = PreferredSize,
            TitleFont = new BFontStyle("Segoe UI", 14, BFontWeight.SemiBold),
        };

        var message = new StandardLabel
        {
            Text = Describe(reason, fileName),
            Font = new BFontStyle("Segoe UI", 13),
            Wrapping = UiTextWrapping.Wrap,
        };

        _password = new StandardEdit
        {
            IsPassword = true,
            PlaceholderText = reason == WriterPasswordReason.OwnerRequired ? "Owner password" : "Password",
            Font = new BFontStyle("Segoe UI", 13),
        };

        var open = new StandardButton { Text = "Open" };
        var cancelButton = new StandardButton { Text = "Cancel" };
        open.Clicked += (_, _) => Dialog.Accept();
        cancelButton.Clicked += (_, _) => Dialog.Cancel();
        _password.Submitted += (_, _) => Dialog.Accept();

        Dialog.ResultCompleted += (_, e) =>
        {
            if (e.Result.Kind == UiDialogResultKind.Accepted)
                Submit(_password.Text);
            else
                Cancel();
        };

        Dialog.AddChild(new PromptContent(message, _password, open, cancelButton));
    }

    /// <summary>The size the dialog asks for.</summary>
    public static BSize PreferredSize { get; } = new(460, 190);

    public string FileName { get; }

    public WriterPasswordReason Reason { get; }

    public StandardDialog Dialog { get; }

    /// <summary>The masked field, for a test that types into it as a user would.</summary>
    internal StandardEdit PasswordField => _password;

    /// <summary>Puts the caret in the password field.</summary>
    public void Focus() => _password.Session?.SetFocus(_password);

    /// <summary>Answers with <paramref name="password"/>. Only the first answer counts.</summary>
    public void Submit(string password)
    {
        if (!Answer())
            return;

        _submit(password ?? string.Empty);
    }

    /// <summary>Answers with no password. Only the first answer counts.</summary>
    public void Cancel()
    {
        if (!Answer())
            return;

        _cancel();
    }

    public void Dispose() => Dialog.Dispose();

    /// <summary>What the dialog says, for each reason a password is asked for.</summary>
    internal static string Describe(WriterPasswordReason reason, string fileName) => reason switch
    {
        WriterPasswordReason.Incorrect =>
            "That password did not open " + fileName + ". Enter it again, or cancel to keep the open document.",
        WriterPasswordReason.OwnerRequired =>
            fileName + " does not allow its content to be copied, and opening it here copies it. Its owner password lifts that restriction.",
        _ => fileName + " is protected by a password. Enter it to open the document.",
    };

    // Marks the prompt answered, clears the field, and closes the dialog when the
    // answer came from outside it. False when it was already answered. The field
    // is cleared first: closing a dialog disposes what it holds, and a disposed
    // field takes no more text - which is also why an answer from the dialog
    // itself, arriving after it closed, leaves the field alone.
    private bool Answer()
    {
        if (_answered)
            return false;

        _answered = true;
        if (!_password.IsDisposed)
            _password.Text = string.Empty;

        if (Dialog.IsPresented && !Dialog.IsResultCompleted)
            Dialog.Cancel();

        return true;
    }

    /// <summary>The message above, the field below it, and the buttons at the bottom right.</summary>
    private sealed class PromptContent : UiElement
    {
        private const double FieldHeight = 30;
        private const double ButtonHeight = 30;
        private const double ButtonWidth = 88;
        private const double Gap = 10;

        private readonly StandardLabel _message;
        private readonly StandardEdit _field;
        private readonly StandardButton _open;
        private readonly StandardButton _cancel;

        public PromptContent(StandardLabel message, StandardEdit field, StandardButton open, StandardButton cancel)
        {
            _message = message;
            _field = field;
            _open = open;
            _cancel = cancel;
            AddChild(_message);
            AddChild(_field);
            AddChild(_open);
            AddChild(_cancel);
        }

        protected override BSize MeasureCore(BSize availableSize)
        {
            _message.Measure(new BSize(availableSize.Width, MessageHeight(availableSize.Height)));
            _field.Measure(new BSize(availableSize.Width, FieldHeight));
            _open.Measure(new BSize(ButtonWidth, ButtonHeight));
            _cancel.Measure(new BSize(ButtonWidth, ButtonHeight));
            return availableSize;
        }

        protected override void ArrangeCore(BRect finalRect)
        {
            double messageHeight = MessageHeight(finalRect.Height);
            _message.Arrange(new BRect(finalRect.Left, finalRect.Top, finalRect.Width, messageHeight));
            _field.Arrange(new BRect(finalRect.Left, finalRect.Top + messageHeight + Gap, finalRect.Width, FieldHeight));

            double buttonTop = finalRect.Top + Math.Max(0, finalRect.Height - ButtonHeight);
            double cancelLeft = finalRect.Left + Math.Max(0, finalRect.Width - ButtonWidth);
            _cancel.Arrange(new BRect(cancelLeft, buttonTop, ButtonWidth, ButtonHeight));
            _open.Arrange(new BRect(Math.Max(finalRect.Left, cancelLeft - Gap - ButtonWidth), buttonTop, ButtonWidth, ButtonHeight));
        }

        private static double MessageHeight(double available) =>
            Math.Max(0, available - FieldHeight - ButtonHeight - (2 * Gap));
    }
}
