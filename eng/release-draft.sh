#!/usr/bin/env bash
# Creates the draft GitHub pre-release for one Broiler.Writer version.
#
#   eng/release-draft.sh <version> <artifacts-dir>
#
# <artifacts-dir> holds the Publish workflow's artifacts, extracted as
# `gh run download` / actions/download-artifact leave them:
#
#   <artifacts-dir>/broiler-writer-win-x64-self-contained-<version>/Broiler.Writer.Windows.exe
#   <artifacts-dir>/broiler-writer-win-x64-framework-dependent-<version>/Broiler.Writer.Windows.exe, *.dll, ...
#   <artifacts-dir>/broiler-writer-linux-x64-self-contained-<version>/Broiler.Writer.Linux
#   <artifacts-dir>/broiler-writer-linux-x64-framework-dependent-<version>/Broiler.Writer.Linux, *.dll, ...
#   <artifacts-dir>/broiler-writer-android-<version>/Broiler.Writer-<version>.aab
#                                                    Broiler.Writer-<version>-arm64.apk
#
# Each desktop artifact is zipped into one release asset,
# Broiler.Writer-<version>-<rid>-<variant>.zip: the self-contained one holds the NativeAOT
# executable alone, the framework-dependent one the whole publish folder. The executable is
# recorded as rwxr-xr-x and every other file as rw-r--r--: the workflow artifact drops the
# Linux binary's executable bit, the release zip restores it.
# The Android packages are archives already and signed, so they are attached as they are.
# They are optional, so a run from before Android was published can still be released;
# ANDROID_SIGNING_CERT_SHA256, when set, is quoted in the notes for checking a download.
#
# The tag writer-v<version> must already exist on the remote; the release is created
# for it as a draft, so nothing is public until someone publishes it. Needs the GitHub
# CLI with GH_TOKEN (or a login) that may write releases, and Python 3.
set -euo pipefail

version=${1:?usage: eng/release-draft.sh <version> <artifacts-dir>}
artifacts=${2:?usage: eng/release-draft.sh <version> <artifacts-dir>}
tag="writer-v$version"
out=$(mktemp -d)

