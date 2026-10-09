#!/usr/bin/env python3
"""Bind the standalone EXE to the same verified release ZIP and source commit."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--installer', type=Path, required=True)
p.add_argument('--payload', type=Path, required=True)
p.add_argument('--source-head', required=True)
p.add_argument('--verify', action='store_true')
p.add_argument('--output', type=Path)
a = p.parse_args()
if not re.fullmatch('[0-9a-f]{40}', a.source_head):
    raise SystemExit('Invalid source identity')
with zipfile.ZipFile(a.payload) as archive:
    manifest = json.loads(archive.read('PRODUCT-GUI-MANIFEST.json').decode('utf-8-sig'))
version = manifest['version']
if manifest['sourceHead'] != a.source_head or a.installer.name != f'VictusFanControl-{version}-Setup-win-x64.exe':
    raise SystemExit('Installer/payload source or version mismatch')
data = a.installer.read_bytes()
if not data.startswith(b'MZ') or not 0 < len(data) <= 256 * 1024 * 1024:
    raise SystemExit('Invalid Windows installer')
digest = hashlib.sha256(data).hexdigest()
record = dict(schemaVersion=1, kind='VictusFanControl.WindowsInstaller', version=version,
              sourceHead=a.source_head, file=a.installer.name, size=len(data), sha256=digest,
              payloadSha256=hashlib.sha256(a.payload.read_bytes()).hexdigest(),
              selfContainedInstaller=True, appDesktopRuntime='Microsoft.WindowsDesktop.App 8 x64',
              codeSigned=False, preservesProfiles=True, installHardwareWrites=False)
sidecar = a.installer.with_suffix('.exe.sha256')
provenance = a.installer.with_suffix('.exe.provenance.json')
checksum = f'{digest}  {a.installer.name}\n'
if a.verify:
    if json.loads(provenance.read_text()) != record or sidecar.read_text() != checksum:
        raise SystemExit('Installer provenance/checksum mismatch')
else:
    sidecar.write_text(checksum, encoding='utf-8')
    provenance.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
if a.output:
    for file in (a.installer, sidecar, provenance):
        shutil.copyfile(file, a.output / file.name)
print(f'Windows installer assets: PASS ({version}; {len(data)} bytes; SHA-256 {digest})')
