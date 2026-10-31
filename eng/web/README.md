# Web static producer

`producer.json` identifies this producer. From its `eng/web` directory, run:

```powershell
./publish.ps1 -Version 0.1.0-preview.1 -Tag writer-web-v0.1.0-preview.1
```

Select the actual product tag and version; the example is not a released asset.
Launcher additionally requires explicit `-HostingVersion` and `-GraphicsVersion`
for the selected browser package cohort. Use the published W1 package generation.

Releases require clean committed sources, a .NET 10 SDK (10.0.x), the wasm-tools workload,
and a new output directory. NuGet restore runs without lock files. No
source-project fallback or application rebuild occurs downstream. AOT is disabled.
`-VerificationOnly` permits local dirty checks but permanently marks their ZIP
and descriptor as ineligible for a release or deployment.

`artifact.py` packages the complete published wwwroot without rewriting bytes,
then verifies its archive/file digests, generated entry/import map/SRI and replay
manifest. `smoke.mjs` boots the extracted output in Chromium, Firefox and WebKit
using fresh normal profiles. Install its pinned dependencies with `npm ci` and
`npx playwright install chromium firefox webkit`.

The reusable producer workflow runs these checks before uploading the original
ZIP, descriptor and checksum. Standalone and full-product release workflows
consume that one artifact. The standalone workflow creates a reviewable draft.

These tools are copied from the Office repository's `eng/web-producer`. Keep
the shared files identical; change only `producer.json` for product-specific
configuration. Pages deployment tools are separate under `eng/web-deploy`.
The Office repository's `docs/broiler-office-web-w6.md` describes suite locking,
browser integration, three-site Pages staging, catalog promotion and rollback.
