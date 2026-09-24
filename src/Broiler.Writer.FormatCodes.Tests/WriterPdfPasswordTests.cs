using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Broiler.Documents;
using Broiler.Documents.Pdf;
using Broiler.Documents.Resources;

namespace Broiler.Writer.FormatCodes.Tests;

/// <summary>
/// Opening a password-protected PDF in the Writer: the codec refuses the first
/// read, the Writer asks for the password, reads again with it, asks again
/// while it is wrong, and leaves the open document alone when the user cancels.
/// </summary>
/// <remarks>
/// The documents are encrypted here, with RC4-128 at revision 3 - the smallest
/// revision that carries everything the Writer does with a password. This suite
/// cannot reach the producer the Documents suite tests the codec with, and does
/// not need to: what is under test is the Writer's flow and the heads' wiring,
/// and the codec's own cover lives with the codec.
/// </remarks>
public sealed class WriterPdfPasswordTests
{
    private const string Body = "Quarterly figures";
    private const int AllPermissions = -4;
    private const int NoCopying = -4 & ~(1 << 4);

    [Fact(Timeout = 600000)]
    public void A_Password_Protected_Pdf_Asks_For_Its_Password()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        string before = app.Document.PlainText;

        Assert.False(Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf"));

        WriterPasswordPrompt prompt = Assert.IsType<WriterPasswordPrompt>(app.PasswordPrompt);
        Assert.Equal(WriterPasswordReason.Required, prompt.Reason);
        Assert.Equal("locked.pdf", prompt.FileName);
        Assert.Equal("Password for locked.pdf", prompt.Dialog.Title);
        Assert.Equal(before, app.Document.PlainText);
        Assert.Equal("Password needed to open locked.pdf", app.LastAction);
    }

    [Fact(Timeout = 600000)]
    public void The_Right_Password_Opens_It()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf");

        app.PasswordPrompt!.Submit("user-pw");

