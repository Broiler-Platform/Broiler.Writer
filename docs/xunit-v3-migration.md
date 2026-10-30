# Writer xUnit v3 migration

The Writer test project uses xunit.v3 4.0.2 with the already selected
xunit.runner.visualstudio 4.0.1 and Microsoft.NET.Test.Sdk 18.11.0. It is an
executable as required by v3. IsTestingPlatformApplication=false retains the
existing VSTest workflow, runsettings, filters and TRX reports on .NET 10.
Runner dependencies are private assets. No warning suppression was added.

## Fixes

- Print-page resolution uses TestContext.Current.CancellationToken.
- Both waits in the superseded-background-result timeout test observe that token,
  so the xUnit timeout can terminate the wait promptly. The existing complete
  VSTest session timeout is retained and its v2-specific comment was corrected.
- Tests now use the published UI package by default. The old default silently
  relied on a sibling Broiler.UI checkout, which a standalone CI checkout does
  not provide. Explicit UseBroilerUiSource=true remains available when working
  with a compatible sibling checkout and BroilerUiSourceRoot.
- Documents codecs were already selected at preview.31 while the shared Model
  version property still said preview.29. All Documents package entries now use
  the same overridable BroilerDocumentsPackageVersion, defaulting to preview.31.
  This includes the binary compatibility repairs required by UI preview.24.
- A public rendering regression test loads a document with page geometry and
  renders it at 150% zoom through the published RichEdit package. It guards the
  PageGeometry constructor compatibility issue previously hidden by source builds.

## Validation on 2026-10-31

- 287 tests passed in Release, no failures or skips.
- 287 tests passed in Debug, no failures or skips.
- The test solution built in both configurations with warnings treated as errors.
- Windows, Linux, WebAssembly and Android Release builds completed with zero
  warnings/errors under -warnaserror. These are local builds on Windows; Linux,
  browser and Android device execution were not performed.
- Android build used -p:AndroidKeyStore=false; release signing was not exercised.
- The test solution's transitive NuGet vulnerability audit reported none.
- Release test-output hashes match the actual published RichEdit preview.24 and
  Documents.Model preview.31 DLLs from the NuGet cache.

Commands (from the Writer checkout):

```powershell
dotnet test Broiler.Writer.Tests.slnx -c Release -warnaserror --logger trx
dotnet test Broiler.Writer.Tests.slnx -c Debug -warnaserror --logger trx
dotnet build Broiler.Windows.Writer.slnx -c Release -warnaserror
dotnet build Broiler.Linux.Writer.slnx -c Release -warnaserror
dotnet build Broiler.WebAssembly.Writer.slnx -c Release -warnaserror
dotnet build Broiler.Android.Writer.slnx -c Release -warnaserror -p:AndroidKeyStore=false
dotnet list Broiler.Writer.Tests.slnx package --vulnerable --include-transitive
```

Logs and TRX reports are in D:/Broiler/artifacts/writer-xunit3-*.
