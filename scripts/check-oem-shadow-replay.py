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
    names=['Unknown','A','B','C','D','Transition','Unmapped'];matrix=[[0]*len(names) for _ in names];unique=[[0]*len(names) for _ in names];last_fan=None
    logs=[json.loads(s) for p in sorted(output.glob('shadow-*.jsonl')) for s in p.read_text().splitlines()]
    baseline=json.loads((fixtures/'prediction-baseline.json').read_text())['captures'][version]
    predictions=('\n'.join(json.dumps(row['prediction'],sort_keys=True,separators=(',',':')) for row in logs)+'\n').encode()
    assert len(logs)==baseline['frames'] and hashlib.sha256(predictions).hexdigest()==baseline['predictionSha256'], 'thermal predictions changed from reviewed baseline'
    assert len(logs)==len(frames);missing=collections.Counter();previous=None;observed=unknown=gaps=unmapped_seconds=stable_seconds=0
    regimes=collections.Counter();unique_regimes=collections.Counter();range_comparable=above=below=inside=0
    actual_events=[];last_actual=None;predicted_events=[];last_prediction=None
    endpoint_events=[];last_endpoint=None;unmapped_since_endpoint=False
    for expected,row in zip(frames,logs):
        frame=row['input'];now=prepare.timestamp(frame['TimestampUtc'])
        def canonical(value):
            if isinstance(value,dict):return {k:canonical(v) for k,v in value.items()}
            if isinstance(value,str) and 'T' in value and (value.endswith('Z') or '+' in value):return prepare.timestamp(value)
            return value
        assert canonical(frame)==canonical(expected), 'input changed while replaying'
        assert previous is None or now>previous[0]
        p=row['prediction']['State'];a=row['actual']['State'];matrix[names.index(a)][names.index(p)]+=1
        regime=row['actual'].get('Regime')
        if regime:
            assert a=='Unmapped'
            regimes[regime['Kind']]+=1
            for key in ['CpuRange','GpuRange']:
                assert 0<=regime[key]['Max']-regime[key]['Min']<=summary['parameters']['ActualUnmappedMaximumSpan']
            if regime['Kind']=='UnmappedStableCandidate':
                assert regime['DistinctAcquisitions']>=summary['parameters']['ActualUnmappedMinimumAcquisitions']
                assert (prepare.timestamp(frame['FanSampledAtUtc'])-prepare.timestamp(regime['SinceUtc']))/10_000_000>=summary['parameters']['ActualUnmappedStableSeconds']
        invalid=False
        for name in ['CpuPackage','CpuCoreMax','Gpu','Tz01','Dtt3']:
            s=frame[name];at=prepare.timestamp(s['SampledAtUtc']) if s['SampledAtUtc'] else None
            fresh=s['Value'] is not None and 0<=s['Value']<=120 and at is not None and 0<=now-at<30_000_000
            if not fresh:missing[name]+=1;invalid=True
        if invalid:assert p=='Unknown' and row['prediction']['CpuRange'] is None and row['prediction']['GpuRange'] is None
        if previous:
            seconds=(now-previous[0])/10_000_000
            if seconds<=5:
                observed+=seconds;unknown+=seconds if previous[1]=='Unknown' else 0
                unmapped_seconds+=seconds if previous[2]=='Unmapped' else 0
                stable_seconds+=seconds if previous[3]=='UnmappedStableCandidate' else 0
            else:gaps+=seconds;last_actual=last_prediction=last_endpoint=None;unmapped_since_endpoint=False
        previous=(now,p,a,regime['Kind'] if regime else None)
        fan=prepare.timestamp(frame['FanSampledAtUtc']) if frame['FanSampledAtUtc'] else None
        fresh=fan is not None and 0<=now-fan<30_000_000 and row['actual']['RawState']!='Unknown'
        if fresh and (last_fan is None or fan>last_fan):
            unique[names.index(a)][names.index(p)]+=1;last_fan=fan
            if regime:unique_regimes[regime['Kind']]+=1
        prediction=row['prediction'];cr=prediction['CpuRange'];gr=prediction['GpuRange']
        if fresh and cr and gr:
            range_comparable+=1
            high=frame['ActualCpuLevel']>cr['Max'] or frame['ActualGpuLevel']>gr['Max']
            low=frame['ActualCpuLevel']<cr['Min'] or frame['ActualGpuLevel']<gr['Min']
            above+=high;below+=low;inside+=not high and not low
        if a in ['Unknown','Unmapped']:last_actual=None
        elif a in names[1:5]:
            if last_actual is not None and a!=last_actual:actual_events.append((last_actual,a,prepare.timestamp(row['actual']['AcceptedSinceUtc'])))
            last_actual=a
        if a=='Unknown':last_endpoint=None;unmapped_since_endpoint=False
        elif a=='Unmapped':unmapped_since_endpoint=True
        elif a in names[1:5]:
            if unmapped_since_endpoint and last_endpoint is not None and a!=last_endpoint:
                endpoint_events.append((last_endpoint,a,prepare.timestamp(row['actual']['AcceptedSinceUtc'])))
            last_endpoint=a;unmapped_since_endpoint=False
        if p=='Unknown':last_prediction=None
        elif p in names[1:5]:
            if last_prediction is not None and p!=last_prediction:predicted_events.append((last_prediction,p,now))
            last_prediction=p
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
    assert [(e['From'],e['To'],prepare.timestamp(e['actualAtUtc'])) for e in observed_events]==actual_events
    assert [(e['From'],e['To'],prepare.timestamp(e['AtUtc'])) for e in summary['transitionMetrics']['predicted']]==predicted_events
    assert [(e['From'],e['To'],prepare.timestamp(e['AtUtc'])) for e in summary['transitionMetrics']['endpointChangesAcrossUnmapped']]==endpoint_events
    if version=='v11':
        assert [(a,b,t) for a,b,t in actual_events]==[
            ('A','B',prepare.timestamp('2026-10-07T14:14:21.2519409+00:00')),
            ('B','A',prepare.timestamp('2026-10-07T14:18:42.2395310+00:00'))]
    coverage=summary['classificationCoverage'];assert coverage['knownPlateauFrames']==support
    assert coverage['knownPlateauFraction']==support/len(frames)
    assert coverage['unmappedFrames']==sum(matrix[6]);assert coverage['transitionFrames']==sum(matrix[5])
    assert coverage['unknownActualFrames']==sum(matrix[0])
    observation=summary['observedRegimes'];assert observation['frameCounts']==dict(regimes)
    assert observation['distinctAcquisitionCounts']==dict(unique_regimes)
    assert abs(observation['unmappedSeconds']-unmapped_seconds)<1e-5
    assert abs(observation['stableCandidateSeconds']-stable_seconds)<1e-5
    rc=summary['rawFanRangeComparison']
    for key,value in [('comparableFrames',range_comparable),('actualAbovePredictedUpperBound',above),('actualBelowPredictedLowerBound',below),('insideBothRanges',inside)]:assert rc[key]==value,key
    print(version,'PASS:',len(frames),'frames; independent counts, freshness, ages, epochs and hashes verified')

def main():
    p=argparse.ArgumentParser();p.add_argument('fixtures');p.add_argument('v10_output');p.add_argument('v11_output');p.add_argument('live_output',nargs='?');a=p.parse_args()
    check('v10',pathlib.Path(a.fixtures),pathlib.Path(a.v10_output));check('v11',pathlib.Path(a.fixtures),pathlib.Path(a.v11_output))
    if a.live_output:check('live-20261008',pathlib.Path(a.fixtures),pathlib.Path(a.live_output))
if __name__=='__main__':main()
