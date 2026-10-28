"""Describe already-created release assets; never builds or modifies their bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True)
    parser.add_argument("--product", required=True, choices=("browser", "writer", "cell", "plate", "launcher", "boss"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--self-contained-mode", choices=("self-contained", "native-aot"), default="self-contained")
    parser.add_argument("--additional", nargs="*", default=[])
    parser.add_argument("--dependency-lock")
    parser.add_argument("--demo", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"[a-f0-9]{40}", args.commit) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?", args.version):
        raise ValueError("Full source SHA and semantic component version required")
    root = Path(args.directory)
    names = {"browser": "Browser", "writer": "Writer", "cell": "Cells", "plate": "Plate", "launcher": "Launcher", "boss": "Office.Server"}
    artifacts = []
    files = sorted(root.glob("*.zip")) + [Path(p) for p in args.additional]
    hashes = []
    for path in files:
        if not path.is_file() or path.is_symlink():
            raise ValueError("Missing or linked release asset")
        with path.open("rb") as stream:
            sha = hashlib.file_digest(stream, "sha256").hexdigest()
        hashes.append(f"{sha}  {path.name}")
        if path.suffix != ".zip":
            continue
        rid = next((r for r in ("win-x64", "linux-x64", "browser-wasm") if r in path.name), None)
        if rid is None:
            raise ValueError("Cannot identify archive RID: " + path.name)
        web = rid == "browser-wasm"
        mode = "web" if web else "framework-dependent" if "framework-dependent" in path.name else args.self_contained_mode
        entry = "wwwroot/index.html" if web else "Broiler." + names[args.product] + (".Windows.exe" if rid == "win-x64" else ".Linux")
        if args.product in ("launcher", "boss"):
            entry = "Broiler.Launcher.Windows.exe" if args.product == "launcher" else "Broiler.Office.Server.exe"
        artifacts.append(dict(id="writer-web" if web else args.product, version=args.version, rid=rid,
            asset=path.name, size=path.stat().st_size, sha256=sha, archive="zip", entryPoint=entry,
            invocation="web" if web else "native", publishMode=mode))
    if not artifacts or len({a["asset"] for a in artifacts}) != len(artifacts):
        raise ValueError("Missing or duplicate component artifacts")
    manifest = dict(schemaVersion=1, repository=args.repository, tag=args.tag, version=args.version,
                    commit=args.commit, demo=args.demo, artifacts=artifacts)
    if args.dependency_lock:
        manifest["dependencies"] = [json.loads(Path(args.dependency_lock).read_text(encoding="utf-8-sig"))]
    metadata = root / "component-release.json"
    metadata.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    hashes.append(hashlib.sha256(metadata.read_bytes()).hexdigest() + "  " + metadata.name)
    (root / "SHA256SUMS.txt").write_text("\n".join(sorted(hashes)) + "\n", encoding="ascii")


if __name__ == "__main__":
    main()
