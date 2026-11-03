import hashlib,json,xml.etree.ElementTree as ET,zipfile,subprocess
from pathlib import Path
r=Path(__file__).resolve().parents[1];p=r/'dist/AICT-host-url-update-v4'
sha=lambda b:hashlib.sha256(b).hexdigest()
expected={}
for line in (p/'SHA256SUMS.txt').read_text('utf-8').splitlines():
    h,n=line.split('  ',1);assert sha((p/n).read_bytes())==h,n;expected[n]=h
with zipfile.ZipFile(p.with_suffix('.zip')) as z:
    assert z.testzip() is None
    assert all(sha(z.read(n))==h for n,h in expected.items())
meta=json.loads((p/'UPDATE.json').read_text('utf-8'));target=json.loads((p/'TARGET-MANIFEST.json').read_text('utf-8'))
assert sha((p/'TARGET-MANIFEST.json').read_bytes())==meta['targetManifestSha256']
assert sha((p/'TARGET-SHA256SUMS.txt').read_bytes())==meta['targetSumsSha256']
for n,h in meta['files'].items():assert sha((p/'payload'/n).read_bytes())==h and target['files'][n]==h
assert not any(n.startswith('state/') or n.endswith('.sqlite') for n in meta['files'])
for n in ('rules.js','worker.js','gate.js','gate.html','blocked.js','manifest.json'):
    assert (p/'payload/Extension'/n).read_bytes()==(r/'browser-extension/beta'/n).read_bytes()
assert json.loads((p/'payload/Extension/manifest.json').read_text('utf-8'))['version']=='0.2.0'
trx=r/'artifacts/core-tests-20261103T125927828Z/core.trx';c=ET.parse(trx).getroot().find('.//{*}Counters').attrib;assert c['total']==c['passed']=='121'
live=[]
for n in ('extension-live-1793710853435','extension-live-1793710857540'):
    f=r/'artifacts'/n/'result.json';d=json.loads(f.read_text('utf-8'));assert d['passed'] and len(d['cases'])==10;live.append({'path':str(f),'sha256':sha(f.read_bytes()),'browser':d['browser']['product'],'cases':len(d['cases'])})
build=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8'))['payloadDirectory'])
assert json.loads((build/'ui-self-test/result.json').read_text('utf-8'))['passed']
service=subprocess.run([str(build/'Service/Aict.Beta.Service.exe'),'--offline-check'],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=15);assert service.returncode==0,service.stdout
assert sha((r/'dist/AICT-host-trial-v1.zip').read_bytes())=='20930a897b2c2afa45e605a5452f3e2e1b07cf6ee370c7dab09d374f7e5a0002'
installed=Path(r'C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f')
assert sha((installed/'MANIFEST.json').read_bytes())==meta['baseManifestSha256']
d=json.loads((installed/'state/status.json').read_text('utf-8'))['status'];assert not d['policy']['groups'] and not d['websites']['firewallClosed']
receipt={'kind':'AICT_URL_PATTERNS_V4_LOCAL_VALIDATION','package':str(p.with_suffix('.zip')),'sha256':sha(p.with_suffix('.zip').read_bytes()),'core_tests':121,'core_trx':str(trx),'extension_simulation_tests':11,'live_browser_tests':live,'offline_native_layout':service.stdout.decode('utf-8','replace'),'updater_filesystem_recovery_cases':8,'database_payload':False,'draft':{'sites':2,'exceptions':40,'restored_as':'unsubmitted draft'},'host_update_applied':False,'host_rules_enabled':False,'limitations':['exact-page rules ignore query parameters','complex patterns mediate document navigation, not every background resource','real site acceptance pending']}
with (r/'reports/url-patterns-v4-local-checks-20261103.json').open('x',encoding='utf-8') as f:json.dump(receipt,f,ensure_ascii=False,indent=2)
print(json.dumps(receipt,ensure_ascii=False))
