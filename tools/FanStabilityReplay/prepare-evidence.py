"""Collect local diagnostics for offline replay, keeping original traces private."""
import argparse, collections, csv, hashlib, io, json, statistics, zipfile
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--upload-dir',type=Path,required=True)
p.add_argument('--additional-dir',type=Path)
p.add_argument('--historical-dir',type=Path)
p.add_argument('--output-dir',type=Path,required=True)
a=p.parse_args();a.output_dir.mkdir(parents=True,exist_ok=True)
sessions={};older=[];seen=set()
for directory in filter(None,[a.upload_dir,a.additional_dir]):
 for path in sorted(directory.glob('VictusFanControl-diagnostico*.zip')):
  digest=hashlib.sha256(path.read_bytes()).hexdigest()
  if digest in seen:continue
  seen.add(digest)
  with zipfile.ZipFile(path) as z:
   state=json.loads(z.read('gui-state.json'))
   if state.get('Target')!='HP-8C40-9D0R1LA-F18':raise ValueError('Wrong or missing target: '+path.name)
   events=z.read('events-tail.log').decode('utf-8-sig').splitlines()
   source={'file':path.name,'sha256':digest}
   if 'telemetry-tail.jsonl' not in z.namelist():older.append({**source,'events':len(events),'scope':'older-cumulative-log-no-telemetry'});continue
   rows=[json.loads(s) for s in z.read('telemetry-tail.jsonl').decode('utf-8-sig').splitlines() if s]
   if not rows:continue
   sid=rows[0]['sessionId'];g=sessions.setdefault(sid,{'rows':{},'events':set(),'archives':[]})
   g['archives'].append(source);g['events'].update(events)
   for row in rows:
    if row['sessionId']!=sid:raise ValueError('Mixed sessions')
    s=row['snapshot'];t=s['Timestamp']
    if t in g['rows'] and g['rows'][t]!=s:raise ValueError('Conflicting samples at '+t)
    g['rows'][t]=s

def metrics(ds):
 levels=[x['decision']['EqualFanLevel'] for x in ds if x['decision'].get('EqualFanLevel') is not None]
 changes=[b-a for a,b in zip(levels,levels[1:]) if b!=a]
 return {'decisions':len(levels),'commandChanges':len(changes),'directionReversals':sum(x*y<0 for x,y in zip(changes,changes[1:])),'totalVariationLevels':sum(abs(x) for x in changes),'range':[min(levels),max(levels)] if levels else None}
markers={'PRODUCT AUTOMATIC ACTIVATED WITH PERFORMANCE: ':'start','PRODUCT AUTOMATIC CURVE APPLIED: ':'curve','PRODUCT AUTOMATIC TUNING APPLIED: ':'tuning','PRODUCT AUTOMATIC SOURCE TRANSITION CONFIRMED: ':'source'}
dataset=[];summary=[]
for sid,g in sessions.items():
 snapshots=sorted(g['rows'].values(),key=lambda s:s['Timestamp']);ds=[];configs=[]
 for line in sorted(g['events']):
  marker='PRODUCT AUTOMATIC DECISION: '
  if marker in line:
   d=json.loads(line.split(marker,1)[1]);d['eventTime']=line.split('  ')[0];ds.append(d)
  for marker,kind in markers.items():
   if marker in line:configs.append({'time':line.split('  ')[0],'kind':kind,'data':json.loads(line.split(marker,1)[1])})
 runs=collections.defaultdict(list)
 for d in ds:runs[d['automaticSessionId']].append(d)
 complete=[s for s in snapshots if s.get('IsComplete')]
 def bounds(k):
  vs=[s[k] for s in complete if s.get(k) is not None];return [min(vs),max(vs)] if vs else None
 summary.append({'session':sid,'archives':g['archives'],'samples':len(snapshots),'complete':len(complete),'cpuRawRange':bounds('CpuControlTemperatureC'),'gpuRange':bounds('GpuTemperatureC'),'runs':[{'id':key,**metrics(v),'first':v[0]['snapshotTimestamp'],'last':v[-1]['snapshotTimestamp']} for key,v in runs.items()]})
 dataset.append({'session':sid,'snapshots':snapshots,'decisions':ds,'configEvents':configs})
history={};sources=[];csv_seen=set()
def scan(z,prefix,depth=0):
 for n in z.namelist():
  if n.endswith('.zip') and depth<2:
   with zipfile.ZipFile(io.BytesIO(z.read(n))) as child:scan(child,prefix+'!'+n,depth+1)
  elif n.endswith('.csv'):
   data=z.read(n);reader=csv.DictReader(io.StringIO(data.decode('utf-8-sig',errors='replace')))
   if reader.fieldnames and 'cpu_package_temp_c' in reader.fieldnames:
    rows=list(reader);digest=hashlib.sha256(data).hexdigest();sources.append({'source':prefix+'!'+n,'sha256':digest,'samples':len(rows)})
    if digest in csv_seen:continue
    csv_seen.add(digest)
    for row in rows:
     if row.get('timestamp_utc'):history.setdefault(row['timestamp_utc'],row)
for directory in filter(None,[a.upload_dir,a.historical_dir]):
 for path in sorted(directory.glob('*.zip')):
  if directory==a.historical_dir and path.name not in ['01-logs.zip','19-logs.zip']:continue
  with zipfile.ZipFile(path) as z:scan(z,path.name)
firmware=[]
for path in sorted(a.upload_dir.glob('*hp-auto.csv')):
 rows=list(csv.DictReader(path.open(encoding='utf-8-sig')));e={'file':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'samples':len(rows)}
 for k in ['cpu_package_temp_c','cpu_core_max_temp_c','gpu_temp_c','cpu_package_power_w','gpu_power_w','cpu_fan_rpm','gpu_fan_rpm']:
  v=[float(r[k]) for r in rows if r.get(k)];e[k]={'min':min(v),'median':statistics.median(v),'max':max(v)}
 firmware.append(e)
report={'target':'HP-8C40-9D0R1LA-F18','deduplication':'Union by application session/timestamp and identical event lines; automatic IDs separate. Historical CSV union by timestamp.','sessions':summary,'olderWithoutTelemetry':older,'historicalCsv':{'distinctSamples':len(history),'sources':sources,'scope':'Earlier manual, firmware and experimental engines: context, not current-preset proof.'},'firmware':firmware}
for name,data in [('audit.json',report),('dataset.json',dataset)]:(a.output_dir/name).write_text(json.dumps(data,indent=2),encoding='utf-8')
print('Collected',sum(s['samples'] for s in summary),'recent snapshots,',len(history),'historical CSV timestamps and',sum(f['samples'] for f in firmware),'firmware samples.')
