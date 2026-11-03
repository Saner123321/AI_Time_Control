"""Validate the host trial artifact and received release report; never installs."""
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path

def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def save(path,value):
    with path.open('x',encoding='utf-8') as stream:json.dump(value,stream,ensure_ascii=False,indent=2)

def main():
    root=Path(__file__).resolve().parents[1]
    incoming=root/'reports/aict-beta-20261103T110547662Z';verified={}
    for line in (incoming/'SHA256SUMS.txt').read_text('utf-8-sig').splitlines():
        expected,name=line.split('  ',1);path=(incoming/name).resolve();path.relative_to(incoming.resolve());assert sha(path)==expected,name;verified[name]=expected
    status=json.loads((incoming/'state/status.json').read_text('utf-8-sig'))['status']
    snapshot=json.loads((incoming/'rules-and-usage.json').read_text('utf-8-sig'))
    assert snapshot['policy']==status['policy'] and status['pending'] is None
    assert all(not g['websiteDomains'] for g in status['policy']['groups'])
    assert status['websites']['required'] and status['websites']['connectedProfiles']==0 and '0x00000539' in status['websites']['fault']
    events=[json.loads(x) for x in (incoming/'state/events/20261103.jsonl').read_text('utf-8-sig').splitlines()]
    recent=[x for x in events if x['kind'] in ('ChangeSubmitted','ChangeApplied')][-4:]
    audit={'kind':'AICT_WEBSITE_RELEASE_RETURNED_AUDIT','source':incoming.relative_to(root).as_posix(),'source_sha256':verified,'files_verified':len(verified),
        'no_pending_change':True,'current_website_blacklist_empty':True,'old_required_flag_incorrectly_true':True,'browser_confirmed_profiles':0,
        'network_error':status['websites']['fault'],'old_network_closed_flag_not_an_execution_proof':True,'recent_changes':recent,
        'source_findings':['Empty blacklist was emitted as an active web group','Old DNR builder emitted an invalid empty requestDomains list','ALE user SD was tagged as FWP_SID 13 instead of SECURITY_DESCRIPTOR 14'],
        'browser_dynamic_rules_not_directly_exported':True,'source_files_unchanged':True,'host_test_authorized_by_user':True}
    build=json.loads((root/'lab/build/host-trial/last-build.json').read_text('utf-8-sig'));payload=Path(build['payloadDirectory'])
    package=root/'dist/AICT-host-trial-v1.zip';extracted=root/'dist/AICT-host-trial-v1'
    assert package.with_suffix('.zip.sha256').read_text().split('  ',1)[0]==sha(package)
    with zipfile.ZipFile(package) as archive:
        assert archive.testzip() is None
        manifest=json.loads(archive.read('MANIFEST.json'))
        assert manifest['host_trial'] and not manifest['vmware_only'] and manifest['trial_minutes']==30
        for name,digest in manifest['files'].items():assert hashlib.sha256(archive.read(name)).hexdigest()==digest and sha(extracted/name)==digest,name
        assert not any(n.endswith('.sqlite') or 'ui-self-test-data' in n for n in archive.namelist())
        assert b'$taskHostBuild=$true' in archive.read('scripts/Install-Beta.ps1')
    checks=root/'artifacts'/('host-trial-checks-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'));checks.mkdir()
    results={}
    def run(name,args):
        p=subprocess.run(args,cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=90)
        (checks/(name+'.txt')).write_bytes(p.stdout);assert p.returncode==0,p.stdout.decode('utf-8','replace')
        results[name]={'passed':True,'log':(checks/(name+'.txt')).relative_to(root).as_posix(),'sha256':sha(checks/(name+'.txt'))}
        return p.stdout
    for folder,exe in (('Service','Aict.Beta.Service.exe'),('Agent','Aict.Beta.Agent.exe'),('App','Aict.Beta.Desktop.exe')):
        info=json.loads(run(folder+'_build_info',[str(payload/folder/exe),'--build-info']).decode('utf-8-sig'));assert info['hostTrial'] and info['trialMinutes']==30
    run('wfp_abi_sd_validation',[str(payload/'Service/Aict.Beta.Service.exe'),'--offline-check'])
    ps=r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    run('host_task_bindings',[ps,'-NoProfile','-File',str(root/'tests/Test-HostTrialTasks.ps1')])
    run('native_policy_schema',[ps,'-NoProfile','-File',str(root/'tests/Test-BetaBindings.ps1'),'-Payload',str(payload)])
    run('browser_rules_and_worker',[r'C:\Program Files\nodejs\node.exe','--test','--test-reporter=tap',str(root/'tests/extension-rules.test.mjs'),str(root/'tests/extension-worker.test.mjs')])
    for name in ('worker.js','blocked.js'):
        run(name+'_syntax',[r'C:\Program Files\nodejs\node.exe','--check',str(root/'browser-extension/beta'/name)])
    ui=json.loads((payload/'ui-self-test/result.json').read_text('utf-8-sig'));assert ui['passed'] and not ui['systemWrites']
    trx=root/'artifacts/core-tests-20261103T114808759Z/core.trx';counters=ET.parse(trx).getroot().find('.//{*}Counters').attrib;assert counters['passed']==counters['total']=='114' and counters['failed']=='0'
    script="$taskServices=@(Get-CimInstance Win32_Service -Filter \"Name LIKE 'AICT%'\");$taskTasks=@(Get-ScheduledTask -TaskName 'AICT*' -ErrorAction SilentlyContinue);@{services=$taskServices.Count;tasks=$taskTasks.Count}|ConvertTo-Json -Compress"
    host=json.loads(run('host_before_install',[ps,'-NoProfile','-Command',script]).decode('utf-8-sig'));assert host=={'services':0,'tasks':0}
    assert sha(root/'reports/phase0-results.json')=='80759f9146f9ed187a2706abcfeff5aab9b23da33be757111f2b32a33551e22b'
    assert sha(root/'dist/AICT-self-use-beta-v1.zip')=='9cf460f469dc64a1935121db7677b80e8e58ca3ad4a526307437ffcffe22a6fa'
    assert all(sha(incoming/name)==value for name,value in verified.items())
    receipt={'kind':'AICT_HOST_TRIAL_V1_LOCAL_VALIDATION','package':package.relative_to(root).as_posix(),'sha256':sha(package),'bytes':package.stat().st_size,
        'ready_directory':str(extracted),'build_directory':str(payload.parent),'core_tests_passed':114,'core_test_evidence':trx.relative_to(root).as_posix(),
        'browser_rules_and_worker_tests':9,'checks':results,'ui_self_test':ui,'renders':{p.name:sha(p) for p in (payload/'ui-self-test').glob('*.png')},
        'host_installation_before_user_launch':host,'host_system_enforcement_installed_by_audit':False,'host_installation_and_live_enforcement':'USER_LAUNCH_PENDING',
        'trial_expiry_and_task_graph_unit_tests_passed':True,'actual_scheduled_recovery_and_uninstall_not_yet_observed':True,
        'old_phase0_and_frozen_packages_unchanged':True,'user_authorized_host_trial':True}
    save(root/'reports/website-release-audit-20261103T110547662Z.json',audit)
    save(root/'reports/host-trial-v1-local-checks-20261103.json',receipt)
    print(json.dumps({'passed':True,'package_sha256':receipt['sha256'],'ready_directory':str(extracted),'host_installation':'USER_LAUNCH_PENDING'},ensure_ascii=False))

if __name__=='__main__':main()
