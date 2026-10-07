#!/usr/bin/env python3
"""Validate replay evidence and independently reconcile the diagnostic outputs."""
import argparse, collections, gzip, hashlib, json, pathlib, sys
sys.dont_write_bytecode=True
from importlib.util import spec_from_file_location, module_from_spec
spec=spec_from_file_location('prepare',pathlib.Path(__file__).with_name('prepare-oem-shadow-replay.py'))
prepare=module_from_spec(spec);spec.loader.exec_module(prepare)

def check(version,fixtures,output):
    manifest=json.loads((fixtures/(version+'.manifest.json')).read_text());data=(fixtures/(version+'.jsonl.gz')).read_bytes()
    assert hashlib.sha256(data).hexdigest()==manifest['sha256'], 'compressed hash'
    plain=gzip.decompress(data);assert hashlib.sha256(plain).hexdigest()==manifest['uncompressedSha256'], 'uncompressed hash'
    frames=[json.loads(s) for s in plain.splitlines()];assert len(frames)==manifest['samples']
    summary=json.loads((output/'summary.json').read_text());assert summary['samples']==len(frames)
    assert summary['goForFanControl'] is False and summary['fanWriteAuthority'] is False
    names=['Unknown','A','B','C','D','Transition'];matrix=[[0]*6 for _ in names];unique=[[0]*6 for _ in names];last_fan=None
    logs=[json.loads(s) for p in sorted(output.glob('shadow-*.jsonl')) for s in p.read_text().splitlines()]
    assert len(logs)==len(frames);missing=collections.Counter();previous=None;observed=unknown=gaps=0
    for expected,row in zip(frames,logs):
        frame=row['input'];now=prepare.timestamp(frame['TimestampUtc'])
        def canonical(value):
            if isinstance(value,dict):return {k:canonical(v) for k,v in value.items()}
            if isinstance(value,str) and 'T' in value and (value.endswith('Z') or '+' in value):return prepare.timestamp(value)
            return value
        assert canonical(frame)==canonical(expected), 'input changed while replaying'
        assert previous is None or now>previous[0]
        p=row['prediction']['State'];a=row['actual']['State'];matrix[names.index(a)][names.index(p)]+=1
        invalid=False
        for name in ['CpuPackage','CpuCoreMax','Gpu','Tz01','Dtt3']:
            s=frame[name];at=prepare.timestamp(s['SampledAtUtc']) if s['SampledAtUtc'] else None
            fresh=s['Value'] is not None and 0<=s['Value']<=120 and at is not None and 0<=now-at<30_000_000
            if not fresh:missing[name]+=1;invalid=True
        if invalid:assert p=='Unknown' and row['prediction']['CpuRange'] is None and row['prediction']['GpuRange'] is None
        if previous:
            seconds=(now-previous[0])/10_000_000
            if seconds<=5:observed+=seconds;unknown+=seconds if previous[1]=='Unknown' else 0
            else:gaps+=seconds
        previous=(now,p)
        fan=prepare.timestamp(frame['FanSampledAtUtc']) if frame['FanSampledAtUtc'] else None
        fresh=fan is not None and 0<=now-fan<30_000_000 and row['actual']['RawState']!='Unknown'
        if fresh and (last_fan is None or fan>last_fan):unique[names.index(a)][names.index(p)]+=1;last_fan=fan
        comparable=a in names[1:5] and p in names[1:5]
        assert row['matchState']==(a==p if comparable else None)
    assert matrix==summary['confusionMatrix']['counts'];assert unique==summary['uniqueAcquisitionConfusionMatrix']['counts']
    assert sum(map(sum,unique))==summary['uniqueFreshFanAcquisitions']
    for key,value in [('observedSeconds',observed),('unknownPredictionSeconds',unknown),('unobservedGapSeconds',gaps)]:assert abs(summary[key]-value)<1e-5,key
    for key,source in [('cpuPackage','CpuPackage'),('cpuCoreMax','CpuCoreMax'),('gpu','Gpu'),('tz01','Tz01'),('dtt3','Dtt3')]:assert summary['missingOrStaleFrames'][key]==missing[source]
    support=sum(sum(matrix[i]) for i in range(1,5));correct=sum(matrix[i][i] for i in range(1,5))
    assert abs(summary['knownStateAccuracy']-correct/support)<1e-12
    for i in range(1,5):
        recall=matrix[i][i]/sum(matrix[i]) if sum(matrix[i]) else None
        assert summary['perState'][names[i]]['recall']==recall
    observed_events=summary['transitionMetrics']['observed']
    if version=='v11':
        assert [(e['From'],e['To']) for e in observed_events]==[('A','B'),('B','A')]
        for e,epoch in zip(observed_events,['2026-10-07T14:14:21.2519409+00:00','2026-10-07T14:18:42.2395310+00:00']):
            assert prepare.timestamp(e['actualAtUtc'])==prepare.timestamp(epoch),'actual authoritative epoch'
            assert abs(e['errorSecondsPositiveIsLate']-(prepare.timestamp(e['predictedAtUtc'])-prepare.timestamp(epoch))/10_000_000)<1e-7
        assert summary['perState']['C']['recall'] is None and summary['perState']['D']['recall'] is None
    else:assert len(observed_events)==5
    print(version,'PASS:',len(frames),'frames; independent counts, freshness, ages, epochs and hashes verified')

def main():
    p=argparse.ArgumentParser();p.add_argument('fixtures');p.add_argument('v10_output');p.add_argument('v11_output');a=p.parse_args()
    check('v10',pathlib.Path(a.fixtures),pathlib.Path(a.v10_output));check('v11',pathlib.Path(a.fixtures),pathlib.Path(a.v11_output))
if __name__=='__main__':main()
