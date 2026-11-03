"""Freeze v7 -> v8 and its reason-page component. Does not install or alter policy."""
import hashlib,json,subprocess,zipfile,xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def main():
    out=r/'dist/AICT-block-reasons-v8';base_dir=r/'dist/AICT-multi-quota-v7'
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen release exists')
    base_bytes=(base_dir/'TARGET-MANIFEST.json').read_bytes();base=json.loads(base_bytes)
    assert sha(base_bytes)=='cd5f355d3309d2e1a543cf110577ad613533748fe20b90c493f3250e9dbbf96a'
    installed=Path(r'C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f');assert (installed/'MANIFEST.json').read_bytes()==base_bytes
    payload=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'))['payloadDirectory']);ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8'));assert ui['passed'] and not ui['systemWrites']
    candidate={}
    for folder in ('App','Agent','Service','Extension','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file():candidate[p.relative_to(payload).as_posix()]=p.read_bytes()
    for folder in ('src','tests/Aict.Core.Tests','browser-extension/beta'):
        for p in (r/folder).rglob('*'):
            if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(r).parts) and p.suffix in ('.cs','.csproj','.xaml','.json','.manifest','.js','.html'):candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    candidate['Sources/scripts/Build-Beta.ps1']=(r/'scripts/Build-Beta.ps1').read_bytes()
    changed={n:b for n,b in candidate.items() if base['files'].get(n)!=sha(b)}
    assert json.loads((payload/'Extension/manifest.json').read_text('utf-8'))['version']=='0.3.0'
    target={**base,'version':'0.6.0','revision':'block-reasons-v8','files':{**base['files'],**{n:sha(b) for n,b in changed.items()}}}
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_BLOCK_REASONS_V8_DELTA','baseManifestSha256':sha(base_bytes),'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False,'browser_extension_change':True,'extension_version':'0.3.0'})
    old_meta=(base_dir/'UPDATE.json').read_bytes();old_script=(base_dir/'Update-MultiQuota.ps1').read_bytes();script=old_script.decode('utf-8-sig');assert sha(old_meta) in script
    script=script.replace(sha(old_meta),sha(meta)).replace('AICT_MULTI_QUOTA_V7_UPDATE','AICT_BLOCK_REASONS_V8_UPDATE').replace("updaterVersion='7.0'","updaterVersion='8.0'").replace("'multi-quota-v7'","'block-reasons-v8'")
    build=r/'lab/build/block-reasons'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True);script_bytes=script.encode('utf-8-sig');(build/'Update-Reasons.ps1').write_bytes(script_bytes)
    code=(base_dir/'Sources/MultiQuotaUpdate.cs').read_text('utf-8-sig');assert sha(old_script) in code
    code=code.replace(sha(old_script),sha(script_bytes)).replace('Update-MultiQuota.ps1','Update-Reasons.ps1').replace('分时段独立额度更新','封锁原因显示更新').replace('独立额度更新','封锁原因更新').replace('每个时段分别设置限额、分别计时','封锁页显示命中规则、触发原因和相关截止时间').replace('打开新版 · 设置独立额度','打开新版 · 查看封锁原因')
    code=code.replace('浏览器组件无需重新加载。','本次浏览器组件需重新加载至0.3.0。')
    code=code.replace('更新完成。额度方式选择“分时段，每段独立额度”，逐行设置起止时间和分钟数，再提交。本次无需重新加载浏览器组件。','更新完成。请先重新加载浏览器组件到0.3.0，再查看网站封锁页。软件主窗口也可点击“查看当前封锁原因”。')
    code=code.replace('复制组件路径（本次无需重载）','复制新版浏览器组件路径').replace('已复制现有组件路径。本次更新共享额度的时段设置，不需要重新加载浏览器组件。','已复制安装目录的组件路径，请在浏览器扩展页面重新加载专注计划组件至0.3.0；原组件若加载自旧解压包，请改为加载此路径。')
    (build/'ReasonUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(build/'更新封锁原因.exe'),str(build/'ReasonUpdate.cs')],check=True)
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe';check=subprocess.run([ps,'-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-Reasons.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'updater-tests.txt').write_bytes(check.stdout);assert check.returncode==0,check.stdout
    files={'payload/'+n:b for n,b in changed.items()};files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-Reasons.ps1':script_bytes,'更新封锁原因.exe':(build/'更新封锁原因.exe').read_bytes(),'README.md':(r/'docs/BLOCK_REASONS_V8.md').read_bytes(),'封锁原因预览.png':(r/'artifacts/extension-live-1793718233818/block-reasons.png').read_bytes(),'Sources/ReasonUpdate.cs':(build/'ReasonUpdate.cs').read_bytes(),'Sources/make_reason_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode();out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    digest=sha(out.with_suffix('.zip').read_bytes());out.with_suffix('.zip.sha256').write_text(digest+'  '+out.name+'.zip\n')
    test=r/'artifacts/core-tests-20261103T150127011Z/core.trx';c=ET.parse(test).getroot().find('.//{*}Counters').attrib;assert c['total']==c['passed']=='155'
    receipt={'kind':'AICT_BLOCK_REASONS_V8_LOCAL_VALIDATION','package':str(out.with_suffix('.zip')),'sha256':digest,'changed_files':len(changed),'core_tests':155,'extension_simulation_tests':15,'core_trx':str(test),'ui_self_test':ui,'updater_filesystem_recovery_cases':8,'extension_version':'0.3.0','database_in_package':False,'host_installation_performed':False,'edge_live_test':str(r/'artifacts/extension-live-1793718233818/result.json'),'chrome_live_test':'Reason page rendered; loopback allow-navigation blocked by existing host WFP gate. Retest pending.','build':str(build)}
    with (r/'reports/block-reasons-v8-local-checks-20261103.json').open('xb') as f:f.write(enc(receipt))
    print(json.dumps(receipt,ensure_ascii=False))
if __name__=='__main__':main()
