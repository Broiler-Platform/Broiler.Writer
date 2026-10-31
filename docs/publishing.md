# Product publication

Run **Publish** on the product repository's main branch. Windows, Linux, Android
and Web are independent boolean inputs, all enabled by default. Select at least
one. Desktop targets include NativeAOT and framework-dependent ZIPs. Android
requires the existing four signing secrets only when selected. Web runs the
reusable archive/browser verification workflow.

Leave `version-suffix` blank to calculate the next preview from the configured
product release line and existing product tags. A web-only run uses the same
product version/tag sequence as desktop and Android; old web-only releases remain
available. Each run creates a new version, rather than modifying an old release.
The separate manual `publish-web.yml` entry point has been removed; for web-only
publication disable Windows, Linux and Android here.

`create_draft=true` keeps the prerelease private for review. Set it to false to
publish the prerelease after all selected targets and product regression tests
succeed. Notes and component checksums list only selected assets. Published web
assets can be consumed by the website's Site workflow, which must then be run.
Desktop Office can select the published native ZIPs independently.

The workflow also exposes `workflow_call`, the same inputs, and version/tag
outputs for reuse inside the producer repository. A future Office coordinator
should dispatch this workflow in the producer repository. Direct calls from a
foreign repository fail before building: a reusable workflow's default GitHub
token and source context belong to its caller.
Android secrets must be passed when Android is selected. No central Office
coordinator or latest-component-release selection is added by this product change.

SDK setup uses `10.0.x`. Dependencies continue to restore under the product's
NuGet policy; this workflow does not republish dependencies or replace existing
releases. Product versions remain previews, rather than introducing a new stable
release policy.
