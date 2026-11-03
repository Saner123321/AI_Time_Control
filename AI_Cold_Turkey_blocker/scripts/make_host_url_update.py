"""Freeze an update for the existing, authorized host installation. Does not install it."""
import hashlib,json,subprocess,zipfile
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def digest(p):return sha(p.read_bytes())
def main():
    name='AICT-host-url-update-v4';out=r/'dist'/name
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen update already exists')
    payload=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'))['payloadDirectory'])
    assert json.loads((payload/'ui-self-test/result.json').read_text('utf-8'))['passed']
    original=json.loads((r/'dist/AICT-host-trial-v1/MANIFEST.json').read_text('utf-8'))
    base=(r/'dist/AICT-host-trial-v1/MANIFEST.json').read_bytes()
    assert sha(base)=='7897c827ac7499c97f863f32a580b966a54df7887be2a2a09bd9c16db36d1fee'
    candidates={}
    for folder in ('Service','Agent','App','Extension','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file():candidates[p.relative_to(payload).as_posix()]=p.read_bytes()
    for folder in ('src','tests/Aict.Core.Tests','browser-extension/beta'):
        for p in (r/folder).rglob('*'):
            if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(r).parts) and p.suffix in ('.cs','.csproj','.json','.xaml','.js','.html','.manifest'):candidates['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    changed={n:b for n,b in candidates.items() if original['files'].get(n)!=sha(b)}
    target={**original,'version':'0.2.0','revision':'url-patterns-v4','files':{**original['files'],**{n:sha(b) for n,b in changed.items()}}}
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_HOST_URL_V4_DELTA','baseManifestSha256':sha(base),'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False})
    script=(r/'scripts/Update-BetaRules.ps1').read_text('utf-8-sig')
    start=script.index('$taskMachine=');end=script.index('$taskIdentity=')
    script=script[:start]+script[end:]
    script=script.replace('111e0fe3cfa348b4a055eafb99e25f06','32c8bab7ba164a01b5988c4c7c03554f').replace("'rules-v2'","'url-patterns-v4'").replace('UPDATE_METADATA_SHA256',sha(meta))
    script=script.replace('AICT_BETA_RULES_V2_UPDATE','AICT_HOST_URL_V4_UPDATE').replace("updaterVersion='2.1'","updaterVersion='4.0'")
    script=script.replace("$taskAgentName='AICT-Beta-Agent-32c8bab7ba164a01b5988c4c7c03554f'", """$taskAgentName='AICT-Beta-Agent-32c8bab7ba164a01b5988c4c7c03554f'
$taskSafetyName='AICT-Beta-TrialSafety-32c8bab7ba164a01b5988c4c7c03554f'
$taskSafetyPaused=$false;$taskSafetyTrigger=$null
""")
    script=script.replace("if($taskConfig.runId -cne", "if(-not $taskConfig.hostTrial){throw 'This update requires the authorized host trial.'};if($taskConfig.runId -cne")
    script=script.replace("    Set-UpdatePhase 'stopping-backend'", """    $taskSafety=Get-ScheduledTask -TaskName $taskSafetyName
    if(@($taskSafety.Actions).Count -ne 1 -or $taskSafety.Actions[0].Execute -ine (Join-Path $taskRoot 'Service\\Aict.Beta.Service.exe') -or $taskSafety.Actions[0].Arguments -cne '--host-trial-safety' -or $taskSafety.Principal.UserId -notin @('SYSTEM','S-1-5-18') -or $taskSafety.State -eq 'Running'){throw 'Independent safety task ownership/state mismatch.'}
    $taskSafetyTrigger=$taskSafety.Triggers
    $null=Disable-ScheduledTask -TaskName $taskSafetyName;$taskSafetyPaused=$true
    Set-UpdatePhase 'stopping-backend'""")
    script=script.replace("    Set-UpdatePhase 'copying-files'", """    Copy-Item -LiteralPath (Join-Path $taskRoot 'config.json') -Destination (Join-Path $taskBackup 'config.json')
    $taskModifiedFiles.Add('config.json')
    $taskConfig.hostTrialEndsUtc=[DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o')
    Write-UpdateJson (Join-Path $taskRoot 'config.json') $taskConfig
    $null=Set-ScheduledTask -TaskName $taskSafetyName -Trigger (New-ScheduledTaskTrigger -Once -At ([DateTimeOffset]::Parse($taskConfig.hostTrialEndsUtc).AddMinutes(1).LocalDateTime))
    Set-UpdatePhase 'copying-files'""")
    script=script.replace("    $taskStatus.completed=$true;", "    $null=Enable-ScheduledTask -TaskName $taskSafetyName;$taskSafetyPaused=$false\n    $taskStatus.completed=$true;")
    script=script.replace("    Restore-UpdateAfterFailure", """    Restore-UpdateAfterFailure
    if($taskSafetyPaused){try{if(-not $taskStatus.newServiceStartAttempted -and $null -ne $taskSafetyTrigger){$null=Set-ScheduledTask -TaskName $taskSafetyName -Trigger $taskSafetyTrigger};$null=Enable-ScheduledTask -TaskName $taskSafetyName;$taskSafetyPaused=$false}catch{$taskStatus.rollbackErrors+=('Restore safety task: '+$_.Exception.Message)}}""")
    build=r/'lab/build/host-url-update'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    script_bytes=script.encode('utf-8-sig');(build/'Update-HostUrls.ps1').write_bytes(script_bytes)
    code=(r/'lab/HostUrlUpdate.cs').read_text('utf-8-sig').replace('UPDATE_SCRIPT_SHA256',sha(script_bytes))
    (build/'HostUrlUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/out:'+str(build/'更新网址功能.exe'),str(build/'HostUrlUpdate.cs')],check=True)
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    check=subprocess.run([ps,'-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-HostUrls.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
    (build/'updater-filesystem-tests.txt').write_bytes(check.stdout)
    if check.returncode:raise RuntimeError(check.stdout.decode('utf-8','replace'))
    files={'payload/'+n:b for n,b in changed.items()}
    files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-HostUrls.ps1':script_bytes,'更新网址功能.exe':(build/'更新网址功能.exe').read_bytes(),'website-draft.json':(r/'artifacts/host-website-draft-20261103.json').read_bytes(),'README.md':(r/'docs/URL_PATTERNS_V4.md').read_bytes(),'Sources/HostUrlUpdate.cs':(build/'HostUrlUpdate.cs').read_bytes(),'Sources/make_host_url_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode()
    out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    out.with_suffix('.zip.sha256').write_text(digest(out.with_suffix('.zip'))+'  '+name+'.zip\n')
    print(json.dumps({'folder':str(out),'sha256':digest(out.with_suffix('.zip')),'changed_files':len(changed),'build':str(build)},ensure_ascii=False))
if __name__=='__main__':main()
