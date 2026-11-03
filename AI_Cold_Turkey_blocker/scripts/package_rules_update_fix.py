"""Reuse the frozen v2 program payload; fix only the graphical update bootstrap."""
import hashlib
import json
import subprocess
import zipfile
from datetime import datetime, timezone
from pathlib import Path

def sha(data): return hashlib.sha256(data).hexdigest()
def file_sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def encode(value):return (json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8')

def main():
    root=Path(__file__).resolve().parents[1]
    original=root/'dist/AICT-beta-rules-update-v2.zip'
    assert file_sha(original)=='60d05195b0f51e6cb64dfd9b4fce5f290883c569dd23d8b7222c10203f4d783a'
    output=root/'dist/AICT-beta-rules-update-v2.1.zip'
    assert not output.exists() and not output.with_suffix('.zip.sha256').exists()
    incoming=root/'reports/AICT-beta-rules-update-v2'
    result=json.loads((incoming/'update-result.json').read_text('utf-8-sig'))
    assert not result['completed'] and not result['newServiceStarted'] and not result['rollbackBeforeServiceStart']
    assert 'InternalReplace' in result['error'] and 'Aict.Beta.Agent.dll' in result['error']
    with zipfile.ZipFile(original) as archive:
        frozen={n:archive.read(n) for n in archive.namelist()}
    for name,data in frozen.items():
        assert (incoming/name).read_bytes()==data,name
    metadata=json.loads(frozen['UPDATE.json'])
    script=(root/'scripts/Update-BetaRules.ps1').read_text('utf-8-sig').replace('UPDATE_METADATA_SHA256',sha(frozen['UPDATE.json'])).encode('utf-8-sig')
    build=root/'lab/build/rules-update-fix'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    (build/'Update-BetaRules.ps1').write_bytes(script)
    code=frozen['Sources/BetaRulesUpdate.cs'].decode('utf-8-sig')
    old_script_hash=sha(frozen['Update-BetaRules.ps1'])
    assert old_script_hash in code
    code=code.replace(old_script_hash,sha(script)).replace('专注计划 · 规则更新','专注计划 · 规则更新 v2.1')
    code=code.replace('p=="creating-runtime-directory"?"正在补齐目录":',
        'p=="preflight-file-operations"?"正在检查文件替换兼容性":p=="stopping-backend"?"正在暂停后台":p=="pausing-collectors"?"正在暂停采集连接":p=="copying-files"?"正在更新程序文件":p=="starting-backend"?"正在恢复后台和采集":p=="checking-health"?"正在确认新版状态":p=="creating-runtime-directory"?"正在补齐目录":')
    (build/'BetaRulesUpdate.cs').write_text(code,encoding='utf-8-sig')
    exe=build/'更新专注计划.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(exe),str(build/'BetaRulesUpdate.cs')],check=True,cwd=root)
    tests={};ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    def check(name,args):
        p=subprocess.run(args,cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=90)
        (build/(name+'.txt')).write_bytes(p.stdout)
        assert p.returncode==0,p.stdout.decode('utf-8','replace')
        tests[name]={'passed':True,'log':(build/(name+'.txt')).relative_to(root).as_posix(),'sha256':sha(p.stdout)}
    check('actual_ps51_filesystem_recovery',[ps,'-NoProfile','-File',str(root/'tests/Test-BetaUpdateFiles.ps1')])
    guard="$taskOk=$false;try{& '"+str(build/'Update-BetaRules.ps1').replace("'","''")+"'}catch{if($_.Exception.Message -match 'VMware guest required'){$taskOk=$true}else{throw}};if(-not $taskOk){throw 'Host was not refused'};Write-Output 'Fixed updater refused host before any repair writes'"
    check('host_script_refusal',[ps,'-NoProfile','-Command',guard])
    p=subprocess.run([str(exe),'--guard-check'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=15)
    assert p.returncode==1 and b'VMware guest required' in p.stderr
    files=dict(frozen)
    files.update({'Update-BetaRules.ps1':script,'更新专注计划.exe':exe.read_bytes(),'README.md':(root/'docs/RULES_UPDATE_V2_1.md').read_bytes(),
        '规则交互说明.md':frozen['README.md'],'Sources/BetaRulesUpdate.cs':(build/'BetaRulesUpdate.cs').read_bytes(),
        'Sources/Update-BetaRules-v2.1.ps1':script,'Sources/Test-BetaUpdateFiles.ps1':(root/'tests/Test-BetaUpdateFiles.ps1').read_bytes()})
    assert files['UPDATE.json']==frozen['UPDATE.json'] and files['TARGET-MANIFEST.json']==frozen['TARGET-MANIFEST.json']
    assert all(files['payload/'+n]==frozen['payload/'+n] and sha(files['payload/'+n])==expected for n,expected in metadata['files'].items())
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in files.items() if n!='SHA256SUMS.txt').encode('utf-8')
    with zipfile.ZipFile(output,'x',zipfile.ZIP_DEFLATED) as archive:
        for n,b in files.items():archive.writestr(n,b)
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None and all(archive.read(n)==b for n,b in files.items())
    with output.with_suffix('.zip.sha256').open('x',encoding='utf-8') as f:f.write(file_sha(output)+'  '+output.name+'\n')
    receipt={'kind':'AICT_RULES_UPDATE_V2_1_LOCAL_VALIDATION','package':output.relative_to(root).as_posix(),'sha256':file_sha(output),'bytes':output.stat().st_size,
        'checks':tests,'filesystem_recovery_cases':8,'gui_host_refused':True,'program_payload_identical_to_frozen_v2':True,
        'target_manifest_identical_to_v2':True,'database_replacement_in_payload':False,'guest_update_and_recovery':'NOT_RUN','host_system_mutations_performed':False}
    with (root/'reports/rules-update-v2.1-local-checks-20261103.json').open('x',encoding='utf-8') as f:json.dump(receipt,f,ensure_ascii=False,indent=2)
    audit={'kind':'AICT_RULES_V2_RETURNED_FAILURE_AUDIT','source':incoming.relative_to(root).as_posix(),'package_files_verified':len(frozen),'package_sha256':file_sha(original),
        'raw_result_sha256':file_sha(incoming/'update-result.json'),'raw_status_sha256':file_sha(incoming/'service-status-after-update.json'),
        'primary_failure':'PS51_FILE_REPLACE_NULL_BACKUP_ARGUMENT','secondary_failure':'ROLLBACK_COPIED_UNCHANGED_LOCKED_AGENT_DLL_AND_ABORTED',
        'reported_new_service_started':False,'reported_database_replaced':False,'reported_rollback_confirmed':False,
        'failure_precedes_program_payload_copy':True,'live_guest_backend_health_confirmed':False,'source_files_unchanged':True}
    for n,b in frozen.items():assert (incoming/n).read_bytes()==b
    with (root/'reports/rules-update-v2-failure-audit-20261103.json').open('x',encoding='utf-8') as f:json.dump(audit,f,ensure_ascii=False,indent=2)
    print(json.dumps(receipt,ensure_ascii=False))

if __name__=='__main__':main()