# Python writes the zip so the Unix mode is recorded explicitly instead of read from the
# file system, which on Windows has no executable bit to read.
asset() {
  local rid=$1 variant=$2 executable=$3
  local source="$artifacts/broiler-writer-$rid-$variant-$version"
  local zip="$out/Broiler.Writer-$version-$rid-$variant.zip"
  [ -f "$source/$executable" ] || { echo "missing $source/$executable" >&2; exit 1; }
  if [ "$variant" = self-contained ] && [ "$(find "$source" -type f | wc -l)" -ne 1 ]; then
    echo "$source should hold $executable alone" >&2; exit 1
  fi
  "$python" - "$source" "$zip" "$executable" <<'PY'
import os, sys, zipfile, time
source, target, executable = sys.argv[1:4]
stamp = time.localtime()[:6]
with zipfile.ZipFile(target, 'w') as z:
    for root, dirs, files in os.walk(source):
        dirs.sort()
        for name in sorted(files):
            path = os.path.join(root, name)
            arcname = os.path.relpath(path, source).replace(os.sep, '/')
            info = zipfile.ZipInfo(arcname, date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3          # Unix, so external_attr carries a mode
            mode = 0o755 if arcname == executable else 0o644
            info.external_attr = (0o100000 | mode) << 16   # regular file
            with open(path, 'rb') as f:
                z.writestr(info, f.read())
    for license_name in ('LICENSE', 'LICENSE.md', 'LICENSE.txt'):
        if os.path.isfile(license_name):
            if 'LICENSE' not in z.namelist():
                z.write(license_name, 'LICENSE')
            break
PY
  echo "$zip"
}

python=$(command -v python3 || command -v python) || { echo "needs python3" >&2; exit 1; }

# One assignment per asset: under set -e a failing $(...) stops the script only when it is
# the whole assignment, not one element of an array literal.
desktop_assets=()
for rid in win-x64 linux-x64; do
  if [ "$rid" = win-x64 ]; then executable=Broiler.Writer.Windows.exe; else executable=Broiler.Writer.Linux; fi
  for variant in self-contained framework-dependent; do
    zip=$(asset "$rid" "$variant" "$executable")
    desktop_assets+=("$zip")
  done
done

web_source="$artifacts/broiler-writer-web-$version"
web_zip="$out/Broiler.Writer-$version-browser-wasm.zip"
[ -f "$web_source/wwwroot/index.html" ] || { echo "missing published Writer web bundle" >&2; exit 1; }
"$python" - "$web_source" "$web_zip" <<'PY'
import pathlib, sys, zipfile
source, target = pathlib.Path(sys.argv[1]), sys.argv[2]
with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(source.rglob('*')):
        if path.is_symlink():
            raise ValueError('Writer web artifact must not contain links')
        if path.is_file():
            archive.write(path, path.relative_to(source).as_posix())
PY
desktop_assets+=("$web_zip")

android_dir="$artifacts/broiler-writer-android-$version"
aab="$android_dir/Broiler.Writer-$version.aab"
apk="$android_dir/Broiler.Writer-$version-arm64.apk"
android_assets=()
android_rows=''
if [ -d "$android_dir" ]; then
  for f in "$aab" "$apk"; do [ -f "$f" ] || { echo "missing $f" >&2; exit 1; }; done
  android_assets=("$aab" "$apk")
  android_rows="| Android (arm64) | \`Broiler.Writer-$version-arm64.apk\` | install on the device (sideload) |
| Android | \`Broiler.Writer-$version.aab\` | app bundle for Google Play (arm64 + x86_64) |"
fi

cat > "$out/notes.md" <<EOF
Broiler Writer **$version**, a preview build for evaluation and testing, not for
production use.

## Downloads

| Platform | File | Run |
| --- | --- | --- |
| Windows x64 | \`Broiler.Writer-$version-win-x64-self-contained.zip\` | unzip, start \`Broiler.Writer.Windows.exe\` |
| Windows x64 | \`Broiler.Writer-$version-win-x64-framework-dependent.zip\` | install the .NET 10 runtime, unzip into a folder, start \`Broiler.Writer.Windows.exe\` |
| Linux x64 | \`Broiler.Writer-$version-linux-x64-self-contained.zip\` | unzip, run \`./Broiler.Writer.Linux\` (X11) |
| Linux x64 | \`Broiler.Writer-$version-linux-x64-framework-dependent.zip\` | install the .NET 10 runtime, unzip into a folder, run \`./Broiler.Writer.Linux\` (X11) |
$android_rows

The **self-contained** zips hold a single NativeAOT executable: no .NET runtime to install,
nothing else to copy. The **framework-dependent** zips hold the executable together with the
application's assemblies and run on the .NET 10 runtime installed on the machine, so they
pick up its servicing updates. The executables are not code-signed, so Windows SmartScreen
may ask before the first start.
EOF

if [ ${#android_assets[@]} -gt 0 ]; then
  {
    echo
    echo 'The Android packages are signed with the Broiler release key.'
    if [ -n "${ANDROID_SIGNING_CERT_SHA256:-}" ]; then
      echo "Signing certificate SHA-256: \`$ANDROID_SIGNING_CERT_SHA256\`"
    fi
  } >> "$out/notes.md"
fi

"$python" eng/write-component-manifest.py --directory "$out" --product writer --version "$version" --repository Broiler-Platform/Broiler.Writer --tag "$tag" --commit "$(git rev-parse HEAD)" --self-contained-mode native-aot --additional "${android_assets[@]}"

gh release create "$tag" "${desktop_assets[@]}" "${android_assets[@]}" "$out/component-release.json" "$out/SHA256SUMS.txt" \
  --verify-tag \
  --draft \
  --prerelease \
  --title "Broiler Writer $version" \
  --notes-file "$out/notes.md" \
  --generate-notes
