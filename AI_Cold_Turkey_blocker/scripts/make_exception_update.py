"""Build a v4 -> v5 host update without touching the installed service or its data."""
import hashlib,json,subprocess,zipfile,xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def main():
    out=r/'dist/AICT-exception-organizer-v5'
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen release exists')
    base_dir=r/'dist/AICT-host-url-update-v4';base_bytes=(base_dir/'TARGET-MANIFEST.json').read_bytes();base=json.loads(base_bytes)
    assert sha(base_bytes)=='e034e756f0ca8d6023364a6156c901e4a7b6680748b012c20940842e7c1e4852'
    installed=Path(r'C:\ProgramData\AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f');assert (installed/'MANIFEST.json').read_bytes()==base_bytes
    payload=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'))['payloadDirectory'])
    ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8'));assert all(ui[k] for k in ('passed','exceptionNotes','groupSearch','newRulesHaveNoCycle')) and not ui['systemWrites']
    candidate={}
    for folder in ('App','Agent','Service','Extension','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file():candidate[p.relative_to(payload).as_posix()]=p.read_bytes()
    for folder in ('src','tests/Aict.Core.Tests'):
        for p in (r/folder).rglob('*'):
            if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(r).parts) and p.suffix in ('.cs','.csproj','.xaml','.json','.manifest'):candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    changed={n:b for n,b in candidate.items() if base['files'].get(n)!=sha(b)}
    assert not any(n.startswith('Extension/') for n in changed),'This release must not require extension changes'
    target={**base,'version':'0.3.0','revision':'exception-organizer-v5','files':{**base['files'],**{n:sha(b) for n,b in changed.items()}}}
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_EXCEPTION_ORGANIZER_V5_DELTA','baseManifestSha256':sha(base_bytes),'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False,'browser_extension_change':False})
    old_meta=(base_dir/'UPDATE.json').read_bytes();script=(base_dir/'Update-HostUrls.ps1').read_text('utf-8-sig')
    assert sha(old_meta) in script
    script=script.replace(sha(old_meta),sha(meta)).replace('AICT_HOST_URL_V4_UPDATE','AICT_EXCEPTION_ORGANIZER_V5_UPDATE').replace("updaterVersion='4.0'","updaterVersion='5.0'").replace("'url-patterns-v4'","'exception-organizer-v5'")
    build=r/'lab/build/exception-organizer'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    script_bytes=script.encode('utf-8-sig');(build/'Update-Exceptions.ps1').write_bytes(script_bytes)
    code=(r/'lab/HostUrlUpdate.cs').read_text('utf-8-sig').replace('UPDATE_SCRIPT_SHA256',sha(script_bytes)).replace('Update-HostUrls.ps1','Update-Exceptions.ps1').replace('网址与通配符更新','例外备注与分组更新').replace('网址功能更新','例外整理更新').replace('添加网址路径、* 通配符和页面例外','例外清单：备注、分组、搜索和批量整理')
    code=code.replace('打开新版并恢复网站草稿','打开新版 · 管理例外').replace('更新现有主机安装，保留规则、用量和待生效申请。此操作重启后台，并从更新时起重新安排30分钟测试与独立恢复任务。旧窗口的网站列表已备份，新版打开后恢复为未提交草稿；请重新核对时间条件。','请先在旧窗口提交想保留的草稿，再关闭专注计划主窗口。更新保留已保存的例外、规则、用量和待生效申请；旧例外显示为未分组。本次重启后台并重新安排30分钟测试和独立恢复任务，已停用的规则保持停用。浏览器组件无需重新加载。')
    code=code.replace('更新完成。请重新加载浏览器组件，再打开新版。网站草稿将恢复，规则要经你提交才开始执行。','更新完成。打开新版，在规则中的“管理例外”添加备注、分组并搜索；完成后点击主窗口“提交完整修改”。本次不需要重新加载浏览器组件。')
    start=code.index('new ProcessStartInfo(app,');end=code.index('{UseShellExecute=true,WorkingDirectory=InstallRoot}',start);code=code[:start]+'new ProcessStartInfo(app)'+code[end:]
    code=code.replace(',draft=Path.Combine(Root,"website-draft.json")','')
    code=code.replace('复制浏览器组件文件夹路径','复制组件路径（本次无需重载）').replace('更新成功后，在浏览器扩展页面对原来的专注计划组件点击“重新加载”。若原组件加载自旧压缩包，请移除后加载刚复制的安装目录。','已复制现有组件路径。本次只更新例外整理功能，不需要重新加载浏览器组件。')
    (build/'ExceptionUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(build/'更新备注与分组.exe'),str(build/'ExceptionUpdate.cs')],check=True)
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe';check=subprocess.run([ps,'-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-Exceptions.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'updater-tests.txt').write_bytes(check.stdout);assert check.returncode==0,check.stdout
    files={'payload/'+n:b for n,b in changed.items()};files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-Exceptions.ps1':script_bytes,'更新备注与分组.exe':(build/'更新备注与分组.exe').read_bytes(),'README.md':(r/'docs/EXCEPTION_ORGANIZER_V5.md').read_bytes(),'界面预览.png':(payload/'ui-self-test/exceptions.png').read_bytes(),'Sources/ExceptionUpdate.cs':(build/'ExceptionUpdate.cs').read_bytes(),'Sources/make_exception_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode()
    out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    digest=sha(out.with_suffix('.zip').read_bytes());out.with_suffix('.zip.sha256').write_text(digest+'  '+out.name+'.zip\n')
    test=r/'artifacts/core-tests-20261103T134016934Z/core.trx';c=ET.parse(test).getroot().find('.//{*}Counters').attrib;assert c['total']==c['passed']=='126'
    receipt={'kind':'AICT_EXCEPTION_ORGANIZER_V5_LOCAL_VALIDATION','package':str(out.with_suffix('.zip')),'sha256':digest,'changed_files':len(changed),'core_tests':126,'core_trx':str(test),'ui_self_test':ui,'ui_preview':str(out/'界面预览.png'),'updater_filesystem_recovery_cases':8,'extension_unchanged':True,'database_in_package':False,'host_installation_performed':False,'pending_deadline_preserved_by_annotation_tests':True,'build':str(build)}
    (r/'reports/exception-organizer-v5-local-checks-20261103.json').write_bytes(enc(receipt));print(json.dumps(receipt,ensure_ascii=False))
if __name__=='__main__':main()
