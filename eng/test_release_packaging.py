"""Run the actual release packager with fixture outputs and a local gh stub."""
import base64
import hashlib
import json
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PRODUCT = ROOT.name.split('.')[1]
SLUG = PRODUCT.lower()
PID = 'cell' if PRODUCT == 'Cells' else 'writer'
VERSION = '0.1.0-preview.999'
BASH = 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else shutil.which('bash')


@unittest.skipUnless(BASH and Path(BASH).exists(), 'Bash is required')
class ReleasePackagingTests(unittest.TestCase):
    def exercise(self, targets, draft=True, missing=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'eng/web').mkdir(parents=True)
            for name in ('release-draft.sh', 'write-component-manifest.py'):
                shutil.copy2(ROOT/'eng'/name, root/'eng'/name)
            shutil.copy2(ROOT/'eng/web/artifact.py', root/'eng/web/artifact.py')
            subprocess.run(['git', 'init', '-q', str(root)], check=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '--allow-empty', '-qm', 'fixture'], check=True)
            commit = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
            artifacts = root/'artifacts'; artifacts.mkdir()
            for target, rid, head in [('WINDOWS', 'win-x64', 'Windows'), ('LINUX', 'linux-x64', 'Linux')]:
                if target not in targets:
                    continue
                for variant in ('self-contained', 'framework-dependent'):
                    folder = artifacts/f'broiler-{SLUG}-{rid}-{variant}-{VERSION}'
                    folder.mkdir()
                    (folder/f'Broiler.{PRODUCT}.{head}{".exe" if head == "Windows" else ""}').write_bytes(b'executable')
            if 'ANDROID' in targets:
                folder = artifacts/f'broiler-{SLUG}-android-{VERSION}'; folder.mkdir()
                for suffix in ('.aab', '-arm64.apk'):
                    (folder/f'Broiler.{PRODUCT}-{VERSION}{suffix}').write_bytes(b'signed fixture')
            if 'WEB' in targets:
                source = root/'wwwroot'; (source/'_framework').mkdir(parents=True)
                (source/'main.js').write_text('export const officeInputProtocolVersion = 1;')
                (source/'_framework/dotnet.js').write_text('runtime')
                (source/'_framework/App.wasm').write_bytes(b'\0asm')
                mapping = {'imports': {'./main.js': './main.js'}, 'integrity': {'./main.js': 'sha256-'+base64.b64encode(hashlib.sha256((source/'main.js').read_bytes()).digest()).decode()}}
                (source/'index.html').write_text('<script type="importmap">'+json.dumps(mapping)+'</script><script type="module" src="main.js"></script>')
                (source/'broiler.graphics.webassembly.js').write_text('replay')
                spec = importlib.util.spec_from_file_location('artifact', root/'eng/web/artifact.py')
                artifact = importlib.util.module_from_spec(spec); spec.loader.exec_module(artifact)
                artifact.pack(source, artifacts/f'broiler-{SLUG}-web-{VERSION}', dict(
                    product=PID, productName=PRODUCT, repository=f'Broiler-Platform/Broiler.{PRODUCT}',
                    version=VERSION, tag=f'{SLUG}-v{VERSION}', commit=commit,
                    sdk='10.0.403', workload='10.0.114/10.0.100', workloadSet='10.0.403',
                    runtimeMode='trimmed-interpreter', publishTrimmed=True, runAotCompilation=False,
                    dirty=False, verificationOnly=False))
            if missing:
                for path in artifacts.rglob('*.exe'):
                    path.unlink()
            stub = root/'stub'; stub.mkdir()
            (stub/'gh').write_text('''#!/usr/bin/env bash
printf '%s\\n' "$@" > "$CAPTURE/args"
while [ "$#" -gt 0 ]; do
  if [ "$1" = --notes-file ]; then cp "$2" "$CAPTURE/notes"; fi
  case "$1" in *.json|*.txt) if [ -f "$1" ]; then cp "$1" "$CAPTURE/"; fi ;; esac
  shift
done
''')
            (stub/'gh').chmod(0o755)
            env = dict(os.environ, PATH=str(stub)+os.pathsep+os.environ['PATH'], CAPTURE=str(root), CREATE_DRAFT=str(draft).lower())
            env.update({f'PUBLISH_{key}': str(key in targets).lower() for key in ('WINDOWS','LINUX','ANDROID','WEB')})
            result = subprocess.run([BASH, 'eng/release-draft.sh', VERSION, 'artifacts'], cwd=root, env=env, capture_output=True, text=True)
            if missing or not targets:
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((root/'args').exists())
                return
            self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
            args = (root/'args').read_text()
            self.assertEqual('--draft' in args, draft)
            notes = (root/'notes').read_text()
            for target, marker in [('WINDOWS','-win-x64-'),('LINUX','-linux-x64-'),('ANDROID','-arm64.apk'),('WEB','-browser-wasm.zip')]:
                self.assertEqual(marker in args, target in targets)
                self.assertEqual(marker in notes, target in targets)
            self.assertTrue((root/'component-release.json').is_file())
            self.assertTrue((root/'SHA256SUMS.txt').is_file())

    def test_each_target_alone(self):
        for target in ('WINDOWS','LINUX','ANDROID','WEB'):
            with self.subTest(target=target):
                self.exercise({target})

    def test_all_targets_published(self):
        self.exercise({'WINDOWS','LINUX','ANDROID','WEB'}, draft=False)

    def test_missing_selected_output_fails_before_release(self):
        self.exercise({'WINDOWS'}, missing=True)

    def test_empty_selection_fails(self):
        self.exercise(set())
