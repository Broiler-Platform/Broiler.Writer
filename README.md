# Broiler.Writer

[![CI](https://github.com/Broiler-Platform/Broiler.Writer/actions/workflows/ci.yml/badge.svg)](https://github.com/Broiler-Platform/Broiler.Writer/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

Broiler.Writer is the word processor of the [Broiler](https://github.com/Broiler-Platform/Broiler)
managed-code application stack for .NET. It holds the four platform heads — Windows, Linux,
Android and WebAssembly — the shared `Broiler.Writer.Core` application they have in common,
and `Broiler.Writer.FormatCodes`, the reveal-codes layer that keeps a structured view of the
document in sync with the editing surface.

Everything below the application — document codecs, DOM, graphics, media, input and the UI
toolkit — lives in its own repository and is consumed here as a submodule.

> **Preview release.** `0.1.0-preview.1` is the first preview of this repository. Public
> APIs, repository layout and persisted behaviour are not frozen and may change before
> `1.0`. The document codecs parse untrusted input — RTF control words, Open XML packages,
> HTML and PDF object graphs — and must be treated as security-sensitive; no fuzzing
> campaign, dependency scan or independent security audit is recorded for this revision.
> Substantial implementation work was AI-assisted, and this repository carries no
> `HUMAN_REVIEW.md` yet, so no checkout of it should be described as human-approved.
>
> This preview is intended for evaluation, testing and contribution — not production,
> security-critical or safety-critical use.

## Getting started

The dependency components are submodules, so the checkout must be recursive:

```bash
git clone --recurse-submodules https://github.com/Broiler-Platform/Broiler.Writer.git
```

If you already cloned without them:

```bash
git submodule update --init --recursive
```

Build and run the Windows head:

```bash
dotnet build Broiler.Windows.Writer.slnx -c Release
```

```bash
dotnet run --project src/Broiler.Writer.Windows/Broiler.Writer.Windows.csproj -c Release
```

Run the tests:

```bash
dotnet test Broiler.Writer.Tests.slnx -c Release
```

### Prerequisites

- **.NET SDK 10.0** or later. The repository is developed against 10.0.400.
- **Windows head** — builds on Windows; targets `net10.0-windows` and renders through the
  Direct2D graphics backend.
- **Linux head** — targets `net10.0`, with X11 clipboard and input coordination. The
  `Debug-Linux` and `Release-Linux` configurations pin `linux-x64`; the plain
  `Debug`/`Release` configurations build framework-dependent.
- **Android head** — needs the `android` workload (`dotnet workload install android`).
  Targets `net10.0-android36.0` with a minimum SDK of 24, builds `android-arm64` and
  `android-x64`, and produces an `.aab` in `Release` and an `.apk` otherwise. Override
  `BroilerAndroidAbis` to package a single ABI.
- **WebAssembly head** — needs the `wasm-tools` workload
  (`dotnet workload install wasm-tools`). It publishes trimmed on every publish path;
  publishing this app untrimmed crashes the mono runtime at boot.

## Document formats

`Broiler.Writer.Core` registers the RTF, DOCX, HTML and Markdown codecs for both opening and
saving. PDF is deliberately **not** part of the shared set: the Windows and Linux heads
register `Broiler.Documents.Pdf` themselves, for **opening only**, so that no head acquires
the codec transitively. `Broiler.Writer.FormatCodes.Tests` asserts that this separation
holds.

## Dialogs

Open, Save As, Insert Picture and Font are **real top-level OS windows** on the Windows head:
each can be moved onto another monitor, is ordered by the window manager, and carries the one
title bar Broiler.UI draws for it rather than a second native caption. Broiler.UI calls this
breaking out (its ADR 0025 and 0026) and makes it the default for every owned window and
dialog; the Windows head supplies the `IUiWindowHost` capability that makes it possible, in
`WriterWindowsUiHost`, and each broken-out dialog gets its own `WriterHostWindow` — a second
Direct2D window that does not own the message loop, so closing a dialog never quits the Writer.

The Linux, Android and WebAssembly heads do not offer the capability, so their dialogs stay
logical subwindows rendered inside the main viewport. That is the documented fallback rather
than a failure: a head either answers `IUiWindowHost` or does not, and Broiler.UI keeps the
dialog inside its owner when it does not.

## Toolbar

The toolbar is wider than the window every head opens, and it does not wrap. What does not fit
moves into a drop-down behind a `»` chevron at its end rather than being drawn past the edge
and clipped away — `StandardToolbar` overflows by default now (`UiToolbarOverflow.Menu`), and
`UiToolbarOverflow.Clip` is the old behaviour for a host that guarantees its own width.

Everything from the first item that does not fit onward moves, so the order in the drop-down
is the order on the bar, and the drop-down flows into columns rather than growing taller than
the window. Arrowing along the bar still reaches every item it holds: focus landing on an
overflowed one brings the drop-down with it. A control with a list of its own — the zoom
picker — works from inside the drop-down as it does on the bar.

## Zoom

The document view reads at 25% to 400% of the size the document states. The toolbar carries
the picker and its two steps, and **View → Zoom** offers the same ladder with the current
level checked; **Ctrl+plus**, **Ctrl+minus**, **Ctrl+0** and **Ctrl+wheel** reach it from the
keyboard and mouse. The compact Android toolbar has no room for the group and reaches zoom
through the View menu, as it does alignment and lists.

Zoom is a property of the view. `StandardRichEdit.Zoom` scales every measurement layout takes
from the document — font sizes, indents, tab stops, picture sizes, the page and its margins —
and nothing it takes from the control, so the text grows inside chrome that stays where it is
and wraps to the column the window actually has. Nothing scaled is written back: a document
saves at the size it was authored at whatever it is being read at. `WriterZoom` holds the
ladder and the key policy, shared by the desktop and browser heads.

## Solutions

Each head has a focused solution containing exactly its transitive closure, so opening one
does not drag in another platform's backends.

| Solution | Entry point | Projects |
|---|---|---|
| `Broiler.Windows.Writer.slnx` | `src/Broiler.Writer.Windows` | 58 |
| `Broiler.Linux.Writer.slnx` | `src/Broiler.Writer.Linux` | 60 |
| `Broiler.Android.Writer.slnx` | `src/Broiler.Writer.Android` | 59 |
| `Broiler.WebAssembly.Writer.slnx` | `src/Broiler.Writer.WebAssembly` | 51 |
| `Broiler.Writer.Tests.slnx` | `src/Broiler.Writer.FormatCodes.Tests` | 55 |

The solutions are **generated, not hand-edited**. `eng/solutions.json` declares each entry
point and the platform boundaries it must not cross; `scripts/update-solutions.ps1` walks the
real project-reference graph and writes the `.slnx` files from it:

```bash
pwsh scripts/update-solutions.ps1
```

`-Verify` fails instead of writing, which is the form CI should run:

```bash
pwsh scripts/update-solutions.ps1 -Verify
```

A hand-edit to a `.slnx` is silently reverted by the next generator run. Add or remove
projects by changing the reference graph, then regenerate.

## Continuous integration

[`ci.yml`](.github/workflows/ci.yml) runs on every push to `main` and every pull request:

- **Solution manifest** — `scripts/update-solutions.ps1 -Verify`, which fails if a
  checked-in `.slnx` no longer matches the reference graph. This is what catches a new
  `ProjectReference` that was never folded into a solution.
- **Build** — the Windows head on `windows-latest`, the Linux head on `ubuntu-latest`.
- **Tests** — the suite on both hosts, because the shared Writer core does clipboard and
  file-dialog work that is easy to make accidentally platform-specific.
- **Android head** and **WebAssembly head** — separate jobs, since each pays for a
  workload the others should not. The WebAssembly job publishes as well as builds: this app
  must publish trimmed, and only a publish exercises that.
- **Publish** — `Release-Windows` and `Release-Linux`, the runtime-identifier-pinned
  configurations. They are project-level builds by necessity: the solutions declare only
  `Debug` and `Release`, so a solution-level build with either fails `MSB4126`.

[`release.yml`](.github/workflows/release.yml) is dispatch-only and uploads build artifacts
for manual testing — `win-x64`, `linux-x64`, a **debug-signed** `android-arm64` APK, and the
`browser-wasm` static site. It creates no GitHub release and signs nothing for distribution;
store-ready signed preview packages come from the monorepo's *Prepare Broiler Preview
Package* workflow, which owns the signing material.

## NativeAOT

The two desktop heads publish with NativeAOT, so the `win-x64` and `linux-x64` artifacts are
**a single self-contained native binary that runs with no .NET runtime installed** — 9.2 MB
for the Windows head, against a 162-file framework-dependent drop. Pass `native-aot: false`
when dispatching `release.yml` to get the framework-dependent output instead.

This works because nothing in the Writer's closure needs the reflection AOT cannot see
through. In particular the Direct2D backend dispatches COM through manual vtable offsets
and `delegate* unmanaged` function pointers rather than `ComImport` interfaces, which is the
one pattern that would otherwise rule it out. An AOT publish of the Windows head reports
**zero `IL2xxx`/`IL3xxx` warnings**, and the Linux head reports zero under the trim, AOT and
single-file analyzers.

Keep it that way: reflection, `Activator.CreateInstance`, and reflection-based serialization
in a head or in `Broiler.Writer.Core` will break the AOT publish while leaving an ordinary
build green. CI publishes both desktop heads with AOT for exactly that reason.

The other two heads are deliberately excluded. **Android** — the Android SDK raises
`XA1040`, "the NativeAOT runtime on Android is an experimental feature and not yet suitable
for production use", and it collides with that head's `PublishTrimmed=false` since native
compilation implies trimming. **WebAssembly** — NativeAOT emits a native binary for a
desktop OS and does not apply; the wasm analogue is mono's own `-p:RunAOTCompilation=true`,
a separate feature that is not enabled.

The nested-submodule set the Writer needs is defined once, in
[`.github/actions/setup-broiler`](.github/actions/setup-broiler/action.yml).

## Build configuration

[`Directory.Build.props`](Directory.Build.props) decomposes the four-configuration scheme the
desktop heads declare. `Debug`/`Release` build framework-dependent; `Debug-Windows`,
`Release-Windows`, `Debug-Linux` and `Release-Linux` pin a runtime identifier and are what
the publish paths use. MSBuild understands only `Debug` and `Release` on its own, so without
this file a `-c Release-Linux` publish builds **unoptimized and with neither `RELEASE` nor
`LINUX` defined**.

It also carries the suppressed-warning list, with a documented reason for every code. That
list was measured against a clean rebuild of all five solutions rather than inherited, and
nothing in it is authored in this repository — the nullable warnings come from component
submodules and from the vendored Android glue, and `CA1416` is a false positive against
runtime `Build.VERSION.SdkInt` guards the analyzer cannot see through. `NU1504` is
deliberately left visible; see *Known issues*.

There is no `Directory.Build.targets`. Broiler.Browser uses one solely to rewrite
`Broiler.HTML`'s stale component paths, and the Writer does not consume `Broiler.HTML`.

## Repository layout

| Path | Contents |
|---|---|
| `src/Broiler.Writer` | Shared application (`Broiler.Writer.Core`) — window, menu, toolbar, RichEdit surface, palette, format registry |
| `src/Broiler.Writer.FormatCodes` | Reveal-codes synchronization, structured editing, and the host policy the heads share — Formatting Codes shortcuts and the zoom ladder |
| `src/Broiler.Writer.FormatCodes.Tests` | xUnit suite — format codes, document load, image render, PDF policy, zoom, desktop host smoke |
| `src/Broiler.Writer.Windows` | Windows head — `WinExe`, Direct2D, Win32 clipboard, and the break-out host that gives each dialog its own OS window |
| `src/Broiler.Writer.Linux` | Linux head — X11 clipboard and input coordination |
| `src/Broiler.Writer.Android` | Android head — activity, manifest, resources |
| `src/Broiler.Writer.WebAssembly` | Browser head — direct-Canvas 2D backend, browser file picker and download |
| `src/Broiler.App` | Source-only directory shared by the desktop heads — per-platform clipboards and Linux input coordination. It has no project of its own; each head links the files it needs. |
| `src/Broiler.App.Android` | Android view, canvas renderer, inset layout, input connection |
| `eng/`, `scripts/` | Solution manifest and generator |
| `.github/` | CI and release workflows, and the `setup-broiler` composite action |
| `Directory.Build.props` | Configuration decomposition and the documented warning suppressions |

## Dependencies

Six components are submodules, pinned to `main`:

| Component | Purpose |
|---|---|
| `Broiler.Documents` | Document model and the RTF, DOCX, HTML, Markdown and PDF codecs |
| `Broiler.DOM` | Canonical DOM, HTML tokenization, parsing, traversal, serialization |
| `Broiler.Graphics` | Managed bitmap/codec/raster core plus platform backends |
| `Broiler.Media` | Image, audio and video abstractions and managed codecs |
| `Broiler.Input` | Keyboard, mouse, pen, touch and text input abstractions |
| `Broiler.UI` | Platform-neutral retained-mode UI toolkit |

Each of those repositories carries nested checkouts of the components *it* depends on, so
that it still builds standalone. `git submodule update --init --recursive` restores the whole
set.

### Known issues

Three consequences of composing independently released components are worth knowing before
you file a bug:

- **Some components compile more than once.** Because each component repository references
  its own nested checkouts by literal relative path, composing them here means
  `Broiler.Media` and `Broiler.Media.Image` are compiled five times in a desktop head,
  `Broiler.Graphics` four times, and `Broiler.Input`, `Broiler.Documents.Model` and
  `Broiler.Documents.FormatCodes` twice. Every nested gitlink points at the same commit as
  the top-level one, so the duplicates are assembly-identical and the build reports no
  reference conflicts — but it is wasted work. The fix is a `$(BroilerGraphicsPath)`-style
  property hook upstream in `Broiler.UI`, `Broiler.Graphics`, `Broiler.Media` and
  `Broiler.Documents`, of the kind `Broiler.CSS` and `Broiler.HTML` already use. The solution
  generator folds the nested paths onto the top-level ones, so the `.slnx` files list each
  assembly once.

- **The nested `Broiler.DOM` gitlink is not on `main`.** `Broiler.Documents` pins its own
  `Broiler.DOM` checkout at `433ec92`, a commit off `fix/wpt-1508-tokenizer-runs`, while the
  top-level `Broiler.DOM` submodule here is on `main` at `8f13a22`. Both are checked out, so
  `Broiler.Dom` is compiled from the older revision when it is reached through
  `Broiler.Documents.Html`. Resolving it means bumping the gitlink upstream in
  `Broiler.Documents`.

- **`NU1504` on every restore.** The nested `Broiler.DOM` checkout lists
  `Microsoft.SourceLink.GitHub` twice, so restore reports a duplicate `PackageReference` for
  `Broiler.Dom` and `Broiler.Dom.Html`. It is left unsuppressed on purpose — it is a real
  upstream defect, and the fix belongs in `Broiler.DOM`. These are the only warnings a clean
  build still emits.

## License

Apache License 2.0 — see [LICENSE](LICENSE).
