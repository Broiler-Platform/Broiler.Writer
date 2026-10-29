"""Artifact-only Office web integration and immutable GitHub Pages publishing.

No dotnet invocation. Release assets are downloaded by GitHub numeric identity;
producer metadata and every extracted file are verified before site mutation.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import urllib.request
from urllib.parse import urlsplit
import zipfile

HERE = Path(__file__).resolve().parent
VALIDATOR = HERE.parent / "eng/web-producer/artifact.py"
if not VALIDATOR.exists():
    VALIDATOR = HERE / "artifact.py"  # vendored deployment tool in producer repositories
spec = importlib.util.spec_from_file_location("producer", VALIDATOR)
producer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(producer)
PRODUCTS = {"launcher": ("Broiler-Platform/Broiler", "demo.broiler.app"),
            "writer": ("Broiler-Platform/Broiler.Writer", "writer.broiler.app"),
            "cell": ("Broiler-Platform/Broiler.Cells", "cell.broiler.app")}
VERSION = re.compile(r"[A-Za-z0-9][A-Za-z0-9.+-]{0,79}\Z")
SHA = re.compile(r"[0-9a-f]{64}\Z")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def validate(lock):
    if lock.get("schemaVersion") != 1 or lock.get("artifactKind") != "broiler-office-web-lock" or lock.get("status") != "locked":
        raise ValueError("A complete, reviewed web lock is required")
    if not VERSION.fullmatch(lock.get("catalogVersion", "")) or lock.get("channel") not in ("preview", "stable"):
        raise ValueError("Invalid suite identity")
    rows = lock.get("components", [])
    if len(rows) != 3 or {r.get("product") for r in rows} != set(PRODUCTS):
        raise ValueError("Exactly Launcher, Writer and Cell are required")
    for row in rows:
        repo, host = PRODUCTS[row["product"]]
        if row.get("repository") != repo or not VERSION.fullmatch(row.get("version", "")):
            raise ValueError("Unexpected repository or version")
        if not re.fullmatch(r"[0-9a-f]{40}", row.get("commit", "")) or not VERSION.fullmatch(row.get("tag", "")):
            raise ValueError("Exact tag and source commit required")
        if row.get("entryUrl") != f'https://{host}/releases/{row["version"]}/':
            raise ValueError("Entry URL must match the immutable version")
        if row.get("runtimeMode") != producer.MODE or row.get("sdk") != producer.SDK or row.get("workload") != producer.WORKLOAD or row.get("workloadSet") != "10.403.0":
            raise ValueError("Runtime cohort must match the approved producer")
        if row.get("inputProtocol") != ("broiler.host/1" if row["product"] == "launcher" else row["product"] + ".browser/1") or row.get("replayProtocol") not in ("broiler.canvas/1", "broiler.canvas/legacy-preview14"):
            raise ValueError("Unsupported replay/input protocol")
        if type(row.get("releaseId")) is not int or row["releaseId"] <= 0:
            raise ValueError("Numeric release identity required")
        for field in ("archive", "metadata"):
            asset = row[field]
            if type(asset.get("id")) is not int or asset["id"] <= 0 or type(asset.get("size")) is not int or not 0 < asset["size"] <= 512 * 1024 * 1024:
                raise ValueError("Invalid asset identity or size")
            if not SHA.fullmatch(asset.get("sha256", "")) or Path(asset.get("name", "")).name != asset.get("name") or not re.fullmatch(r"[A-Za-z0-9.+_-]+", asset["name"]):
                raise ValueError("Invalid asset digest or filename")
    return lock


def fetch(url, maximum=512 * 1024 * 1024, accept="application/json"):
    headers = {"Accept": accept, "User-Agent": "Broiler-Office-Web/1"}
    if urlsplit(url).netloc == "api.github.com" and os.environ.get("GH_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    request = urllib.request.Request(url, headers=headers)
    class AssetRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, response_headers, target):
            if urlsplit(target).scheme != "https":
                raise ValueError("Release downloads require HTTPS")
            redirected = super().redirect_request(req, fp, code, msg, response_headers, target)
            if redirected is not None and urlsplit(target).netloc != urlsplit(req.full_url).netloc:
                redirected.remove_header("Authorization")
            return redirected
    with urllib.request.build_opener(AssetRedirect()).open(request, timeout=60) as response:
        data = response.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("Response exceeds size bound")
    return data


def release_row(product, tag):
    repo, host = PRODUCTS[product]
    if not VERSION.fullmatch(tag):
        raise ValueError("Invalid tag")
    release = json.loads(fetch(f"https://api.github.com/repos/{repo}/releases/tags/{tag}"))
    if release["draft"]:
        raise ValueError("Publish producer releases before locking")
    assets = release["assets"]
    metas = [a for a in assets if a["name"].endswith(".web-artifact.json")]
    if len(metas) != 1:
        raise ValueError("Exactly one producer descriptor required")
    meta_asset = metas[0]
    meta_bytes = fetch(meta_asset["url"], meta_asset["size"], "application/octet-stream")
    meta = json.loads(meta_bytes)
    archive_asset = next(a for a in assets if a["name"] == meta["archive"])
    archive_bytes = fetch(archive_asset["url"], archive_asset["size"], "application/octet-stream")
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        (root / archive_asset["name"]).write_bytes(archive_bytes)
        (root / meta_asset["name"]).write_bytes(meta_bytes)
        producer.verify(root / archive_asset["name"], root / meta_asset["name"], require_release=True,
                        expected={"product": product, "repository": repo, "tag": tag})
        protocols(root / archive_asset["name"], meta)
    # Resolve annotated tags to the actual commit; target_commitish can be a mutable branch.
    ref = json.loads(fetch(f"https://api.github.com/repos/{repo}/git/ref/tags/{tag}"))["object"]
    for _ in range(8):
        if ref["type"] == "commit":
            break
        if ref["type"] != "tag":
            raise ValueError("Unexpected tag target")
        ref = json.loads(fetch(ref["url"]))["object"]
    if ref["type"] != "commit" or ref["sha"] != meta["commit"]:
        raise ValueError("Release tag and producer source commit differ")
    def identity(asset, data):
        if len(data) != asset["size"]:
            raise ValueError("Release asset size differs")
        return {"id": asset["id"], "name": asset["name"], "size": len(data), "sha256": digest(data)}
    return {"product": product, "repository": repo, "version": meta["version"], "tag": tag,
            "commit": meta["commit"], "releaseId": release["id"], "entryUrl": f'https://{host}/releases/{meta["version"]}/',
            "archive": identity(archive_asset, archive_bytes), "metadata": identity(meta_asset, meta_bytes),
            **{key: meta[key] for key in ("sdk", "workload", "workloadSet", "runtimeMode", "replayProtocol", "inputProtocol")}}


def protocols(path, meta):
    expected_input = "broiler.host/1" if meta["product"] == "launcher" else meta["product"] + ".browser/1"
    with zipfile.ZipFile(path) as archive:
        entry = archive.read("wwwroot/" + meta["moduleEntry"].removeprefix("./")).decode("utf-8")
        if meta.get("inputProtocol") != expected_input or not re.search(r'export const officeInputProtocolVersion\s*=\s*1\s*;', entry):
            raise ValueError("Producer input adapter protocol mismatch")
        if "wwwroot/broiler.graphics.replay.json" in archive.namelist():
            replay = json.loads(archive.read("wwwroot/broiler.graphics.replay.json"))
            if replay.get("protocol") != "broiler.canvas/1" or meta.get("replayProtocol") != replay["protocol"]:
                raise ValueError("Replay protocol mismatch")
        elif meta.get("replayProtocol") != "broiler.canvas/legacy-preview14" or "Broiler.Graphics.WebAssembly/0.1.0-preview.14" not in meta.get("dependencies", []):
            raise ValueError("Legacy replay must pin the exact preview.14 package")
        if meta["product"] == "launcher":
            host = archive.read("wwwroot/broiler.hosting.webassembly.js").decode("utf-8")
            if not re.search(r'export const hostProtocolVersion\s*=\s*1\s*;', host):
                raise ValueError("Published W1 input host required for Launcher")


def obtain(row, cache):
    cache.mkdir(parents=True, exist_ok=True)
    release = json.loads(fetch(f'https://api.github.com/repos/{row["repository"]}/releases/{row["releaseId"]}'))
    if release["draft"] or release["tag_name"] != row["tag"]:
        raise ValueError("Release identity changed")
    for field in ("archive", "metadata"):
        asset = row[field]
        actual = next((a for a in release["assets"] if a["id"] == asset["id"]), None)
        if actual is None or actual["name"] != asset["name"] or actual["size"] != asset["size"]:
            raise ValueError("Asset no longer belongs to locked release")
        path = cache / asset["name"]
        data = path.read_bytes() if path.exists() else fetch(actual["url"], asset["size"], "application/octet-stream")
        if len(data) != asset["size"] or digest(data) != asset["sha256"]:
            raise ValueError("Locked asset bytes differ")
        path.write_bytes(data)
    meta = producer.verify(cache / row["archive"]["name"], cache / row["metadata"]["name"], require_release=True,
        expected={k: row[k] for k in ("product", "repository", "version", "tag", "commit")})
    protocols(cache / row["archive"]["name"], meta)
    if any(row[key] != meta.get(key) for key in ("sdk", "workload", "workloadSet", "runtimeMode", "replayProtocol", "inputProtocol")):
        raise ValueError("Lock and producer protocol/runtime cohort differ")
    return meta


def stage(lock, product, site, cache):
    validate(lock)
    row = next(r for r in lock["components"] if r["product"] == product)
    meta = obtain(row, cache)
    destination = site / "releases" / row["version"]
    if destination.exists():
        existing = {str(p.relative_to(destination)).replace("\\", "/"): digest(p.read_bytes()) for p in destination.rglob("*") if p.is_file()}
        expected = {r["path"].removeprefix("wwwroot/"): r["sha256"] for r in meta["files"]}
        if existing != expected:
            raise ValueError("Immutable version already exists with different content")
    else:
        with tempfile.TemporaryDirectory() as temp:
            extracted = Path(temp) / "extracted"
            producer.verify(cache / row["archive"]["name"], cache / row["metadata"]["name"], extract=extracted, require_release=True)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(extracted / "wwwroot", destination)
    site.mkdir(parents=True, exist_ok=True)
    (site / ".nojekyll").touch()
    (site / "CNAME").write_text(PRODUCTS[product][1] + "\n", encoding="utf-8")
    receipt = {"schemaVersion": 1, "product": product, "component": row, "runtimeMode": meta["runtimeMode"],
               "replayProtocol": row["replayProtocol"], "inputProtocol": row["inputProtocol"], "files": meta["files"]}
    receipt_path = site / "receipts" / f'{row["version"]}.json'
    if receipt_path.exists() and read(receipt_path) != receipt:
        raise ValueError("Immutable receipt differs")
    write(receipt_path, receipt)
    if not (site / "index.html").exists():
        if product == "launcher":
            (site / "index.html").write_text(ROUTER, encoding="utf-8")
        else:
            (site / "index.html").write_text('<!doctype html><meta charset="utf-8"><title>Broiler Office</title><a href="https://demo.broiler.app/">Open the approved Broiler Office suite</a>', encoding="utf-8")
    return receipt


def health(lock):
    validate(lock)
    delivery = []
    for row in lock["components"]:
        base = row["entryUrl"].split("/releases/")[0]
        receipt = json.loads(fetch(base + f'/receipts/{row["version"]}.json'))
        if receipt.get("component") != row or receipt.get("runtimeMode") != producer.MODE or receipt.get("inputProtocol") != row["inputProtocol"] or receipt.get("replayProtocol") != row["replayProtocol"]:
            raise ValueError("Remote receipt identity mismatch")
        # Fetch the pinned descriptor as an independent authority for the remote inventory.
        with tempfile.TemporaryDirectory() as temp:
            meta = obtain(row, Path(temp))
        if receipt.get("files") != meta["files"]:
            raise ValueError("Remote inventory differs from producer")
        for asset in receipt["files"]:
            relative = producer.safe_name(asset["path"]).removeprefix("wwwroot/")
            url = row["entryUrl"] + relative
            data = fetch(url, asset["size"], "*/*")
            if len(data) != asset["size"] or digest(data) != asset["sha256"]:
                raise ValueError("Remote static file differs: " + url)
            suffix = Path(relative).suffix
            expected_mime = {".wasm": ("application/wasm",), ".js": ("text/javascript", "application/javascript"), ".html": ("text/html",), ".json": ("application/json", "text/json"), ".css": ("text/css",)}.get(suffix)
            if expected_mime:
                request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "Broiler-Office-Web/1"})
                with urllib.request.urlopen(request, timeout=60) as response:
                    target = urlsplit(response.geturl())
                    if target.scheme != "https" or target.netloc != urlsplit(url).netloc or response.headers.get_content_type() not in expected_mime:
                        raise ValueError("HTTPS origin or MIME type differs: " + url)
                    if relative == "index.html":
                        delivery.append({"product": row["product"], "url": url, "contentType": response.headers.get_content_type(),
                                         "cacheControl": response.headers.get("Cache-Control"), "contentEncoding": response.headers.get("Content-Encoding"),
                                         "securityHeaders": {name: response.headers.get(name) for name in ("Content-Security-Policy", "Strict-Transport-Security", "X-Content-Type-Options", "Permissions-Policy")}})
    return {"schemaVersion": 1, "artifactKind": "broiler-office-web-release", "status": "staged",
            "catalogVersion": lock["catalogVersion"], "channel": lock["channel"], "lockSha256": digest((json.dumps(lock, sort_keys=True, separators=(",", ":"))).encode()),
            "components": lock["components"], "disabledProducts": ["browser", "plate"], "delivery": delivery}


def promote(lock, site, previous):
    # Both promotion and rollback revalidate every immutable remote file.
    ledger = health(lock)
    pointer = site / "office-web.json"
    current = digest(pointer.read_bytes()) if pointer.exists() else "none"
    if current != previous:
        raise ValueError("Approved catalog changed; review the current pointer before retrying")
    launcher = next(r for r in lock["components"] if r["product"] == "launcher")
    catalog = {"schemaVersion": 1, "status": "approved", "channel": lock["channel"], "catalogVersion": lock["catalogVersion"],
               "launcherUrl": launcher["entryUrl"], "products": [{"id": r["product"], "url": r["entryUrl"], "version": r["version"]} for r in lock["components"] if r["product"] != "launcher"]}
    ledger["status"] = "approved"
    history = site / "catalogs" / (lock["catalogVersion"] + ".json")
    if history.exists() and read(history) != catalog:
        raise ValueError("Catalog version already exists with different contents")
    ledger_path = site / "ledgers" / (lock["catalogVersion"] + ".json")
    if ledger_path.exists() and {k: v for k, v in read(ledger_path).items() if k != "delivery"} != {k: v for k, v in ledger.items() if k != "delivery"}:
        raise ValueError("Immutable release ledger identity differs")
    write(history, catalog)
    if not ledger_path.exists():
        write(ledger_path, ledger)
    write(pointer, catalog)  # sole mutable approval pointer, written last


ROUTER = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin"><title>Broiler Office</title><p id="status">Loading the approved Office release…</p><script type="module">
try { const r = await fetch('./office-web.json', {cache:'no-store'}); if (!r.ok) throw Error(); const c = await r.json();
if(c.schemaVersion !== 1 || c.status !== 'approved' || !/^https:\/\/demo\\.broiler\\.app\/releases\/[A-Za-z0-9][A-Za-z0-9.+-]{0,79}\/$/.test(c.launcherUrl)) throw Error();
location.replace(c.launcherUrl); } catch { document.getElementById('status').textContent='No approved Office release is available yet. Please try again later.'; }
</script></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    create = sub.add_parser("lock")
    create.add_argument("--catalog-version", required=True)
    create.add_argument("--channel", choices=["preview", "stable"], default="preview")
    for product in PRODUCTS:
        create.add_argument("--" + product + "-tag", required=True)
    create.add_argument("--output", type=Path, required=True)
    for command in ("validate", "stage", "health", "promote"):
        p = sub.add_parser(command)
        p.add_argument("--lock", type=Path, required=True)
        if command in ("stage", "promote"):
            p.add_argument("--site", type=Path, required=True)
        if command == "stage":
            p.add_argument("--product", choices=list(PRODUCTS), required=True)
            p.add_argument("--cache", type=Path, required=True)
        if command == "health":
            p.add_argument("--output", type=Path, required=True)
        if command == "promote":
            p.add_argument("--expected-previous-sha256", required=True)
    args = parser.parse_args()
    if args.command == "lock":
        if args.output.exists():
            raise ValueError("Refusing to overwrite a reviewed lock")
        lock = {"schemaVersion": 1, "artifactKind": "broiler-office-web-lock", "status": "locked", "catalogVersion": args.catalog_version,
                "channel": args.channel, "components": [release_row(p, getattr(args, p + "_tag")) for p in PRODUCTS]}
        write(args.output, validate(lock))
        return
    lock = validate(read(args.lock))
    if args.command == "stage":
        stage(lock, args.product, args.site, args.cache)
    elif args.command == "health":
        write(args.output, health(lock))
    elif args.command == "promote":
        promote(lock, args.site, args.expected_previous_sha256)
    print("Verified " + args.command)


if __name__ == "__main__":
    main()
