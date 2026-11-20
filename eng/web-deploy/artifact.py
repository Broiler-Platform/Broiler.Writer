"""Package/verify complete producer wwwroot bytes. Never builds or rewrites application assets."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
from urllib.parse import unquote, urlsplit
import zipfile

SDK = "10.0."  # .NET 10 SDK band prefix; no pinned subversion
WORKLOAD = "10.0."  # wasm-tools manifest prefix; no pinned subversion
MODE = "trimmed-interpreter"


def stable_net10(value):
    return isinstance(value, str) and re.fullmatch(r"10\.0\.\d+", value) is not None


def validate_toolchain(manifest):
    if not stable_net10(manifest.get("sdk")):
        raise ValueError("Stable .NET 10 SDK required")
    workload = manifest.get("workload")
    if not isinstance(workload, str) or re.fullmatch(r"10\.0\.\d+/10\.0\.\d+", workload) is None:
        raise ValueError("Stable .NET 10 wasm-tools workload required")
    workload_set = manifest.get("workloadSet")
    # Early producers captured blank CLI output lines as a JSON array.
    # Accept exactly one nonempty version without changing published bytes.
    if isinstance(workload_set, list) and all(isinstance(v, str) for v in workload_set):
        values = [v.strip() for v in workload_set if v.strip()]
        workload_set = values[0] if len(values) == 1 else None
    if not stable_net10(workload_set):
        raise ValueError("Stable .NET 10 workload-set provenance required")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_name(name):
    path = PurePosixPath(name)
    if not name or "\\" in name or ":" in name or path.is_absolute() or any(p in ("", ".", "..") for p in name.split("/")):
        raise ValueError("Unsafe archive path: " + name)
    return name


def graph(files):
    """Check the generated entry/import-map graph before an archive can be accepted."""
    html = files["wwwroot/index.html"].decode("utf-8-sig")
    if "#[" in html:
        raise ValueError("Unresolved HTML fingerprints")
    entry = re.search(r'<script\b[^>]*type="module"[^>]*src="([^"]+)"', html)
    mapping = re.search(r'<script\s+type="importmap">([\s\S]*?)</script>', html)
    if not entry or not mapping or not mapping[1].strip():
        raise ValueError("Published module entry and import map required")
    imports = json.loads(mapping[1])

    def resource(relative):
        uri = urlsplit(relative)
        if uri.scheme or uri.netloc or relative.startswith("/") or uri.query or uri.fragment:
            raise ValueError("Entry assets must be relative and release-prefix portable: " + relative)
        path = unquote(uri.path)
        if path.startswith("./"):
            path = path[2:]
        key = "wwwroot/" + safe_name(path)
        if key not in files:
            raise ValueError("Missing generated asset: " + key)
        return files[key]

    resource(entry[1])
    for relative in imports.get("imports", {}).values():
        resource(relative)
    for relative, integrity in imports.get("integrity", {}).items():
        data = resource(imports.get("imports", {}).get(relative, relative))
        algorithm, expected = integrity.split("-", 1)
        if algorithm not in ("sha256", "sha384", "sha512") or base64.b64encode(hashlib.new(algorithm, data).digest()).decode() != expected:
            raise ValueError("Import-map integrity mismatch: " + relative)
    resource("broiler.graphics.webassembly.js")
    if not any(name.startswith("wwwroot/_framework/") and name.endswith(".wasm") for name in files):
        raise ValueError("Published framework/managed WebAssembly assets required")
    resource(imports.get("imports", {}).get("./_framework/dotnet.js", "./_framework/dotnet.js"))
    replay = files.get("wwwroot/broiler.graphics.replay.json")
    if replay:
        manifest = json.loads(replay)
        if manifest["protocol"] != "broiler.canvas/1" or manifest["sha256"].lower() != digest(files["wwwroot/broiler.graphics.webassembly.js"]):
            raise ValueError("Graphics replay cohort mismatch")
    return entry[1]


def tree(root):
    files = {}
    if not root.is_dir() or root.is_symlink() or getattr(root.lstat(), "st_file_attributes", 0) & 0x400:
        raise ValueError("Linked wwwroot is not supported")
    for directory, directories, names in os.walk(root, followlinks=False):
        for name in directories + names:
            path = Path(directory) / name
            info = path.lstat()
            if path.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ValueError("Linked/reparse producer assets are not supported: " + str(path))
            if path.is_file():
                files["wwwroot/" + path.relative_to(root).as_posix()] = path.read_bytes()
    return files


def pack(root, output, identity):
    if identity["product"] not in ("writer", "cell", "launcher") or identity["productName"] != {"writer":"Writer", "cell":"Cells", "launcher":"Launcher"}[identity["product"]]:
        raise ValueError("Unknown producer identity")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", identity["version"]):
        raise ValueError("Semantic producer version required")
    if not re.fullmatch(r"[0-9a-f]{40}", identity["commit"]):
        raise ValueError("Full actual source commit required")
    validate_toolchain(identity)
    if identity["runtimeMode"] != MODE:
        raise ValueError("Producer cohort differs from pinned trimmed interpreter configuration")
    if not identity["verificationOnly"] and identity["dirty"]:
        raise ValueError("Release archives require committed clean sources")
    if identity["publishTrimmed"] is not True or identity["runAotCompilation"] is not False:
        raise ValueError("Only the trimmed interpreter configuration is supported")
    identity["releaseEligible"] = not identity["verificationOnly"] and not identity["dirty"]
    files = tree(root)
    entry = graph(files)
    output.mkdir(parents=True, exist_ok=True)
    stem = f"Broiler.{identity['productName']}-{identity['version']}-browser-wasm"
    archive = output / (stem + ".zip")
    metadata = output / (stem + ".web-artifact.json")
    sums = output / (stem + ".SHA256SUMS.txt")
    if any(p.exists() for p in (archive, metadata, sums)):
        raise ValueError("Producer assets already exist; versions may not be overwritten")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as target:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            target.writestr(info, data)
    manifest = dict(schemaVersion=1, artifactKind="broiler-web-static", **identity,
        entryPoint="wwwroot/index.html", moduleEntry=entry, archive=archive.name,
        archiveSize=archive.stat().st_size, archiveSha256=digest(archive.read_bytes()),
        files=[dict(path=name, size=len(data), sha256=digest(data)) for name, data in sorted(files.items())])
    metadata.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    sums.write_text(f"{manifest['archiveSha256']}  {archive.name}\n{digest(metadata.read_bytes())}  {metadata.name}\n", encoding="ascii")
    verify(archive, metadata)
    return metadata


def verify(archive, metadata, extract=None, require_release=False, expected=None):
    manifest = json.loads(metadata.read_text(encoding="utf-8-sig"))
    if manifest["schemaVersion"] != 1 or manifest["artifactKind"] != "broiler-web-static" or manifest["runtimeMode"] != MODE:
        raise ValueError("Unsupported web artifact")
    validate_toolchain(manifest)
    if manifest["publishTrimmed"] is not True or manifest["runAotCompilation"] is not False:
        raise ValueError("Web producer cohort mismatch")
    if require_release and (not manifest["releaseEligible"] or manifest["verificationOnly"] or manifest["dirty"]):
        raise ValueError("Local verification assets cannot be released")
    for field, value in (expected or {}).items():
        if value is not None and manifest.get(field) != value:
            raise ValueError("Producer source identity mismatch: " + field)
    if manifest["archive"] != archive.name or manifest["archiveSize"] != archive.stat().st_size or manifest["archiveSha256"] != digest(archive.read_bytes()):
        raise ValueError("Archive identity/size/digest mismatch")
    expected = {item["path"]: item for item in manifest["files"]}
    if len(expected) != len(manifest["files"]):
        raise ValueError("Duplicate inventory paths")
    files, folded = {}, set()
    with zipfile.ZipFile(archive) as source:
        for info in source.infolist():
            name = safe_name(info.filename)
            if not name.startswith("wwwroot/") or name.casefold() in folded or stat.S_ISLNK(info.external_attr >> 16):
                raise ValueError("Unsafe, duplicate or out-of-root archive member")
            folded.add(name.casefold())
            row = expected.get(name)
            if row is None or info.file_size != row["size"] or info.file_size > 128 * 1024 * 1024:
                raise ValueError("Unexpected or oversized archive member")
            if sum(len(value) for value in files.values()) + info.file_size > 512 * 1024 * 1024:
                raise ValueError("Expanded web archive exceeds producer limit")
            data = source.read(info)
            if digest(data) != row["sha256"]:
                raise ValueError("Asset digest mismatch: " + name)
            files[name] = data
    if set(files) != set(expected) or graph(files) != manifest["moduleEntry"]:
        raise ValueError("Incomplete archive or entry mismatch")
    if extract:
        if extract.exists():
            raise ValueError("Extraction destination must be new")
        extract.mkdir(parents=True)
        for name, data in files.items():
            path = extract / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("pack")
    create.add_argument("--wwwroot", type=Path, required=True)
    create.add_argument("--output", type=Path, required=True)
    create.add_argument("--identity", type=Path, required=True)
    check = commands.add_parser("verify")
    check.add_argument("--archive", type=Path, required=True)
    check.add_argument("--metadata", type=Path, required=True)
    check.add_argument("--extract", type=Path)
    check.add_argument("--require-release", action="store_true")
    for field in ("product", "version", "tag", "commit", "repository"):
        check.add_argument("--" + field)
    args = parser.parse_args()
    if args.command == "pack":
        identity = json.loads(args.identity.read_text(encoding="utf-8-sig"))
        print(pack(args.wwwroot, args.output, identity))
    else:
        expected = {field:getattr(args, field) for field in ("product", "version", "tag", "commit", "repository")}
        print(json.dumps({"verified": verify(args.archive, args.metadata, args.extract, args.require_release, expected)["archive"]}))
