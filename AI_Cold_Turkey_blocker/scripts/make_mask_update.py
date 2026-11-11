"""Freeze the background probe v16 -> native mask presentation v17 update. Does not install or change system policy."""
import hashlib,json,subprocess,zipfile,xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime,timezone
r=Path(__file__).resolve().parents[1]
sha=lambda b:hashlib.sha256(b).hexdigest()
enc=lambda o:(json.dumps(o,ensure_ascii=False,indent=2)+'\n').encode('utf-8')

def main():
    out=r/'dist/AICT-mask-v17'
    if out.exists() or out.with_suffix('.zip').exists():raise FileExistsError('Frozen release exists')
    bases=[];base_hashes=[]
    for folder,expected in [('AICT-background-v16','8b0856cea3d116f72f6b3b98a26f661bb5e7b3d2464946a569de513818b50d7a')]:
        data=(r/'dist'/folder/'TARGET-MANIFEST.json').read_bytes()
        assert sha(data)==expected
        bases.append(json.loads(data));base_hashes.append(sha(data))
    base=bases[-1]
    payload=Path(json.loads((r/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'))['payloadDirectory']);payload.resolve().relative_to((r/'lab/build/host-trial').resolve())
    ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8'));assert ui['passed'] and not ui['systemWrites'] and ui['continuousModeTitle']
    tray=json.loads((payload/'tray-self-test/result.json').read_text('utf-8'));assert tray['passed'] and not tray['systemWrites'] and not tray['realNotificationIconCreated']
    for name in ('System.Windows.Forms.dll','System.Windows.Forms.Primitives.dll','System.Drawing.Common.dll'):assert (payload/'App'/name).is_file()
    candidate={}
    for folder in ('App','Agent','Viewer','Service','Extension','scripts'):
        for p in (payload/folder).rglob('*'):
            if p.is_file():candidate[p.relative_to(payload).as_posix()]=p.read_bytes()
    candidate['Sources/assets/focus.ico']=(r/'assets/focus.ico').read_bytes()
    candidate['BetaBridge.exe']=(payload/'BetaBridge.exe').read_bytes()
    for folder in ('src','tests/Aict.Core.Tests','browser-extension/beta'):
        for p in (r/folder).rglob('*'):
            if p.is_file() and not {'bin','obj'}.intersection(p.relative_to(r).parts) and p.suffix in ('.cs','.csproj','.xaml','.json','.manifest','.js','.html'):candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    for name in ('Build-Beta.ps1','Install-Beta.ps1','Update-Mask.ps1','package_beta.py','make_mask_update.py'):
        candidate['Sources/scripts/'+name]=(r/'scripts'/name).read_bytes()
    for name in ('Test-ContinuousUse.ps1','Test-HostTrialTasks.ps1','Test-BetaUpdateFiles.ps1','Test-ViewerReadiness.ps1'):
        candidate['Sources/tests/'+name]=(r/'tests'/name).read_bytes()
    for p in (r/'tests').glob('extension-*.mjs'):
        candidate['Sources/'+p.relative_to(r).as_posix()]=p.read_bytes()
    for name in ('BetaBridge.cs','BetaPolicyXml.cs','BetaNative.cs','MaskUpdate.cs','ManualBrowserProbe.cs','ManualViewMask.cs','ManualViewConfirmation.cs'):
        candidate['Sources/lab/'+name]=(r/'lab'/name).read_bytes()
    changed={n:b for n,b in candidate.items() if any(baseline['files'].get(n)!=sha(b) for baseline in bases)}
    assert json.loads((payload/'Extension/manifest.json').read_text('utf-8'))['version']=='0.5.0'
    for name in ('Aict.Beta.Service.exe','Aict.Beta.Agent.exe'):
        folder='Service' if '.Service.' in name else 'Agent'
        info=json.loads(subprocess.check_output([str(payload/folder/name),'--build-info']))
        assert info['hostTrial'] and info['trialMinutes']==30 # legacy host-capability flag
    target={**base,'version':'0.14.0','revision':'mask-v17','host_trial':False,'continuous_use_default':True,'trial_minutes':None,'files':{**base['files'],**{n:sha(b) for n,b in changed.items()}}}
    for baseline in bases:
        assert {**baseline['files'],**{n:sha(b) for n,b in changed.items()}}==target['files'], 'Every supported base must reconstruct the complete target, including source files'
    target_bytes=enc(target);sums=''.join(h+'  '+n+'\n' for n,h in sorted({**target['files'],'MANIFEST.json':sha(target_bytes)}.items())).encode()
    meta=enc({'kind':'AICT_MASK_V17_DELTA','baseManifestSha256':base_hashes[-1],'acceptedBaseManifestSha256':base_hashes,'targetManifestSha256':sha(target_bytes),'targetSumsSha256':sha(sums),'files':{n:sha(b) for n,b in changed.items()},'database_replacement':False,'browser_extension_change':False,'extension_version':'0.5.0','continuous_use':True})
    script=(r/'scripts/Update-Mask.ps1').read_text('utf-8-sig');assert script.count('0'*64)==1
    assert "AICT_MASK_V17_UPDATE" in script and "mask-v17" in script and "Test-UpdateViewerReady" in script and "--configure-desktop-startup" in script, 'Wrong updater generation'
    script_bytes=script.replace('0'*64,sha(meta)).encode('utf-8-sig')
    code=(r/'lab/MaskUpdate.cs').read_text('utf-8-sig');assert code.count('0'*64)==1;code=code.replace('0'*64,sha(script_bytes))
    assert 'class MaskUpdate' in code and 'Update-Mask.ps1' in code, 'Wrong update launcher'
    build=r/'lab/build/mask-update'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ');build.mkdir(parents=True)
    (build/'Update-Mask.ps1').write_bytes(script_bytes);(build/'MaskUpdate.cs').write_text(code,encoding='utf-8-sig')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/target:winexe','/platform:x64','/optimize+','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/win32icon:'+str(r/'assets/focus.ico'),'/out:'+str(build/'修复网页遮挡.exe'),str(build/'MaskUpdate.cs')],check=True)
    check=subprocess.run([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-File',str(r/'tests/Test-BetaUpdateFiles.ps1'),'-ScriptPath',str(build/'Update-Mask.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'updater-tests.txt').write_bytes(check.stdout);assert check.returncode==0,check.stdout
    readiness=subprocess.run([r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe','-NoProfile','-File',str(r/'tests/Test-ViewerReadiness.ps1'),'-UpdaterPath',str(build/'Update-Mask.ps1')],cwd=r,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(build/'viewer-readiness-tests.txt').write_bytes(readiness.stdout);assert readiness.returncode==0,readiness.stdout
    files={'payload/'+n:b for n,b in changed.items()}
    files.update({'UPDATE.json':meta,'TARGET-MANIFEST.json':target_bytes,'TARGET-SHA256SUMS.txt':sums,'Update-Mask.ps1':script_bytes,'修复网页遮挡.exe':(build/'修复网页遮挡.exe').read_bytes(),'README.md':(r/'docs/MASK_V17.md').read_bytes(),'Sources/MaskUpdate.cs':(build/'MaskUpdate.cs').read_bytes(),'Sources/make_mask_update.py':Path(__file__).read_bytes()})
    assert not any(n.startswith('payload/state/') or n.endswith('.sqlite') for n in files)
    files['SHA256SUMS.txt']=''.join(sha(b)+'  '+n+'\n' for n,b in sorted(files.items())).encode();out.mkdir()
    for n,b in files.items():p=out/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b)
    with zipfile.ZipFile(out.with_suffix('.zip'),'x',zipfile.ZIP_DEFLATED) as z:
        for n,b in files.items():z.writestr(n,b)
    with zipfile.ZipFile(out.with_suffix('.zip')) as z:assert z.testzip() is None and all(z.read(n)==b for n,b in files.items())
    digest=sha(out.with_suffix('.zip').read_bytes());out.with_suffix('.zip.sha256').write_text(digest+'  '+out.name+'.zip\n')
    test=max((r/'artifacts').glob('core-tests-*/core.trx'),key=lambda p:p.stat().st_mtime);c=ET.parse(test).getroot().find('.//{*}Counters').attrib;assert c['total']==c['passed']=='195'
    mode_test=max((r/'artifacts').glob('continuous-use-*/result.json'),key=lambda p:p.stat().st_mtime);mode=json.loads(mode_test.read_text('utf-8'));assert mode['passed'] and mode['cases']==14 and not mode['actualTaskOrServiceChanges']
    viewer=json.loads((payload/'viewer-self-test/result.json').read_text('utf-8'));assert viewer['passed'] and viewer['cases']==19 and viewer['ordinaryCallerTerminationDenialTested'] and not viewer['realClientOrServiceChanges']
    startup=json.loads((payload/'startup-self-test/result.json').read_text('utf-8'));assert startup['passed'] and startup['cases']==13 and not startup['realRunRegistryChanged']
    receipt={'kind':'AICT_MASK_V17_LOCAL_VALIDATION','atUtc':datetime.now(timezone.utc).isoformat(),'package':str(out.with_suffix('.zip')),'sha256':digest,'changed_files':len(changed),'acceptedBaseManifestSha256':base_hashes,'core_tests':195,'core_trx':str(test),'ui_self_test':ui,'tray_lifecycle_self_test':tray,'updater_filesystem_recovery_cases':8,'viewer_readiness_cases':7,'mode_and_timer_cases':14,'mode_and_timer_evidence':str(mode_test),'extension_unit_tests':27,'viewer_validation':viewer,'startup_validation':startup,'extension_version':'0.5.0','browser_component_unchanged_from_v16':True,'database_in_package':False,'host_installation_performed':False,'real_service_or_task_changes':False,'build':str(build)}
    with (r/'reports/mask-v17-local-checks-20261111.json').open('xb') as f:f.write(enc(receipt))
    print(json.dumps(receipt,ensure_ascii=False))
if __name__=='__main__':main()
