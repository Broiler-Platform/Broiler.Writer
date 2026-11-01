# Running Writer WebAssembly locally

Run the WebAssembly project with its launch profile or `dotnet run --project
src/Broiler.Writer.WebAssembly/Broiler.Writer.WebAssembly.csproj`.

Hosting preview.13 and Graphics preview.16 package their JavaScript as external
Content with a wwwroot Link. The SDK's development asset manifest registers the
app wwwroot as their content root even though the files remain in the NuGet
cache. Local DevServer then returns an empty response for the fingerprinted URL,
causing an empty MIME type and a SHA-256 integrity failure. The hash beginning
`47DEQpj8` is the hash of an empty body, not a reason to disable integrity checks.

BrowserPackageAssets.targets supplies compatibility registrations with the real
package content roots. Modules remain fingerprinted and integrity checked. The
Hosting and Graphics producer targets have the same correction for future NuGet
packages. No package-cache edits, source-tree JS copies or disabled browser
security checks are required.

Stop the existing local debug server and rebuild/restart Writer after updating
these files: the server loads its static-asset manifest at startup. If a browser
tab retains the previous manifest, reload it after the new server starts.
