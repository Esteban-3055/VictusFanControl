"""Test the actual portable C++ firmware engine; public vectors only, no ESP32 IO."""
import argparse, base64, copy, hashlib, hmac, json, pathlib, subprocess

ROOT = pathlib.Path(__file__).resolve().parents[3]
V = json.loads((ROOT / 'docs/EXTERNAL-COOLING-V1-VECTORS.json').read_text())
KEY = base64.b64decode(V['pairingKey'])
SESSION = V['sessionId']
def proof(role):
    return base64.b64encode(hmac.digest(KEY, ('VictusExternalCooling/v1/auth\n'+role+'\n'+SESSION+'\n'+V['clientNonce']+'\n'+V['serverNonce']).encode(), 'sha256')).decode()
RX, TX = (base64.b64decode(proof(x)) for x in ['client-to-device','device-to-client'])
def encode(value): return json.dumps(value, separators=(',', ':'), ensure_ascii=False)
def frame(kind, seq, payload, key=None, raw=None):
    raw = encode(payload) if raw is None else raw
    mac = '' if key is None else base64.b64encode(hmac.digest(key, f'VictusExternalCooling/v1/frame\n{kind}\n{SESSION}\n{seq}\n{raw}'.encode(), 'sha256')).decode()
    return '{"protocolVersion":1,"type":'+encode(kind)+',"sessionId":'+encode(SESSION)+',"sequence":'+str(seq)+',"payload":'+raw+',"mac":'+encode(mac)+'}'
def hello(): return frame('hello',0,{'clientNonce': V['clientNonce']})
def auth(): return frame('authenticate',0,{'proof':proof('client')})
def telemetry(valid=False, mode='Automatic'):
    at='2026-10-16T17:00:00.1234567+00:00'
    return dict(validForMs=2000,replyToDeviceSequence=1 if valid else None,capturedAtUtc=at,sampledAtUtc=at,decisionAtUtc=at,
                cpuTempSampledAtUtc=at,gpuTempSampledAtUtc=at,ramSampledAtUtc=at,cpuUsagePct=10,gpuUsagePct=0,ramUsagePct=44,
                demandaPct=35 if mode=='Automatic' else None,solicitud=27 if valid else None,solicitudValida=valid,
                cpuTargetLevel=27 if valid else None,gpuTargetLevel=27 if valid else None,modoSolicitado=mode,modoEfectivo=mode,
                cpuTempC=55,gpuTempC=42,powerSource='Battery',requestEvidence='WmiAcceptedSupervised' if valid else 'MonitorOnly')
