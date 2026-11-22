"""Release admission fixtures: exact source/branch, all checks, latest run and failures."""
import importlib.util
from pathlib import Path
spec=importlib.util.spec_from_file_location('admission',Path(__file__).with_name('check-release-workflows.py'))
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
head='a'*40;branch='feature/independent-functions'
runs=[dict(name=name,id=i+1,head_sha=head,event='push',head_branch=branch,status='completed',conclusion='success')
      for i,name in enumerate(('oem-shadow','wmi-fan-experiment','cpu-rapl','esp32-firmware'))]
assert module.check(runs,head,branch)==0
assert module.check(runs[:-1],head,branch)==2
assert module.check([dict(row,head_sha='b'*40) for row in runs],head,branch)==2
assert module.check([dict(row,head_branch='main') for row in runs],head,branch)==2
assert module.check(runs[:-1]+[dict(runs[-1],conclusion='failure')],head,branch)==1
assert module.check(runs+[dict(runs[-1],id=99,status='in_progress',conclusion=None)],head,branch)==2
assert module.check([dict(runs[-1],id=0,conclusion='failure')]+runs,head,branch)==0
print('PASS release admission: four same-source/branch checks, external firmware required, failed/pending/latest run fences.')
