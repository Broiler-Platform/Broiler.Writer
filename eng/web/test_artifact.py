"""Producer archive integrity and failure-path tests; no network or app rebuilds."""
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("web_artifact", ROOT / "artifact.py")
artifact = importlib.util.module_from_spec(spec)
spec.loader.exec_module(artifact)


class ProducerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.web = self.root / "wwwroot"
        (self.web / "_framework").mkdir(parents=True)
        files = {"main.hash.js": b"import './_framework/dotnet.js';", "_framework/dotnet.js": b"runtime",
            "_framework/App.hash.wasm": b"\0asm", "broiler.graphics.webassembly.js": b"replay",
            "fonts/font.woff2": b"font", "media/picture.png": b"picture", ".nojekyll": b"", "main.hash.js.br": b"compressed"}
        for name,data in files.items():
            path = self.web / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        mapping = {"imports":{"./main.js":"./main.hash.js"},"integrity":{"./main.js":"sha256-"+base64.b64encode(hashlib.sha256(files["main.hash.js"]).digest()).decode()}}
        self.html = '<script type="importmap">'+json.dumps(mapping)+'</script><script type="module" src="main.hash.js"></script>'
        (self.web / "index.html").write_text(self.html)
        self.identity = dict(product="writer",productName="Writer",repository="Broiler-Platform/Broiler.Writer",version="0.1.0-preview.1",tag="writer-web-v0.1.0-preview.1",commit="a"*40,
            sdk=artifact.SDK,workload=artifact.WORKLOAD,workloadSet="10.403.0",runtimeMode=artifact.MODE,publishTrimmed=True,runAotCompilation=False,dirty=False,verificationOnly=False)

    def package(self, identity=None):
        metadata = artifact.pack(self.web, self.root / "release", copy.deepcopy(identity or self.identity))
        manifest = json.loads(metadata.read_text())
        return metadata.parent / manifest["archive"], metadata

    def rewrite(self, archive, metadata, entries):
        with zipfile.ZipFile(archive,"w",zipfile.ZIP_DEFLATED) as target:
            for name,data in entries:
                target.writestr(name,data)
        manifest=json.loads(metadata.read_text())
        manifest.update(archiveSize=archive.stat().st_size,archiveSha256=artifact.digest(archive.read_bytes()))
        metadata.write_text(json.dumps(manifest))

    def test_complete_bytes_survive_archive_and_extraction(self):
        archive,metadata=self.package()
        extracted=self.root / "extracted"
        artifact.verify(archive,metadata,extracted,require_release=True)
        self.assertEqual(artifact.tree(self.web),artifact.tree(extracted / "wwwroot"))

    def test_no_overwrite(self):
        self.package()
        with self.assertRaises(ValueError): self.package()

    def test_missing_entry_fails(self):
        (self.web / "main.hash.js").unlink()
        with self.assertRaises(ValueError): self.package()

    def test_fingerprinted_runtime_alias_is_preserved(self):
        (self.web/'_framework/dotnet.js').rename(self.web/'_framework/dotnet.hash.js')
        mapping={"imports":{"./_framework/dotnet.js":"./_framework/dotnet.hash.js"}}
        (self.web/'index.html').write_text('<script type="importmap">'+json.dumps(mapping)+'</script><script type="module" src="main.hash.js"></script>')
        archive,metadata=self.package()
        artifact.verify(archive,metadata)
        self.assertFalse((self.web/'_framework/dotnet.js').exists())

    def test_unresolved_fingerprint_fails(self):
        (self.web / "index.html").write_text(self.html + '#[.{fingerprint}]')
        with self.assertRaises(ValueError): self.package()

    def test_integrity_mismatch_fails(self):
        (self.web / "main.hash.js").write_bytes(b"changed")
        with self.assertRaises(ValueError): self.package()

    def test_archive_tampering_fails(self):
        archive,metadata=self.package()
        archive.write_bytes(archive.read_bytes()+b"tamper")
        with self.assertRaises(ValueError): artifact.verify(archive,metadata)

    def test_per_file_digest_still_checked_after_outer_digest_change(self):
        archive,metadata=self.package()
        with zipfile.ZipFile(archive) as source: entries=[(name,source.read(name))for name in source.namelist()]
        entries=[(name,b"replaY" if name.endswith('broiler.graphics.webassembly.js') else data)for name,data in entries]
        self.rewrite(archive,metadata,entries)
        with self.assertRaises(ValueError): artifact.verify(archive,metadata)

    def test_traversal_fails(self):
        archive,metadata=self.package()
        self.rewrite(archive,metadata,[("wwwroot/../../escape",b"escape")])
        with self.assertRaises(ValueError): artifact.verify(archive,metadata)

    def test_missing_inventory_members_fail(self):
        archive,metadata=self.package()
        with zipfile.ZipFile(archive) as source: entries=[(name,source.read(name))for name in source.namelist()if not name.endswith('.woff2')]
        self.rewrite(archive,metadata,entries)
        with self.assertRaises(ValueError): artifact.verify(archive,metadata)

    def test_aot_cannot_masquerade_as_initial_cohort(self):
        self.identity["runAotCompilation"]=True
        with self.assertRaises(ValueError): self.package()

    def test_dirty_release_fails_but_local_verification_is_explicit(self):
        self.identity["dirty"]=True
        with self.assertRaises(ValueError): self.package()
        self.identity["verificationOnly"]=True
        archive,metadata=self.package()
        self.assertFalse(json.loads(metadata.read_text())["releaseEligible"])
        with self.assertRaises(ValueError): artifact.verify(archive,metadata,require_release=True)

if __name__ == "__main__": unittest.main()
