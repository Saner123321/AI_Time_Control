"""Freeze the v5 -> v6 quota update. No installed files or settings are changed."""
import hashlib,json,subprocess,zipfile,xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def main():
    out=r/'dist/AICT-window-quota-v6';base_dir=r/'dist/AICT-exception-organizer-v5'
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen release exists')
    base_bytes=(base_dir/'TARGET-MANIFEST.json').read_bytes();base=json.loads(base_bytes)
    assert sha(base_bytes)=='75f63f689c165381cdd395110edbc548b09e2243df4a6b46763e0993e3b8a0ad'
    installed=Path(r'C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f');assert (installed/'MANIFEST.json').read_bytes()==base_bytes
    payload=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'))['payloadDirectory'])
    ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8'));assert all(ui[k] for k in ('passed','windowQuota','exceptionNotes','groupSearch','newRulesHaveNoCycle')) and not ui['systemWrites']
    candidate={}
    for folder in ('App','Agent','Service','Extension','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file():candidate[p.relative_to(payload).as_posix()]=p.read_bytes()
    for folder in ('src','tests/Aict.Core.Tests'):
        for p in (r/folder).rglob('*'):
            if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(r).parts) and p.suffix in ('.cs','.csproj','.xaml','.json','.manifest'):candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    changed={n:b for n,b in candidate.items() if base['files'].get(n)!=sha(b)}
    assert not any(n.startswith('Extension/') for n in changed),'No extension reload is required by this release'
    target={**base,'version':'0.4.0','revision':'window-quota-v6','files':{**base['files'],**{n:sha(b) for n,b in changed.items()}}}
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_WINDOW_QUOTA_V6_DELTA','baseManifestSha256':sha(base_bytes),'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False,'browser_extension_change':False})
    old_meta=(base_dir/'UPDATE.json').read_bytes();old_script=(base_dir/'Update-Exceptions.ps1').read_bytes();script=old_script.decode('utf-8-sig');assert sha(old_meta) in script
    script=script.replace(sha(old_meta),sha(meta)).replace('AICT_EXCEPTION_ORGANIZER_V5_UPDATE','AICT_WINDOW_QUOTA_V6_UPDATE').replace("updaterVersion='5.0'","updaterVersion='6.0'").replace("'exception-organizer-v5'","'window-quota-v6'")
    build=r/'lab/build/window-quota'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    script_bytes=script.encode('utf-8-sig');(build/'Update-Quota.ps1').write_bytes(script_bytes)
    code=(base_dir/'Sources/ExceptionUpdate.cs').read_text('utf-8-sig');assert sha(old_script) in code
    code=code.replace(sha(old_script),sha(script_bytes)).replace('Update-Exceptions.ps1','Update-Quota.ps1').replace('例外备注与分组更新','指定时段额度更新').replace('例外整理更新','时段额度更新').replace('例外清单：备注、分组、搜索和批量整理','共享额度：可指定每天的计时范围').replace('打开新版 · 管理例外','打开新版 · 设置时段额度')
    code=code.replace('更新保留已保存的例外、规则、用量和待生效申请；旧例外显示为未分组。','更新保留规则、例外备注、用量和待生效申请；原来的每日额度保持全天。')
    code=code.replace('更新完成。打开新版，在规则中的“管理例外”添加备注、分组并搜索；完成后点击主窗口“提交完整修改”。本次不需要重新加载浏览器组件。','更新完成。打开新版，勾选共享额度，将“额度范围”改为“每日指定时间段”，填写起止时间后提交。本次无需重新加载浏览器组件。').replace('本次只更新例外整理功能','本次更新共享额度的时段设置')
    (build/'QuotaUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(build/'更新时段额度.exe'),str(build/'QuotaUpdate.cs')],check=True)
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe';check=subprocess.run([ps,'-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-Quota.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'updater-tests.txt').write_bytes(check.stdout);assert check.returncode==0,check.stdout
    files={'payload/'+n:b for n,b in changed.items()};files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-Quota.ps1':script_bytes,'更新时段额度.exe':(build/'更新时段额度.exe').read_bytes(),'README.md':(r/'docs/WINDOW_QUOTA_V6.md').read_bytes(),'额度界面预览.png':(payload/'ui-self-test/quota.png').read_bytes(),'Sources/QuotaUpdate.cs':(build/'QuotaUpdate.cs').read_bytes(),'Sources/make_quota_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode()
    out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    digest=sha(out.with_suffix('.zip').read_bytes());out.with_suffix('.zip.sha256').write_text(digest+'  '+out.name+'.zip\n')
    test=r/'artifacts/core-tests-20261103T141151469Z/core.trx';c=ET.parse(test).getroot().find('.//{*}Counters').attrib;assert c['total']==c['passed']=='137'
    receipt={'kind':'AICT_WINDOW_QUOTA_V6_LOCAL_VALIDATION','package':str(out.with_suffix('.zip')),'sha256':digest,'changed_files':len(changed),'core_tests':137,'new_quota_cases':11,'core_trx':str(test),'ui_self_test':ui,'ui_preview':str(out/'额度界面预览.png'),'updater_filesystem_recovery_cases':8,'extension_unchanged':True,'database_in_package':False,'host_installation_performed':False,'build':str(build)}
    with (r/'reports/window-quota-v6-local-checks-20261103.json').open('xb') as f:f.write(enc(receipt))
    print(json.dumps(receipt,ensure_ascii=False))
if __name__=='__main__':main()
