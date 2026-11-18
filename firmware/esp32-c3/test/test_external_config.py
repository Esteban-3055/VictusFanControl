"""Configuration extension against the production C++ parser (no board IO)."""
import copy, json, subprocess, sys
from test_protocol import hello, auth, frame, telemetry, run, RX

HOST=sys.argv[1]
CONFIG=dict(schemaVersion=1,revision='0123456789abcdef0123456789abcdef',
    points=[dict(input=0,output=30),dict(input=50,output=130),dict(input=100,output=255)],
    input=0,requestMinimum=0,requestMaximum=54,manualOutput=130,rise=12,fall=6,
    filterMs=1500,hysteresis=3,downHoldMs=3000,startMs=800,startOutput=255)

def message(config,valid=False,seq=1):
    p=telemetry(valid);p['externalConfiguration']=config;return frame('telemetry',seq,p,RX)

results=run(HOST,[hello(),auth(),message(CONFIG),message(CONFIG,True,2),message(CONFIG,True,3)])
assert results[-1]['payload']['appliedConfigRevision']==CONFIG['revision']
assert results[-1]['payload']['externalCurveVersion']==1
assert results[-1]['payload']['externalMinimumOutput']==0
assert results[-1]['payload']['mode']=='Remote'
print('PASS authenticated config, applied revision, capability and remote lease')

for name,change in {
    'version':lambda c:c.update(schemaVersion=2),
    'wrong type':lambda c:c.update(rise=True),
    'absent field':lambda c:c.pop('fall'),
    'null required':lambda c:c.update(startMs=None),
    'revision':lambda c:c.update(revision='secret arbitrary text'),
    'duplicate x':lambda c:c['points'][1].update(input=0),
    'missing endpoint':lambda c:c['points'][0].update(input=1),
    'decreasing y':lambda c:c['points'][2].update(output=100),
    'below min':lambda c:c['points'][0].update(output=-1),
    'above max':lambda c:c['points'][2].update(output=256),
    'too many':lambda c:c.update(points=[dict(input=i*5,output=30+i) for i in range(21)]),
    'calibration':lambda c:c.update(requestMinimum=54),
    'unbounded hold':lambda c:c.update(downHoldMs=30001),
    'unbounded start':lambda c:c.update(startMs=3001),
}.items():
    bad=copy.deepcopy(CONFIG);change(bad);run(HOST,[hello(),auth(),message(bad)],accepted=False);print('PASS reject config',name)
bad=copy.deepcopy(CONFIG);bad['rise']=13
run(HOST,[hello(),auth(),message(CONFIG),message(bad,True,2)],accepted=False)
print('PASS reject reused revision with modified contents')
results=run(HOST,[hello(),auth(),message(CONFIG),message(CONFIG,False,2),message(CONFIG,False,3)])
assert results[-1]['payload']['mode']=='Local'
print('PASS MonitorOnly config does not grant authority')
zero=copy.deepcopy(CONFIG)
zero.update(points=[dict(input=0,output=0),dict(input=100,output=255)],manualOutput=0,startOutput=0,startMs=0,filterMs=0,hysteresis=0,downHoldMs=0)
results=run(HOST,[hello(),auth(),message(zero),message(zero,True,2),message(zero,True,3)])
assert results[-1]['payload']['appliedConfigRevision']==zero['revision']
print('PASS authenticated full 0-255 config with zero Manual/start output')

# The controller survives a TCP session. Reusing its revision must not falsely
# confirm different contents after reconnecting with fresh HMAC authentication.
def reconnect(config, accepted):
    messages=[hello(),auth(),message(CONFIG),'RECONNECT',hello(),auth(),message(config),message(config,True,2),message(config,True,3)]
    result=subprocess.run([HOST,'--reconnect'],input='\n'.join(messages)+'\n',text=True,capture_output=True,timeout=5)
    lines=result.stdout.splitlines()
    if accepted:
        assert result.returncode==0 and len(lines)==len(messages),(result.returncode,lines,result.stderr)
        status=json.loads(lines[-1])['payload']
        assert status['mode']=='Remote' and status['appliedConfigRevision']==config['revision'],status
    else:
        assert result.returncode!=0 and lines[-1]=='REJECT',('reused revision falsely confirmed after reconnect',lines)

changed=copy.deepcopy(CONFIG);changed['rise']=13
reconnect(changed,False)
reconnect(CONFIG,True)
changed['revision']='0123456789abcdef0123456789abcdee'
reconnect(changed,True)
print('PASS reconnect retains immutable curve identity, rejects changed contents and accepts unchanged/new revisions')

# Retention must carry neither authority nor authentication/sequence history.
messages=[hello(),auth(),message(CONFIG),message(CONFIG,True,2),message(CONFIG,True,3),
          'RECONNECT',hello(),auth(),message(CONFIG)]
result=subprocess.run([HOST,'--reconnect'],input='\n'.join(messages)+'\n',text=True,capture_output=True,timeout=5)
assert result.returncode==0 and len(result.stdout.splitlines())==len(messages),(result.returncode,result.stdout)
assert json.loads(result.stdout.splitlines()[-1])['payload']['mode']=='Local'
messages=[hello(),auth(),message(CONFIG),'RECONNECT',message(CONFIG,True,2)]
result=subprocess.run([HOST,'--reconnect'],input='\n'.join(messages)+'\n',text=True,capture_output=True,timeout=5)
assert result.returncode!=0 and result.stdout.splitlines()[-1]=='REJECT',result.stdout
print('PASS retained curve grants no Remote lease and reconnect requires fresh authentication')
