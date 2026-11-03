"""Build the report-scoped delta update. No host service, policy or registry writes."""
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path

def sha(data): return hashlib.sha256(data).hexdigest()
def file_sha(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()
def encoded(value): return (json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8')

def main():
    root=Path(__file__).resolve().parents[1]
    output=root/'dist/AICT-beta-rules-update-v2.zip'
    assert not output.exists() and not output.with_suffix('.zip.sha256').exists()
    payload=Path(json.loads((root/'lab/build/beta/last-build.json').read_text('utf-8-sig'))['payloadDirectory'])
    ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8-sig'));assert ui['passed'] and not ui['systemWrites']
    base=root/'dist/AICT-self-use-beta-v1.zip'
    assert file_sha(base)=='9cf460f469dc64a1935121db7677b80e8e58ca3ad4a526307437ffcffe22a6fa'
    with zipfile.ZipFile(base) as archive:
        original=json.loads(archive.read('MANIFEST.json'));base_hash=sha(archive.read('MANIFEST.json'))
    candidates={}
    for folder in ('Service','Agent','App','Extension','Targets','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file(): candidates[p.relative_to(payload).as_posix()]=p
    candidates['BetaBridge.exe']=payload/'BetaBridge.exe'
    for p in (root/'src').rglob('*'):
        if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(root).parts) and p.suffix in ('.cs','.csproj','.json','.xaml','.manifest'):
            candidates['Sources/'+p.relative_to(root).as_posix()]=p
    for p in (root/'tests/Aict.Core.Tests').glob('*.cs'):candidates['Sources/tests/Aict.Core.Tests/'+p.name]=p
    for p in (root/'lab').glob('Beta*.cs'):candidates['Sources/lab/'+p.name]=p
    for name in ('Install-Beta.ps1','Update-BetaRules.ps1','Build-Beta.ps1'):
        candidates['Sources/scripts/'+name]=root/'scripts'/name
    changed={name:path.read_bytes() for name,path in candidates.items() if original['files'].get(name)!=file_sha(path)}
    assert all(not n.startswith('state/') and not n.endswith('.sqlite') for n in changed)
    target=dict(original);target['version']='0.2.0';target['revision']='rules-v2';target['windows_integration_acceptance']='NOT_RUN'
    target['files']=dict(original['files']);target['files'].update({n:sha(b) for n,b in changed.items()})
    target_bytes=encoded(target)
    target_sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode('utf-8')
    update={'kind':'AICT_BETA_RULES_V2_DELTA','baseManifestSha256':base_hash,'targetManifestSha256':sha(target_bytes),
            'targetSumsSha256':sha(target_sums),'files':{n:sha(b) for n,b in changed.items()},
            'target_run_id':'111e0fe3cfa348b4a055eafb99e25f06','database_replacement':False}
    update_bytes=encoded(update)
    script=(root/'scripts/Update-BetaRules.ps1').read_text('utf-8-sig').replace('UPDATE_METADATA_SHA256',sha(update_bytes)).encode('utf-8-sig')
    build=root/'lab/build/rules-update'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    (build/'Update-BetaRules.ps1').write_bytes(script)
    code=(root/'lab/BetaSetupRepair.cs').read_text('utf-8-sig').replace('REPAIR_SCRIPT_SHA256',sha(script)).replace('Repair-BetaSetup.ps1','Update-BetaRules.ps1').replace('repair-result.json','update-result.json')
    code=code.replace('补齐安装目录，接着原来的安装继续','规则交互更新：保留原规则与数据').replace('修复并继续安装','更新并保留规则').replace('安装修复','规则更新')
    code=code.replace('对应你回传的安装报告。此修复补齐缺失目录，再运行原安装程序；不清空规则、不新建会话。请先关闭旧安装窗口。完成一次管理员确认即可。','请保存工作并关闭专注计划主窗口。此更新会短暂重启后台及采集，不清空原规则、用量或待生效申请。完成一次管理员确认即可。')
    code=code.replace('正在继续迁移规则、启动后台并确认状态','正在更新程序、重启后台并确认状态').replace('正在校验已有安装文件','正在校验已有安装与更新文件').replace('正在检查并继续原安装','正在检查并更新程序')
    code=code.replace('安装已完成。现在可从桌面“专注计划”打开；原规则保留。','更新已完成。仍从原桌面“专注计划”打开，规则与用量保留。')
    code=code.replace('安装未完成。请把本文件夹的 update-result.json 和 setup-result-after-repair.json 回传；无需删除旧配置。','更新未完成。请回传本文件夹的 update-result.json 及同时生成的状态/故障文件；无需删除旧配置。')
    (build/'BetaRulesUpdate.cs').write_text(code,encoding='utf-8-sig')
    exe=build/'更新专注计划.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(exe),str(build/'BetaRulesUpdate.cs')],check=True,cwd=root)
    tests={}
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    guard="$taskOk=$false;try{& '"+str(build/'Update-BetaRules.ps1').replace("'","''")+"'}catch{if($_.Exception.Message -match 'VMware guest required'){$taskOk=$true}else{throw}};if(-not $taskOk){throw 'Physical host was not refused'};Write-Output 'Update refused physical host before writes'"
    for name,args in (('updater_host_refusal',[ps,'-NoProfile','-Command',guard]),('native_and_script_guards',[ps,'-NoProfile','-File',str(root/'tests/Test-BetaGuards.ps1'),'-Payload',str(payload)])):
        p=subprocess.run(args,cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=90);(build/(name+'.txt')).write_bytes(p.stdout);assert p.returncode==0,p.stdout.decode('utf-8','replace');tests[name]={'passed':True,'log':(build/(name+'.txt')).relative_to(root).as_posix()}
    p=subprocess.run([str(exe),'--guard-check'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=15);assert p.returncode==1 and b'VMware guest required' in p.stderr
    files={'payload/'+n:b for n,b in changed.items()}
    files.update({'UPDATE.json':update_bytes,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':target_sums,'Update-BetaRules.ps1':script,'更新专注计划.exe':exe.read_bytes(),'README.md':(root/'docs/RULES_UPDATE_V2.md').read_bytes(),'Sources/BetaRulesUpdate.cs':(build/'BetaRulesUpdate.cs').read_bytes()})
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in files.items()).encode('utf-8')
    with zipfile.ZipFile(output,'x',zipfile.ZIP_DEFLATED) as archive:
        for n,b in files.items():archive.writestr(n,b)
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        assert all(archive.read(n)==b for n,b in files.items())
    with output.with_suffix('.zip.sha256').open('x',encoding='utf-8') as f:f.write(file_sha(output)+'  '+output.name+'\n')
    trx=root/'artifacts/core-tests-20261103T101516700Z/core.trx';counters=ET.parse(trx).getroot().find('.//{*}Counters').attrib;assert counters['total']==counters['passed']=='108'
    receipt={'kind':'AICT_RULES_UPDATE_V2_LOCAL_VALIDATION','package':output.relative_to(root).as_posix(),'sha256':file_sha(output),'bytes':output.stat().st_size,'changed_files':len(changed),'core_tests':108,'core_test_evidence':trx.relative_to(root).as_posix(),'checks':tests,'ui_check':ui,'build_directory':str(payload.parent),'database_in_update':False,'guest_update_and_new_timing_acceptance':'NOT_RUN','source_v1_unchanged':file_sha(base)== '9cf460f469dc64a1935121db7677b80e8e58ca3ad4a526307437ffcffe22a6fa'}
    with (root/'reports/rules-update-v2-local-checks-20261103.json').open('x',encoding='utf-8') as f:json.dump(receipt,f,ensure_ascii=False,indent=2)
    print(json.dumps(receipt,ensure_ascii=False))

if __name__=='__main__':main()
