#!/usr/bin/env python3
"""Preserve archived core types for the platform-policy experiment; no new hardware IO."""
import argparse, csv, gzip, hashlib, io, json, pathlib, zipfile

def prepare(bundle_path, diagnostic_path, output):
    output.mkdir(parents=True, exist_ok=False)
    with zipfile.ZipFile(bundle_path) as bundle:
        for version in ('v10', 'v11'):
            name = f'derived/{version}/vfc_reference.csv'
            raw = bundle.read(name)
            result = []
            for r in csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))):
                cores = []
                for token in r['cpu_core_temps_c'].split('|'):
                    index, kind, value = token.split(':')
                    cores.append({'coreIndex': int(index[1:]), 'logicalProcessorIndex': -1,
                                  'coreType': kind, 'temperatureC': float(value)})
                result.append({'timestampUtc': r['timestamp_utc'], 'cores': cores})
            data = ''.join(json.dumps(r, separators=(',', ':'))+'\n' for r in result).encode()
            (output/f'{version}.cores.jsonl.gz').write_bytes(gzip.compress(data, mtime=0))
            (output/f'{version}.cores.manifest.json').write_text(json.dumps({
                'source': name, 'sourceSha256': hashlib.sha256(raw).hexdigest(),
                'rows': len(result), 'uncompressedSha256': hashlib.sha256(data).hexdigest(),
                'disclosure': 'Archived CPU row epoch approximates acquisition; no logical processor mapping is inferred.'
            }, indent=2)+'\n')
    with zipfile.ZipFile(diagnostic_path) as diagnostic:
        raw = diagnostic.read('profiles-draft.json')
        draft = json.loads(raw.decode('utf-8-sig'))
        (output/'recorded-fan-settings.json').write_text(json.dumps({
            'source': 'profiles-draft.json', 'sourceSha256': hashlib.sha256(raw).hexdigest(),
            'guiRevision': '1e30e2a1893b0542505b2ac3b3f29e4895a19213',
            'disclosure': 'Diagnostic draft, not proof these preferences were active during archived OEM captures.',
            'ac': draft['ac']['fan'], 'battery': draft['battery']['fan']
        }, indent=2, ensure_ascii=False)+'\n')

if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('bundle', type=pathlib.Path); p.add_argument('diagnostic', type=pathlib.Path)
    p.add_argument('new_directory', type=pathlib.Path)
    a = p.parse_args(); prepare(a.bundle, a.diagnostic, a.new_directory)