def run(host, messages, accepted=True):
    result=subprocess.run([host],input=('\n'.join(messages)+'\n').encode(),capture_output=True,timeout=5)
    lines=result.stdout.decode().splitlines()
    if accepted:
        assert result.returncode==0 and len(lines)==len(messages),(result.returncode,lines,result.stderr.decode())
        responses=[json.loads(x) for x in lines]
        assert responses[0]['payload']['proof']==V['proofs']['server']
        if len(responses)>1: assert responses[1]['payload']['proof']==V['proofs']['ready']
        for response in responses[2:]:
            assert response['sessionId']==SESSION and response['protocolVersion']==1 and response['type']=='deviceStatus'
            raw=encode(response['payload'])
            expected=base64.b64encode(hmac.digest(TX,f"VictusExternalCooling/v1/frame\ndeviceStatus\n{SESSION}\n{response['sequence']}\n{raw}".encode(),'sha256')).decode()
            assert hmac.compare_digest(expected,response['mac'])
            assert response['payload']['extensions']=={'slot1':None,'slot2':None}
        return responses
    assert result.returncode!=0 and lines[-1]=='REJECT',(result.returncode,lines)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('host');parser.add_argument('--report');args=parser.parse_args()
    cases=[]
    def case(name, action): action();cases.append({'name':name,'passed':True});print('PASS',name)
    case('device calibration, inverted NOT duty, independent lease and wraparound',lambda:subprocess.run([args.host,'--policy'],check=True,timeout=5))
    case('external controller dynamics and safety',lambda:subprocess.run([args.host,'--controller'],check=True,timeout=5))
    for role,value in V['proofs'].items(): assert proof(role)==value
    base=[hello(),auth(),frame('telemetry',1,telemetry(),RX)]
    def healthy():
        results=run(args.host,base+[frame('telemetry',2,telemetry(True),RX),frame('telemetry',3,telemetry(True),RX)])
        assert results[-1]['payload']['mode']=='Remote' and results[-1]['payload']['appliedDuty']==255
    case('real C++ mutual auth, directional MACs and accepted request',healthy)
    case('Firmware null request',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,telemetry(False,'Firmware'),RX)]))
    case('Manual accepted request',lambda:run(args.host,base+[frame('telemetry',2,telemetry(True,'Manual'),RX)]))
    def local_unknown_reference():
        p=telemetry(True);p['replyToDeviceSequence']=99
        r=run(args.host,base+[frame('telemetry',2,p,RX),frame('telemetry',3,p,RX)])
        assert r[-1]['payload']['mode']=='Local'
    case('unknown reply reference never grants Remote',local_unknown_reference)
    mutations={
        'expired source':lambda p:p.update(sampledAtUtc='2026-10-16T16:59:55.1234567Z'),
        'future source':lambda p:p.update(cpuTempSampledAtUtc='2026-10-16T17:00:01Z'),
        'missing thermal':lambda p:p.update(gpuTempC=None),
        'missing decision':lambda p:p.update(decisionAtUtc=None),
        'false validity with request':lambda p:p.update(solicitudValida=False),
        'Firmware invented request':lambda p:p.update(modoSolicitado='Firmware',modoEfectivo='Firmware',demandaPct=None),
        'invalid enum':lambda p:p.update(powerSource='Mains'),
        'negative load':lambda p:p.update(cpuUsagePct=-1),
        'boolean measurement':lambda p:p.update(cpuUsagePct=True),
        'out of wire bounds':lambda p:p.update(solicitud=65536),
        'fractional request':lambda p:p.update(solicitud=27.5),
        'absent required field':lambda p:p.pop('ramUsagePct'),
        'bad date':lambda p:p.update(capturedAtUtc='2026-02-30T17:00:00Z'),
        'default date':lambda p:p.update(capturedAtUtc='0001-01-01T00:00:00Z'),
        'invalid validity type':lambda p:p.update(solicitudValida=1),
    }
    for name,mutate in mutations.items():
        p=telemetry(True);mutate(p)
        case('reject '+name,lambda p=p:run(args.host,base+[frame('telemetry',2,p,RX)],False))
    case('reject replay',lambda:run(args.host,base+[base[-1]],False))
    case('reject reflected directional key',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,telemetry(),TX)],False))
    case('reject corrupted MAC',lambda:run(args.host,[hello(),auth(),base[-1].replace('"cpuUsagePct":10','"cpuUsagePct":11')],False))
    raw=encode(telemetry()).replace('"cpuUsagePct":10','"cpuUsagePct":10,"cpuUsagePct":11')
    case('reject duplicate JSON keys',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,{},RX,raw)],False))
    raw=encode(telemetry()).replace('"cpuUsagePct":10','"cpuUsagePct":10,"cpu\\u0055sagePct":11')
    case('reject escaped duplicate JSON keys',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,{},RX,raw)],False))
    raw='{ '+encode(telemetry())[1:-1]+' }'
    case('exact raw payload whitespace participates in MAC',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,{},RX,raw)]))
    for name,bad in [('oversize','x'*4097),('partial','{"payload":{'),('trailing','{"payload":{}}x'),('unsupported version',hello().replace('"protocolVersion":1','"protocolVersion":2'))]:
        case('reject '+name,lambda bad=bad:run(args.host,[bad],False))
    # Additive unknown fields are ignored, including future extension values; they have no effects.
    p=telemetry();p['extensions']={'slot1':{'future':1},'slot2':[None]}
    case('unknown extension fields have no effects',lambda:run(args.host,[hello(),auth(),frame('telemetry',1,p,RX)]))
    nested={'clientNonce':V['clientNonce'],'future':[[[[[[[[[0]]]]]]]]]}
    case('reject excessive JSON depth',lambda:run(args.host,[frame('hello',0,nested)],False))
    def invalid_utf8():
        raw=hello().encode().replace(b'"hello"',b'"hell\xffo"')
        r=subprocess.run([args.host],input=raw+b'\n',capture_output=True,timeout=5)
        assert r.returncode!=0 and r.stdout.strip()==b'REJECT'
    case('reject invalid UTF-8',invalid_utf8)
    report={'hardwareIo':False,'physicalPass':False,'cases':cases}
    if args.report:pathlib.Path(args.report).write_text(json.dumps(report,indent=2))
    print(f'{len(cases)}/{len(cases)} firmware protocol fixtures passed; no physical ESP32 assertion.')
if __name__=='__main__':main()
