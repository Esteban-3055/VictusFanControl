#!/usr/bin/env python3
"""Independent chronology/admission/MAX audit; never claims a physical improvement."""
import argparse, collections, hashlib, importlib.util, json, math, pathlib

def load_module(name, path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
replay=load_module('platform_reconcile',pathlib.Path(__file__).with_name('check-platform-thermal-replay.py'))
stamp=replay.stamp

def frame_pascal(frame):
    result={k[0].upper()+k[1:]:v for k,v in frame.items()}
    for key in ('CpuPackage','CpuCoreMax','Gpu','Tz01','Dtt1','Dtt2','Dtt3'):
        if result.get(key):
            result[key]={k[0].upper()+k[1:]:v for k,v in result[key].items()}
            if isinstance(result[key].get('Value'),str) and result[key]['Value'] in ('NaN','Infinity','-Infinity'):
                result[key]['Value']=float(result[key]['Value'])
    return result

def baseline_raw(input, fan):
    demand=fan['unifiedDemand'];tuning=fan['tuning']
    def scale(value,cold,hot):return min(100,max(0,(value-cold)/(hot-cold)*100))
    normalized=[scale(input['cpuEffectiveTemperatureC'],40,90),scale(input['gpuTemperatureC'],35,81),scale(input['cpuPackagePowerW'],0,60),
                scale(input['gpuPowerW'],0,75),input['cpuLoadPercent'],input['gpuLoadPercent']]
    gains=[demand[k+'Influence'] for k in ('cpuTemperature','gpuTemperature','cpuPower','gpuPower','cpuLoad','gpuLoad')]
    terms=[min(100,max(0,v*g/100)) for v,g in zip(normalized,gains)]
    raw_cpu=input['cpuRawControlTemperatureC'];gpu=input['gpuTemperatureC']
    cpu_floor=50 if raw_cpu>=90 else 44 if raw_cpu>=85 else 10
    gpu_floor=50 if gpu>=81 else 44 if gpu>=78 else 10
    for i,floor in ((0,cpu_floor),(1,gpu_floor)):
        if floor>10:terms[i]=max(terms[i],100 if floor==50 else 90)
    return min(50,max(10,replay.interpolate(demand['curve'],max(terms),'input'),cpu_floor,gpu_floor))

def audit(directory):
    if (directory/'evidence-sha256.json').exists():
        manifest=json.loads((directory/'evidence-sha256.json').read_text(encoding='utf-8-sig'))
        for entry in manifest['files']:
            path=(directory/entry['path']).resolve();assert path.is_relative_to(directory.resolve()),'Manifest traversal'
            blob=path.read_bytes();assert len(blob)==entry['size'] and hashlib.sha256(blob).hexdigest()==entry['sha256'],'Evidence hash mismatch'
    if (directory/'metadata.json').exists():
        metadata=json.loads((directory/'metadata.json').read_text(encoding='utf-8-sig'))
        assert hashlib.sha256((directory/'profiles.json').read_bytes()).hexdigest()==metadata['profilesSha256']
    trace=directory/'experiment.jsonl';data=trace.read_bytes();assert data.endswith(b'\n'),'Unfinished JSONL line'
    rows=[json.loads(line) for line in data.splitlines()];assert rows[0]['kind']=='session'
    summary=json.loads((directory/'summary.json').read_text(encoding='utf-8-sig'))
    assert rows[-1]['kind']=='completed' and rows[-1]['data']==summary,'Missing terminal record or mismatched summary'
    session=rows[0]['data'];assert not session['normalAutomaticPromoted'] and not summary['physicalPassClaimed']
    settings=session['settings'];fan=session['fan'];admission=replay.Admission(True,True,settings)
    telemetry={};previous=None;gaps=[];qual=0;decisions=[];dispatch=[];last_level=None;changes=0
    blocks=collections.defaultdict(lambda:dict(samples=0,decisions=0,cpu=[],gpu=[],loadSamples=0,aboveBaselineSamples=0,controller=None))
    closed=False
    variants={};variant_last={}
    for name in ('tz01','dtt3','both','both-retention','both-warmer-thresholds','both-colder-thresholds'):
        tz_shift=5 if name=='both-warmer-thresholds' else -5 if name=='both-colder-thresholds' else 0
        dtt_shift=3 if tz_shift>0 else -3 if tz_shift<0 else 0
        shifted=dict(settings,tz01Curve=[dict(p,temperatureC=p['temperatureC']+tz_shift) for p in settings['tz01Curve']],
                     dtt3Curve=[dict(p,temperatureC=p['temperatureC']+dtt_shift) for p in settings['dtt3Curve']])
        variants[name]=(replay.Admission(name!='dtt3',name!='tz01',shifted),shifted)
    for row in rows:
        kind,d=row['kind'],row['data']
        if kind=='closed':closed=True
        if kind=='telemetry':
            f=frame_pascal(d['frame']);now=stamp(f['TimestampUtc']);s=d['snapshot'];b=blocks[d['stage']['index']];b['controller']=d['stage']['controller']
            if previous is not None:
                assert now>previous,'Non-increasing snapshots'
                if now-previous>30_000_000:gaps.append((previous,now))
            previous=now;available=admission.evaluate(f);assert available==d['admission']['available'],'Source admission mismatch'
            qual+=available;telemetry[now]=d;b['samples']+=1
            for key,field in [('cpu','cpuControlTemperatureC'),('gpu','gpuTemperatureC')]:
                if isinstance(s[field],(int,float)) and math.isfinite(s[field]):b[key].append(s[field])
            b['loadSamples']+=bool((s['cpuLoadPercent'] or 0)>=50 or (s['cpuPackagePowerW'] or 0)>=25 or (s['gpuPowerW'] or 0)>=35)
            if available:
                tz=replay.interpolate(settings['tz01Curve'],f['Tz01']['Value']);dtt=replay.interpolate(settings['dtt3Curve'],f['Dtt3']['Value'])
                assert math.isclose(max(tz,dtt),d['admission']['demandLevel'],abs_tol=1e-9)
        elif kind=='decision':
            now=stamp(d['timestamp']);t=telemetry[now];assert t['admission']['available'] and t['source']=='Ac'
            assert d['stage']['index']==t['stage']['index'] and d['stage']['custom'] and not closed
            snapshot=t['snapshot'];input=d['input'];cores=snapshot['cpuCoreTemperatures']
            assert snapshot['isComplete'] and len(cores)==snapshot['cpuExpectedPhysicalCoreCount']==14
            source=fan['tuning']['cpuTemperatureSource']
            if isinstance(source,str):source={'PackageOrHottestCore':0,'CoreAverage':1,'PerformanceCoreAverage':2,'HottestPerformanceCoresAverage':3}[source]
            raw_cpu=max(snapshot['cpuTemperatureC'],max(c['temperatureC'] for c in cores))
            selected=[c['temperatureC'] for c in cores if source==1 or c['coreType']=='Performance']
            if source==3:selected=sorted(selected,reverse=True)[:fan['tuning']['hottestPerformanceCoreCount']]
            cpu=raw_cpu if source==0 else sum(selected)/len(selected)
            assert math.isclose(input['cpuEffectiveTemperatureC'],cpu,abs_tol=1e-9) and input['cpuRawControlTemperatureC']==raw_cpu
            for key in ('cpuPackagePowerW','cpuLoadPercent','gpuTemperatureC','gpuPowerW','gpuLoadPercent'):
                assert input[key]==snapshot[key] and input[key] is not None and math.isfinite(input[key]),'Missing or changed policy input'
            raw=baseline_raw(input,fan);assert math.isclose(raw,d['baseline']['rawDemandLevel'],abs_tol=1e-9),'Baseline MAX changed'
            extra=d['supplemental']
            if d['stage']['controller']=='baseline':assert extra is None
            else:
                expected=min(t['admission']['demandLevel'],d['physicalLevelBeforeDecision'] if d['physicalLevelBeforeDecision'] is not None else d['baseline']['equalFanLevel'])
                assert math.isclose(extra,expected,abs_tol=1e-9),'Physical retention bound changed'
            a=d['active'];b=d['baseline'];assert 10<=a['equalFanLevel']<=50 and a['thermalOverride']==b['thermalOverride']
            assert math.isclose(a['rawDemandLevel'],max(raw,extra or 0),abs_tol=1e-9),'Supplement MAX mismatch'
            assert {v['name'] for v in d['comparisons']}=={'tz01','dtt3','both','both-retention','both-warmer-thresholds','both-colder-thresholds'}
            f=frame_pascal(t['frame'])
            for v in d['comparisons']:
                name=v['name'];va,vs=variants[name];available=va.evaluate(f)
                assert v['observation']['available']==available, ('variant admission',name)
                if available:
                    tz=replay.interpolate(vs['tz01Curve'],f['Tz01']['Value']) if name!='dtt3' else None
                    dtt=replay.interpolate(vs['dtt3Curve'],f['Dtt3']['Value']) if name!='tz01' else None
                    extra=max(tz or 0,dtt or 0)
                    assert math.isclose(v['observation']['demandLevel'],extra,abs_tol=1e-9)
                    if name=='both-retention':extra=min(extra,variant_last.get(name,b['equalFanLevel']))
                    assert math.isclose(v['extra'],extra,abs_tol=1e-9)
                    candidate=v['candidate'];assert candidate is not None
                    assert math.isclose(candidate['rawDemandLevel'],max(raw,extra),abs_tol=1e-9), ('variant raw',name)
                    assert candidate['thermalOverride']==b['thermalOverride'] and 10<=candidate['equalFanLevel']<=50
                    variant_last[name]=candidate['equalFanLevel']
                else:
                    assert v['extra'] is None and v['candidate'] is None
                    # Before a source can qualify, the generated policy uses the
                    # neutral baseline input; its initial level equals baseline.
                    variant_last[name]=b['equalFanLevel']
            decisions.append(d);block=blocks[d['stage']['index']];block['decisions']+=1;block['aboveBaselineSamples']+=a['equalFanLevel']>b['equalFanLevel']
        elif kind=='dispatch-result':
            now=stamp(d['timestamp']);decision=next((x for x in reversed(decisions) if stamp(x['timestamp'])==now),None)
            assert decision is not None and decision['stage']['index']==d['stage']['index']
            result=d['result'];assert result['equalFanLevel']==decision['active']['equalFanLevel']
            if result['action'] in ('EnterCustomAndApply','ApplyChangedLevel','HoldCustom'):
                if result['equalFanLevel']!=last_level and result['action']!='HoldCustom':changes+=1
                last_level=result['equalFanLevel']
            dispatch.append(d)
    assert len(telemetry)==summary['telemetryRows'] and qual==summary['qualifiedRows'] and len(decisions)==summary['decisions']
    assert changes==summary['appliedChanges']
    assert dict(collections.Counter(d['stage']['controller'] for d in decisions))==summary['decisionsByController']
    assert summary['cpuMaximumC']==max((v for b in blocks.values() for v in b['cpu']),default=0)
    assert summary['gpuMaximumC']==max((v for b in blocks.values() for v in b['gpu']),default=0)
    if summary['protocolComplete']:
        assert all(blocks[i]['decisions']>0 for i in range(4)) and blocks[4]['samples']>0 and blocks[-1]['samples']>0,'Incomplete ABBA coverage'
        assert previous-min(telemetry)>=2550*10_000_000,'Shortened protocol claimed complete'
    outcomes={}
    for index,b in blocks.items():
        outcome={k:v for k,v in b.items() if k not in ('cpu','gpu')}
        for key in ('cpu','gpu'):
            values=sorted(b[key]);outcome[key+'MaximumC']=max(values,default=None);outcome[key+'P95SampleC']=values[int((len(values)-1)*.95)] if values else None
        outcomes[str(index)]=outcome
    report=dict(schemaVersion=1,traceSha256=hashlib.sha256(data).hexdigest(),rows=len(rows),telemetry=len(telemetry),decisions=len(decisions),dispatchResults=len(dispatch),gapsOver3Seconds=len(gaps),
                cleanup=summary['cleanup'],protocolComplete=summary['protocolComplete'],physicalExecution=session['physicalExecution'],blocks=outcomes,
                conclusion='Integrity/admission/MAX audit only. Scheduled workload labels are not proof of matched load. Compare measured power/load and cooling windows before causal or acoustic claims.')
    (directory/'independent-audit.json').write_text(json.dumps(report,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    print(f'Independent physical experiment audit: PASS ({len(telemetry)} snapshots; {len(decisions)} decisions; {len(dispatch)} dispatch results).')
    return report

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('directory',type=pathlib.Path);args=parser.parse_args();audit(args.directory)
