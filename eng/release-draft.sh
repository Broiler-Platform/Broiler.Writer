#!/usr/bin/env bash
# Creates the draft GitHub pre-release for one Broiler.Writer version.
#
#   eng/release-draft.sh <version> <artifacts-dir>
#
# <artifacts-dir> holds the Publish workflow's artifacts, extracted as
# `gh run download` / actions/download-artifact leave them:
#
#   <artifacts-dir>/broiler-writer-win-x64-<version>/Broiler.Writer.Windows.exe
#   <artifacts-dir>/broiler-writer-linux-x64-<version>/Broiler.Writer.Linux
#   <artifacts-dir>/broiler-writer-android-<version>/Broiler.Writer-<version>.aab
#                                                    Broiler.Writer-<version>-arm64.apk
#
# Each executable is zipped on its own into a release asset, recorded as rwxr-xr-x:
# the workflow artifact drops the Linux binary's executable bit, the release zip restores it.
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

# Python writes the zip so the Unix mode is recorded explicitly (rwxr-xr-x) instead of
# read from the file system, which on Windows has no executable bit to read.
asset() {
  local rid=$1 executable=$2
  local source="$artifacts/broiler-writer-$rid-$version/$executable"
  local zip="$out/Broiler.Writer-$version-$rid.zip"
  [ -f "$source" ] || { echo "missing $source" >&2; exit 1; }
  "$python" - "$source" "$zip" "$executable" <<'PY'
import sys, zipfile, time
source, target, name = sys.argv[1:4]
info = zipfile.ZipInfo(name, date_time=time.localtime()[:6])
info.compress_type = zipfile.ZIP_DEFLATED
info.create_system = 3                      # Unix, so external_attr carries a mode
info.external_attr = (0o100755 << 16)       # regular file, rwxr-xr-x
with open(source, 'rb') as f, zipfile.ZipFile(target, 'w') as z:
    z.writestr(info, f.read())
PY
  echo "$zip"
}

python=$(command -v python3 || command -v python) || { echo "needs python3" >&2; exit 1; }

win=$(asset win-x64 Broiler.Writer.Windows.exe)
linux=$(asset linux-x64 Broiler.Writer.Linux)

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
| Windows x64 | \`Broiler.Writer-$version-win-x64.zip\` | unzip, start \`Broiler.Writer.Windows.exe\` |
| Linux x64 | \`Broiler.Writer-$version-linux-x64.zip\` | unzip, run \`./Broiler.Writer.Linux\` (X11) |
$android_rows

Each zip holds a single self-contained NativeAOT executable: no .NET runtime to install,
nothing else to copy. The executables are not code-signed, so Windows SmartScreen may ask
before the first start.
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

gh release create "$tag" "$win" "$linux" "${android_assets[@]}" \
  --verify-tag \
  --draft \
  --prerelease \
  --title "Broiler Writer $version" \
  --notes-file "$out/notes.md" \
  --generate-notes
