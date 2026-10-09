#!/usr/bin/env python3
"""Causal join of original reference/thermal CSV; no forward fill beyond age policy."""
import argparse, csv, datetime as dt, gzip, hashlib, io, json, math, pathlib, re, zipfile

def number(value):
    if not value: return None
    result = float(value.replace(',', '.'))
    return result if math.isfinite(result) else None

def timestamp(value):
    parsed=dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None: raise ValueError('Source timestamp must have a UTC offset')
    delta=parsed-dt.datetime(1970,1,1,tzinfo=dt.timezone.utc)
    fraction=re.search(r'\.(\d+)',value)
    digits=fraction.group(1) if fraction else ''
    if len(digits)>7: raise ValueError('Timestamp exceeds 100 ns precision')
    return (delta.days*86400+delta.seconds)*10_000_000+delta.microseconds*10+(int(digits[6]) if len(digits)==7 else 0)

def rows(data):
    result = list(csv.DictReader(io.StringIO(data.decode('utf-8-sig'))))
    times = [timestamp(r['timestamp_utc']) for r in result]
    if any(b <= a for a, b in zip(times, times[1:])): raise ValueError('CSV timestamps not strictly increasing')
    return result, times

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('bundle');parser.add_argument('new_directory')
    args = parser.parse_args(); out = pathlib.Path(args.new_directory);out.mkdir(exist_ok=False, parents=True)
    with zipfile.ZipFile(args.bundle) as bundle:
        for version in ('v10', 'v11'):
            paths = [f'derived/{version}/{name}.csv' for name in ('vfc_reference','thermal_fast')]
            raw = [bundle.read(p) for p in paths]; reference, times = rows(raw[0]); thermal, thermal_times = rows(raw[1]); cursor=-1
            content=[]
            for ref, now in zip(reference,times):
                while cursor+1<len(thermal_times) and thermal_times[cursor+1]<=now: cursor+=1
                t=thermal[cursor] if cursor>=0 else {}; epoch=t.get('timestamp_utc'); frame={'TimestampUtc':ref['timestamp_utc']}
                def source(name,key,row,at):frame[name]={'Value':number(row.get(key)), 'SampledAtUtc':at}
                for name,key in (('CpuPackage','cpu_package_temp_c'),('CpuCoreMax','cpu_core_max_temp_c'),('Gpu','gpu_temp_c')):
                    source(name,key,ref,ref['timestamp_utc'])
                for name,key in (('Tz01','tz01_c'),('Dtt1','dtt1_temp_c'),('Dtt2','dtt2_temp_c'),('Dtt3','dtt3_temp_c')):
                    source(name,key,t,epoch)
                for name,key in (('ActualCpuLevel','cpu_fan_speed_level'),('ActualGpuLevel','gpu_fan_speed_level')):
                    v=number(ref.get(key));frame[name]=int(v) if v is not None else None
                frame['FanSampledAtUtc']=ref.get('fan_sampled_at_utc') or None
                for name,key in (('CpuPowerW','cpu_package_power_w'),('GpuPowerW','gpu_power_w'),('CpuLoadPercent','cpu_load_pct'),('GpuLoadPercent','gpu_load_pct')):
                    frame[name]=number(ref.get(key))
                content.append(json.dumps(frame,separators=(',',':'),allow_nan=False)+'\n')
            data=''.join(content).encode(); compressed=gzip.compress(data,mtime=0); path=out/(version+'.jsonl.gz');path.write_bytes(compressed)
            manifest={'schemaVersion':1,'version':version,'samples':len(reference),'thermalSamples':len(thermal),
                'originalSources':{p:hashlib.sha256(b).hexdigest() for p,b in zip(paths,raw)},
                'sha256':hashlib.sha256(compressed).hexdigest(),'uncompressedSha256':hashlib.sha256(data).hexdigest(),
                'join':'latest thermal row <= reference row; no future samples; every reference row retained',
                'sourceEpochLimitation':'Historical row timestamps approximate acquisition. True per-query epochs are unavailable. Live capture uses query-start epochs.'}
            (out/(version+'.manifest.json')).write_text(json.dumps(manifest,indent=2)+'\n')
            print(version,len(reference),'frames')

if __name__=='__main__':main()
