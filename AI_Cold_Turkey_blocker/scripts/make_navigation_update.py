"""Freeze the v8 -> v9 browser-only delta. Never installs or changes system policy."""
import hashlib,json,subprocess,zipfile
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')

def main():
    out=r/'dist/AICT-smooth-navigation-v9';base_dir=r/'dist/AICT-block-reasons-v8'
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen release exists')
    base_bytes=(base_dir/'TARGET-MANIFEST.json').read_bytes();base=json.loads(base_bytes)
    assert sha(base_bytes)=='1b1316a03c91d5298c523a32b9ff5cbc87e651f0bd17362aac61b049d2e33ea3'
    candidate={}
    for p in (r/'browser-extension/beta').rglob('*'):
        if p.is_file():
            candidate['Extension/'+p.relative_to(r/'browser-extension/beta').as_posix()]=p.read_bytes()
            candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    for p in (r/'tests').glob('extension-*.mjs'):candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    changed={n:b for n,b in candidate.items() if base['files'].get(n)!=sha(b)}
    assert json.loads((r/'browser-extension/beta/manifest.json').read_text('utf-8'))['version']=='0.4.0'
    assert changed and all(n.startswith(('Extension/','Sources/')) for n in changed)
    target={**base,'version':'0.6.1','revision':'smooth-navigation-v9','files':{**base['files'],**{n:sha(b) for n,b in changed.items()}}}
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_SMOOTH_NAVIGATION_V9_DELTA','baseManifestSha256':sha(base_bytes),'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False,'browser_extension_change':True,'extension_version':'0.4.0','backend_revision':'block-reasons-v8'})
    old_meta=(base_dir/'UPDATE.json').read_bytes();old_script=(base_dir/'Update-Reasons.ps1').read_bytes();script=old_script.decode('utf-8-sig');assert sha(old_meta) in script
    script=script.replace(sha(old_meta),sha(meta)).replace('AICT_BLOCK_REASONS_V8_UPDATE','AICT_SMOOTH_NAVIGATION_V9_UPDATE').replace("updaterVersion='8.0'","updaterVersion='9.0'")
    # Backend binaries are unchanged; keep its existing v8 health expectation.
    assert "status.revision -ceq 'block-reasons-v8'" in script
    build=r/'lab/build/smooth-navigation'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    script_bytes=script.encode('utf-8-sig');(build/'Update-Navigation.ps1').write_bytes(script_bytes)
    code=(base_dir/'Sources/ReasonUpdate.cs').read_text('utf-8-sig');assert sha(old_script) in code
    code=code.replace(sha(old_script),sha(script_bytes)).replace('Update-Reasons.ps1','Update-Navigation.ps1').replace('封锁原因显示更新','网页加载优化').replace('封锁原因更新','网页加载优化').replace('封锁页显示命中规则、触发原因和相关截止时间','减少正常网站及例外网页的插件中转闪屏').replace('打开新版 · 查看封锁原因','打开专注计划').replace('0.3.0','0.4.0')
    code=code.replace('更新完成。请先重新加载浏览器组件到0.4.0，再查看网站封锁页。软件主窗口也可点击“查看当前封锁原因”。','更新完成。请重新加载浏览器组件到0.4.0，再刷新原网页。普通网站和可提前确认的例外现在直接加载。')
    (build/'NavigationUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(build/'更新网页加载优化.exe'),str(build/'NavigationUpdate.cs')],check=True)
    check=subprocess.run([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-Navigation.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'updater-tests.txt').write_bytes(check.stdout);assert check.returncode==0,check.stdout
    files={'payload/'+n:b for n,b in changed.items()}
    files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-Navigation.ps1':script_bytes,'更新网页加载优化.exe':(build/'更新网页加载优化.exe').read_bytes(),'README.md':(r/'docs/SMOOTH_NAVIGATION_V9.md').read_bytes(),'Sources/NavigationUpdate.cs':(build/'NavigationUpdate.cs').read_bytes(),'Sources/make_navigation_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode();out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    digest=sha(out.with_suffix('.zip').read_bytes());out.with_suffix('.zip.sha256').write_text(digest+'  '+out.name+'.zip\n')
    receipt={'kind':'AICT_SMOOTH_NAVIGATION_V9_PACKAGE_VALIDATION','package':str(out.with_suffix('.zip')),'sha256':digest,'changed_files':len(changed),'backend_changed':False,'updater_filesystem_recovery_cases':8,'extension_version':'0.4.0','database_in_package':False,'host_installation_performed':False,'build':str(build),'browser_live_results':'Recorded separately after testing; package construction is not a browser pass.'}
    with (r/'reports/smooth-navigation-v9-package-20261103.json').open('xb') as f:f.write(enc(receipt))
    print(json.dumps(receipt,ensure_ascii=False))
if __name__=='__main__':main()
