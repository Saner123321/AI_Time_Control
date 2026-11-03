"""Read-only validation of the two returned v1 sessions; never awards browser or full product gates."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

def verify(folder, root):
    folder=Path(folder).resolve()
    sums={}
    for line in (folder/'SHA256SUMS.txt').read_text('utf-8-sig').splitlines():
        digest,name=line.split('  ',1)
        if '/' in name or '\\' in name or name in sums or name in ('.','..'):
            raise ValueError('Unsafe or repeated evidence name')
        if hashlib.sha256((folder/name).read_bytes()).hexdigest()!=digest:
            raise ValueError('Evidence hash mismatch: '+name)
        sums[name]=digest
    if set(sums)!={p.name for p in folder.iterdir() if p.is_file() and p.name!='SHA256SUMS.txt'}:
        raise ValueError('Evidence inventory mismatch')
    result=json.loads((folder/'session-result.json').read_text('utf-8-sig'))
    with zipfile.ZipFile(root/'dist/AICT-interactive-lab-v1.zip') as z:
        if hashlib.sha256(z.read('MANIFEST.json')).hexdigest()!=result['sourceManifestSha256']:
            raise ValueError('Frozen package source mismatch')
        manifest=json.loads(z.read('MANIFEST.json'))
    smoke=json.loads((folder/'workspace-smoke.json').read_text('utf-8-sig'))
    if smoke['runId']!=result['runId'] or not smoke['passed'] or not smoke['limitedToken']:
        raise ValueError('Limited smoke did not pass')
    launches=smoke['launches']
    deny=[p for p in launches if p['purpose']=='block-transition' and not p['started'] and p['nativeErrorCode']==1260]
    control=[p for p in launches if p['purpose']=='control' and p['started'] and p['exitCode']==0 and p['markerMatched']]
    restore=[p for p in launches if p['purpose']=='release-transition' and p['started'] and p['exitCode']==0 and p['markerMatched']]
    if not deny or not control or not restore:
        raise ValueError('Actual launch evidence incomplete')
    status=json.loads((folder/'workspace-status.json').read_text('utf-8-sig'))['status']
    actions=[json.loads(line) for line in (folder/'process-actions.jsonl').read_text('utf-8-sig').splitlines() if line]
    if not any(p['terminationRequested'] and p['processExitObserved'] and p['nativeErrorCode']==0 for p in actions):
        raise ValueError('Existing process exit not observed')
    expected='authenticode:sha256:'+manifest['target_sip_hash'][2:]
    return {'report':str(folder.relative_to(root)),'run_id':result['runId'],'stage':result['stage'],
            'source_manifest_sha256':result['sourceManifestSha256'],'files_verified':len(sums),
            'automatic_inert_application_checks_passed':True,'launches_recorded':len(launches),
            'already_running_inert_process_exit_observed':True,
            'current_rules':[{'name':g['name'],'enabled':g['enabled'],'cycle':g['cycle'],'contains_frozen_target_identity':expected in g['applicationIds']} for g in status['policy']['groups']],
            'current_system_state_is_snapshot_only':True,'effective_utc':status['effectiveUtc'],
            'current_verified_denied_ids':status['execution']['verifiedDeniedIdentities'],
            'manual_task_manager_tests_automatically_awarded':False,'website_tests_awarded':False,
            'source_sha256':sums}

if __name__=='__main__':
    root=Path(__file__).resolve().parents[1]
    print(json.dumps([verify(Path(arg),root) for arg in sys.argv[1:]],ensure_ascii=False,indent=2))
