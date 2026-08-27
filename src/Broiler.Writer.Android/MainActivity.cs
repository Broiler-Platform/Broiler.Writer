using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Broiler.App.Android;

namespace Broiler.Writer.Android;

[Activity(
    Label = "Broiler Writer",
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode |
        ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : Activity
{
    private const int OpenDocumentRequest = 1001;
    private const int CreateDocumentRequest = 1002;
    private const string RecoveryFileName = "writer-recovery.rtf";
    private AndroidBroilerView? _view;
    private WriterUiHost? _host;
    private WriterApp? _app;
    private global::Android.Net.Uri? _currentDocumentUri;
    private string _pendingSaveName = "Untitled.rtf";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        _view = new AndroidBroilerView(this, WriterPalette.Canvas);
        _host = new WriterUiHost(
            () => _view.ViewportSize,
            () => _view.Scale,
            _view.InvalidateFrame,
            _view.Present,
            _view.GetClipboardText,
            _view.SetClipboardText,
            _view.NotifyCaretChanged);
        _app = new WriterApp(
            _host,
            Finish,
            RequestOpenDocument,
            RequestSaveDocument,
            compactMode: true);

        _view.RenderFrame = _app.RenderFrame;
        _view.DispatchInput = _app.Dispatch;
        _view.GetSession = () => _app.Session;
        var content = new AndroidInsetLayout(this);
        content.AddView(_view, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        SetContentView(content);

        if (savedInstanceState is null)
            RestoreRecovery();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.SetResumed(true);
    }

    protected override void OnPause()
    {
        SaveRecovery();
        _view?.SetResumed(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _view?.Dispose();
        _app?.Dispose();
        _host?.Dispose();
        _app = null;
        _host = null;
        _view = null;
        base.OnDestroy();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null && _view?.ProcessKeyEvent(e) == true)
            return true;
        return base.DispatchKeyEvent(e);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is not global::Android.Net.Uri uri || _app is null)
            return;

        TakePersistablePermission(data, uri);
        if (requestCode == OpenDocumentRequest)
        {
            using Stream? input = ContentResolver?.OpenInputStream(uri);
            if (input is not null && _app.LoadDocument(input, ResolveDisplayName(uri)))
                _currentDocumentUri = uri;
        }
        else if (requestCode == CreateDocumentRequest)
        {
            _currentDocumentUri = uri;
            SaveToUri(uri, ResolveDisplayName(uri));
        }

        _view?.InvalidateFrame();
    }

    private void RequestOpenDocument()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "application/rtf",
            "text/rtf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "text/html",
            "text/markdown",
            "text/plain",
        });
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, OpenDocumentRequest);
    }

    private void RequestSaveDocument(string suggestedName, bool saveAs)
    {
        _pendingSaveName = EnsureSupportedExtension(suggestedName);
        if (!saveAs && _currentDocumentUri is not null)
        {
            SaveToUri(_currentDocumentUri, _pendingSaveName);
            return;
        }

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(MimeTypeFor(_pendingSaveName));
        intent.PutExtra(Intent.ExtraTitle, _pendingSaveName);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, CreateDocumentRequest);
    }

    private void SaveToUri(global::Android.Net.Uri uri, string displayName)
    {
        using Stream? output = ContentResolver?.OpenOutputStream(uri, "wt");
        if (output is not null)
            _app?.WriteDocument(output, EnsureSupportedExtension(displayName));
        _view?.InvalidateFrame();
    }

    private void SaveRecovery()
    {
        if (_app is null || FilesDir?.AbsolutePath is not string directory)
            return;
        try
        {
            using FileStream output = File.Create(Path.Combine(directory, RecoveryFileName));
            _app.WriteDocument(output, RecoveryFileName, updateIdentity: false);
        }
        catch (IOException)
        {
        }
    }

    private void RestoreRecovery()
    {
        if (_app is null || FilesDir?.AbsolutePath is not string directory)
            return;
        string path = Path.Combine(directory, RecoveryFileName);
        if (!File.Exists(path))
            return;
        try
        {
            using FileStream input = File.OpenRead(path);
            _app.LoadDocument(input, "Recovered.rtf");
        }
        catch (IOException)
        {
        }
    }

    private string ResolveDisplayName(global::Android.Net.Uri uri)
    {
        using ICursor? cursor = ContentResolver?.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
        if (cursor?.MoveToFirst() == true)
        {
            int index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
            if (index >= 0)
                return cursor.GetString(index) ?? _pendingSaveName;
        }

        return uri.LastPathSegment ?? _pendingSaveName;
    }

    private void TakePersistablePermission(Intent data, global::Android.Net.Uri uri)
    {
        ActivityFlags requested = data.Flags &
            (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        try
        {
            ContentResolver?.TakePersistableUriPermission(uri, requested);
        }
        catch (global::Java.Lang.SecurityException)
        {
            // Some document providers grant access only for the current process.
        }
    }

    private static string EnsureSupportedExtension(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".rtf" or ".docx" or ".html" or ".htm" or ".md" or ".markdown"
            ? name
            : name + ".rtf";

    private static string MimeTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".html" or ".htm" => "text/html",
        ".md" or ".markdown" => "text/markdown",
        _ => "application/rtf",
    };
}
