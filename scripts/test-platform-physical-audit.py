#!/usr/bin/env python3
"""Exercise the independent audit against deliberate evidence corruption."""
import copy, importlib.util, json, pathlib, sys, tempfile
spec=importlib.util.spec_from_file_location('audit',pathlib.Path(__file__).with_name('check-platform-physical-experiment.py'))
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
source=pathlib.Path(sys.argv[1]);rows=[json.loads(l) for l in (source/'experiment.jsonl').read_text(encoding='utf-8-sig').splitlines()]
summary=json.loads((source/'summary.json').read_text(encoding='utf-8-sig'))
def decision(trace):return next(r['data'] for r in trace if r['kind']=='decision' and all(v['candidate'] is not None for v in r['data']['comparisons']))
def raw(trace):decision(trace)['active']['rawDemandLevel']-=1
def variant(trace):decision(trace)['comparisons'][0]['extra']+=1
def admission(trace):
    data=next(r['data'] for r in trace if r['kind']=='telemetry' and r['data']['admission']['available'])
    data['frame']['tz01']['sampledAtUtc']='2099-01-01T00:00:00+00:00'
def dispatch(trace):next(r['data'] for r in trace if r['kind']=='dispatch-result')['result']['equalFanLevel']+=1
def missing(trace):
    timestamp=next(r['data']['timestamp'] for r in trace if r['kind']=='decision')
    next(r['data'] for r in trace if r['kind']=='telemetry' and r['data']['snapshot']['timestamp']==timestamp)['snapshot']['cpuLoadPercent']=None
def truncate(trace):trace.pop()
for mutate in (raw,variant,admission,dispatch,missing,truncate):
    with tempfile.TemporaryDirectory(prefix='vfc-audit-negative-') as tmp:
        root=pathlib.Path(tmp);changed=copy.deepcopy(rows);mutate(changed)
        (root/'experiment.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in changed),encoding='utf-8')
        (root/'summary.json').write_text(json.dumps(summary),encoding='utf-8')
        try:audit.audit(root)
        except (AssertionError,ValueError,KeyError,TypeError):continue
        raise AssertionError('Corruption accepted: '+mutate.__name__)
print('Independent physical audit negative controls: PASS (6 corruptions rejected).')
