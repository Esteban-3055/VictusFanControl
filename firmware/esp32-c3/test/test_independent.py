"""Independent external control through the production authenticated C++ parser; no board IO."""
import copy, sys
from test_protocol import hello, auth, frame, telemetry, run, RX

host=sys.argv[1]
config=dict(schemaVersion=1,revision='0123456789abcdef0123456789abcdef',
    points=[dict(input=0,output=0),dict(input=50,output=130),dict(input=100,output=255)],
    input=1,requestMinimum=10,requestMaximum=54,manualOutput=130,rise=100,fall=100,
    filterMs=0,hysteresis=0,downHoldMs=0,startMs=0,startOutput=0)
bootstrap=telemetry(False,'Firmware');bootstrap['externalConfiguration']=config
for kind,value in [('Fixed',0),('Fixed',255),('Curve',50)]:
    p=telemetry(False,'Firmware');p['requestEvidence']='Unavailable';p['replyToDeviceSequence']=1
    p['independentExternal']=dict(mode=kind,duty=value if kind=='Fixed' else None,
        demandPercent=value if kind=='Curve' else None,sampledAtUtc=p['capturedAtUtc'] if kind=='Curve' else None)
    r=run(host,[hello(),auth(),frame('telemetry',1,bootstrap,RX),frame('telemetry',2,p,RX),frame('telemetry',3,p,RX)])
    assert r[-1]['payload']['mode']=='Remote' and r[-1]['payload']['independentControlVersion']==1
    print('PASS independent',kind,value,'with Firmware mode and no internal fan request')
    bads=[]
    for key in ['mode','duty','demandPercent','sampledAtUtc']:
        bad=copy.deepcopy(p);bad['independentExternal'].pop(key);bads.append(bad)
    bad=copy.deepcopy(p);bad['replyToDeviceSequence']=None;bads.append(bad)
    bad=copy.deepcopy(p);bad['requestEvidence']='WmiAcceptedSupervised';bads.append(bad)
    bad=copy.deepcopy(p);bad['independentExternal']['mode']='Other';bads.append(bad)
    if kind=='Fixed':
        for duty in [-1,256,1.5]:
            bad=copy.deepcopy(p);bad['independentExternal']['duty']=duty;bads.append(bad)
    else:
        for at in [None,'2026-10-16T16:59:55+00:00','2026-10-16T17:00:01+00:00']:
            bad=copy.deepcopy(p);bad['independentExternal']['sampledAtUtc']=at;bads.append(bad)
    for bad in bads:run(host,[hello(),auth(),frame('telemetry',1,bootstrap,RX),frame('telemetry',2,bad,RX)],accepted=False)
print('PASS independent malformed, expired, future and unbound requests rejected')