        Assert.Null(app.PasswordPrompt);
        Assert.Contains(Body, app.Document.PlainText, StringComparison.Ordinal);
        Assert.Equal("Opened locked.pdf", app.LastAction);
        Assert.Contains(app.LastReadDiagnostics, d => d.Code == PdfDiagnosticCodes.EncryptionDecrypted);
    }

    [Fact(Timeout = 600000)]
    public void The_Dialog_Opens_The_Document_With_What_Was_Typed()
    {
        // The dialog's own Open closes it before the answer is read, and closing
        // disposes the field; what was typed has to survive that.
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf");
        WriterPasswordPrompt prompt = app.PasswordPrompt!;

        Assert.True(prompt.PasswordField.IsPassword);
        prompt.PasswordField.Text = "user-pw";
        prompt.Dialog.Accept();

        Assert.Null(app.PasswordPrompt);
        Assert.Contains(Body, app.Document.PlainText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void Escape_Cancels_Like_The_Cancel_Button()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        string before = app.Document.PlainText;
        Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf");

        app.PasswordPrompt!.Dialog.Cancel();

        Assert.Null(app.PasswordPrompt);
        Assert.Equal(before, app.Document.PlainText);
        Assert.StartsWith("Did not open locked.pdf", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Wrong_Password_Asks_Again_Until_The_Right_One()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        string before = app.Document.PlainText;
        Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf");

        app.PasswordPrompt!.Submit("not-it");

        WriterPasswordPrompt again = Assert.IsType<WriterPasswordPrompt>(app.PasswordPrompt);
        Assert.Equal(WriterPasswordReason.Incorrect, again.Reason);
        Assert.Equal(before, app.Document.PlainText);

        again.Submit("user-pw");

        Assert.Contains(Body, app.Document.PlainText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void Cancelling_Leaves_The_Open_Document_Alone()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        string before = app.Document.PlainText;
        Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf");

        app.PasswordPrompt!.Cancel();

        Assert.Null(app.PasswordPrompt);
        Assert.Equal(before, app.Document.PlainText);
        Assert.Equal(
            "Did not open locked.pdf: it is protected by a password. The open document is unchanged.",
            app.LastAction);
    }

    [Fact(Timeout = 600000)]
    public void A_Pdf_That_Withholds_Copying_Asks_For_Its_Owner_Password()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());

        // Anyone can open it - its user password is empty - but its permissions
        // withhold copying, which is what opening it in the Writer does.
        Assert.False(Load(app, LockedPdf(string.Empty, "owner-pw", NoCopying), "restricted.pdf"));
        Assert.Equal(WriterPasswordReason.OwnerRequired, app.PasswordPrompt!.Reason);

        app.PasswordPrompt.Submit("owner-pw");

        Assert.Contains(Body, app.Document.PlainText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Pdf_Protected_Only_By_Its_Permissions_Opens_Without_Asking()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());

        Assert.True(Load(app, LockedPdf(string.Empty, "owner-pw"), "permissions.pdf"));

        Assert.Null(app.PasswordPrompt);
        Assert.Contains(Body, app.Document.PlainText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Head_Without_Password_Support_Refuses_As_Before()
    {
        WriterDocumentFormats formats = WriterDocumentFormats.CreateDefault().With(
            new WriterDocumentFormat(new PdfDocumentCodec(), "PDF", WriterFormatCapabilities.Open));
        using WriterApp app = WriterPdfFormatTests.CreateApp(formats);

        Assert.False(Load(app, LockedPdf("user-pw", "owner-pw"), "locked.pdf"));

        Assert.Null(app.PasswordPrompt);
        Assert.Contains("Could not open locked.pdf", app.LastAction, StringComparison.Ordinal);
        Assert.Contains("user password", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void A_Stream_That_Cannot_Be_Rewound_Is_Refused_Rather_Than_Prompted()
    {
        // The caller's stream is gone by the time a password is typed, so it has
        // to be read again from a copy - which one that cannot rewind cannot give.
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        using var stream = new ForwardOnlyStream(LockedPdf("user-pw", "owner-pw"));

        Assert.False(app.LoadDocument(stream, "locked.pdf"));

        Assert.Null(app.PasswordPrompt);
        Assert.Contains("Could not open locked.pdf", app.LastAction, StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void The_Password_Reaches_No_Status_And_No_Note()
    {
        using WriterApp app = WriterPdfFormatTests.CreateApp(WriterPdfFormatTests.DesktopFormats());
        Load(app, LockedPdf("Needle-4Rt", "owner-pw"), "locked.pdf");

        app.PasswordPrompt!.Submit("Needle-4Rt-wrong");
        Assert.DoesNotContain("Needle", app.LastAction, StringComparison.Ordinal);

        app.PasswordPrompt!.Submit("Needle-4Rt");
        Assert.DoesNotContain("Needle", app.LastAction, StringComparison.Ordinal);
        Assert.All(app.LastReadDiagnostics, d => Assert.DoesNotContain("Needle", d.Message, StringComparison.Ordinal));
    }

    [Fact(Timeout = 600000)]
    public void A_Prompt_Takes_One_Answer()
    {
        var answers = new List<string>();
        using var prompt = new WriterPasswordPrompt(
            "locked.pdf",
            WriterPasswordReason.Required,
            password => answers.Add("submit:" + password),
            () => answers.Add("cancel"));

        prompt.Submit("first");
        prompt.Submit("second");
        prompt.Cancel();

        Assert.Equal(new[] { "submit:first" }, answers);
    }

    [Theory(Timeout = 600000)]
    [InlineData(WriterPasswordReason.Required, "is protected by a password")]
    [InlineData(WriterPasswordReason.Incorrect, "That password did not open")]
    [InlineData(WriterPasswordReason.OwnerRequired, "owner password")]
    public void The_Prompt_Says_Why_It_Asks(WriterPasswordReason reason, string expected)
    {
        Assert.Contains(expected, WriterPasswordPrompt.Describe(reason, "locked.pdf"), StringComparison.Ordinal);
    }

    [Fact(Timeout = 600000)]
    public void The_Desktop_Composition_Hands_The_Password_To_The_Pdf_Reader()
    {
        WriterPasswordSupport passwords = WriterPdfFormatTests.DesktopPdfPasswords();
        var shared = new DocumentReadOptions(resourcePolicy: DocumentResourcePolicy.AllowOwnDocuments);

        PdfReadOptions options = Assert.IsType<PdfReadOptions>(passwords.WithPassword(shared, "user-pw"));

        Assert.NotNull(options.Credentials);
        Assert.Same(shared.ResourcePolicy, options.ResourcePolicy);
        Assert.Equal(shared.Limits.MaxDocumentBytes, options.Limits.MaxDocumentBytes);
    }

    // ---- fixtures -----------------------------------------------------------------

    private static bool Load(WriterApp app, byte[] pdf, string name)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        return app.LoadDocument(stream, name);
    }

    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// A one-page PDF showing <see cref="Body"/>, encrypted with RC4-128 at
    /// revision 3 under <paramref name="user"/> and <paramref name="owner"/>:
    /// ISO 32000-1 Algorithms 1, 2, 3 and 5, run forwards.
    /// </summary>
    private static byte[] LockedPdf(string user, string owner, int permissions = AllPermissions)
    {
        byte[] id = [.. Enumerable.Range(0, 16).Select(i => (byte)((i * 11) + 3))];
        byte[] o = OwnerValue(owner, user);
        byte[] key = FileKey(user, o, permissions, id);
        byte[] u = UserValue(key, id);
        byte[] content = Rc4(ObjectKey(key, 5), Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 720 Td ({Body}) Tj ET"));

        var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Object(string body)
        {
            offsets.Add(output.Length);
            Write(string.Create(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n"));
        }

        Write("%PDF-1.7\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        offsets.Add(output.Length);
        Write(string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n"));
        output.Write(content);
        Write("\nendstream\nendobj\n");

        Object(string.Create(CultureInfo.InvariantCulture,
            $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P {permissions} /O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> >>"));

        long xref = output.Length;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
        foreach (long offset in offsets)
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));

        string hex = Convert.ToHexString(id);
        Write(string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 6 0 R /ID [<{hex}> <{hex}>] >>\nstartxref\n{xref}\n%%EOF\n"));

        return output.ToArray();
    }

    private static byte[] Pad(string password)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(password);
        byte[] padded = new byte[32];
        int length = Math.Min(32, bytes.Length);
        Array.Copy(bytes, padded, length);
        Array.Copy(Padding, 0, padded, length, 32 - length);
        return padded;
    }

    // Algorithm 3: /O, the padded user password under a key from the owner's.
    private static byte[] OwnerValue(string owner, string user)
    {
        byte[] hash = MD5.HashData(Pad(owner.Length > 0 ? owner : user));
        for (int i = 0; i < 50; i++)
            hash = MD5.HashData(hash);

        byte[] ownerKey = hash[..16];
        byte[] value = Rc4(ownerKey, Pad(user));
        for (int i = 1; i <= 19; i++)
            value = Rc4(Xor(ownerKey, i), value);
        return value;
    }

    // Algorithm 2: the file key.
    private static byte[] FileKey(string user, byte[] o, int permissions, byte[] id)
    {
        byte[] hash = MD5.HashData([.. Pad(user), .. o, .. BitConverter.GetBytes(permissions), .. id]);
        for (int i = 0; i < 50; i++)
            hash = MD5.HashData(hash[..16]);
        return hash[..16];
    }

    // Algorithm 5: /U, whose first 16 bytes the reader checks.
    private static byte[] UserValue(byte[] key, byte[] id)
    {
        byte[] value = Rc4(key, MD5.HashData([.. Padding, .. id]));
        for (int i = 1; i <= 19; i++)
            value = Rc4(Xor(key, i), value);
        return [.. value, .. new byte[16]];
    }

    // Algorithm 1: the key for one object of generation zero.
    private static byte[] ObjectKey(byte[] key, int objectNumber) =>
        MD5.HashData([.. key, (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), 0, 0])[..16];

    private static byte[] Xor(byte[] key, int value) => [.. key.Select(b => (byte)(b ^ value))];

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        byte[] s = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
        }

        byte[] output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) % 256;
            j = (j + s[i]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) % 256]);
        }

        return output;
    }

    /// <summary>A stream that reads forwards and cannot seek, as a network or content stream may.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
