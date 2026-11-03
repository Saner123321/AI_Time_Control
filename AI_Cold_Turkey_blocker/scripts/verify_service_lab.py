"""Audit service-only guest evidence, without equating heartbeat persistence to blocking."""
from __future__ import annotations
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import zipfile
from datetime import datetime

SERVICE_OPS={"Stop","Reconfigure","Delete","ChangeDacl","CustomControl","Start","TerminateSystemServiceProcess"}
FILE_OPS={"Write protection-canary.bin","Write config.json","Write state\\service-state.json"}


def verify(directory: Path, package: Path | None = None) -> dict:
    directory=directory.resolve()
    original={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    manifest={}
    for line in (directory/'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        match=re.fullmatch(r'([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]*)',line)
        if not match or match[2] in manifest:raise ValueError('Invalid or duplicate evidence manifest entry')
        manifest[match[2]]=match[1]
    if set(manifest)!=set(original)-{'SHA256SUMS.txt'} or any(original[k]!=v for k,v in manifest.items()):raise ValueError('Evidence set/hash mismatch')

    def read(name):
        if name not in manifest:raise ValueError('Missing evidence: '+name)
        value=json.loads((directory/name).read_text(encoding='utf-8-sig'))
        if not isinstance(value,dict):raise ValueError('Expected JSON object')
        return value

    runner=read('runner-result.json'); run_id=runner.get('RunId')
    if runner.get('Kind')!='AICT_SERVICE_LAB_RUNNER' or not isinstance(run_id,str) or not re.fullmatch('[a-f0-9]{32}',run_id):raise ValueError('Unexpected service runner identity')
    stage=runner.get('Stage')
    if stage not in ('Prepare','Finish'):raise ValueError('Unknown service evidence stage')
    checks={
        'vm_metadata':runner.get('VmwareGuestVerified') is True and bool(runner.get('EditionId')) and bool(runner.get('Build')) and bool(runner.get('SnapshotReferenceOperatorSupplied')),
        'honest_scope':runner.get('NoBlockingPolicyApplied') is True and runner.get('SnapshotRestoreVerified') is False and runner.get('BlockingAcrossBootProven',False) is False,
        'runner_errors_absent':runner.get('Error') is None and runner.get('CleanupError') is None and not runner.get('ProbeExportErrors') and not runner.get('StateSnapshotUnavailable'),
    }
    binary_match=False
    if package is not None:
        with zipfile.ZipFile(package) as bundle:
            release=json.loads(bundle.read('MANIFEST.json')); raw=bundle.read('lab/AictServiceLab.exe')
            binary_match=release.get('kind')=='AICT_SERVICE_BOOT_LAB_V1' and hashlib.sha256(raw).hexdigest()==runner.get('WorkerSha256')==release['files']['lab/AictServiceLab.exe']
        checks['frozen_binary_match']=binary_match

    before,after=[read(n) for n in ('inspect-before-crash.json','inspect-after-crash.json')]
    install,crash=[read(n) for n in ('install-result.json','crash-request.json')]
    reports=[runner,before,after,install,crash]

    def inspection(value):
        h=value.get('Heartbeat',{})
        try:boot=datetime.fromisoformat(h.get('BootTimeUtc',''))
        except (ValueError,TypeError):return False
        return (value.get('Kind')=='AICT_SERVICE_LAB_INSPECTION' and value.get('State')==4 and type(value.get('ProcessId')) is int and value['ProcessId']>0
                and value.get('AutoStart') is True and value.get('ImageAndAccountMatch') is True and value.get('SecurityMatches') is True
                and value.get('Recovery',{}).get('MatchesFixedRecovery') is True and h.get('Kind')=='AICT_SERVICE_LAB_HEARTBEAT' and h.get('RunId')==run_id
                and h.get('LocalSystem') is True and h.get('ServicePhase')=='Running' and h.get('ProcessId')==value['ProcessId'] and boot.tzinfo is not None
                and isinstance(h.get('InstanceId'),str) and bool(re.fullmatch('[a-f0-9]{32}',h['InstanceId']))
                and type(h.get('PersistentStartCount')) is int and h['PersistentStartCount']>0 and type(h.get('PersistentHeartbeatCount')) is int and h['PersistentHeartbeatCount']>=0)

    def probe(phase,snapshot):
        value=read('probe-probe-'+phase+'.json');reports.append(value)
        rows=value.get('Checks',[])
        valid=(value.get('Kind')=='AICT_SERVICE_LAB_LIMITED_PROBE' and value.get('Phase')==phase and value.get('LimitedToken') is True
               and value.get('InteractiveSession') is True and value.get('ProtectedLayoutAclSafe') is True and value.get('PermissionsPassed') is True
               and value.get('NoStopDeleteKillOrFileBytesWritten') is True and value.get('ProcessIdObserved')==snapshot.get('ProcessId')
               and len(rows)==10 and {r.get('Operation') for r in rows}==SERVICE_OPS|FILE_OPS)
        for row in rows:
            if row.get('Operation') in SERVICE_OPS:
                valid &= row.get('Denied') is True and type(row.get('NativeErrorCode')) is int and row['NativeErrorCode']==5
            elif row.get('Operation') in FILE_OPS:
                valid &= all(row.get(k) is True for k in ('Denied','OwnerSystemOrAdministrators','AclSafe')) and row.get('UnexpectedFailure') is None
        return valid

    checks['install_readback']=install.get('Kind')=='AICT_SERVICE_LAB_INSTALL' and all(install.get(k) is True for k in ('Installed','SecurityVerified','RecoveryVerified','NoBlockingPolicyApplied'))
    checks['inspections']=inspection(before) and inspection(after)
    checks['permissions_before_crash']=probe('before-crash',before)
    checks['permissions_after_crash']=probe('after-crash',after)
    bh,ah=before.get('Heartbeat',{}),after.get('Heartbeat',{})
    checks['one_crash_recovered']=(crash.get('Kind')=='AICT_SERVICE_LAB_CRASH_REQUEST' and crash.get('OneTimeCrashRequested') is True and before.get('ProcessId')!=after.get('ProcessId') and bh.get('InstanceId')!=ah.get('InstanceId')
                                   and bh.get('BootTimeUtc')==ah.get('BootTimeUtc') and type(bh.get('PersistentStartCount')) is int and ah.get('PersistentStartCount')==bh['PersistentStartCount']+1
                                   and type(bh.get('PersistentHeartbeatCount')) is int and type(ah.get('PersistentHeartbeatCount')) is int and ah['PersistentHeartbeatCount']>=bh['PersistentHeartbeatCount'])
    prepare=read('prepare-summary.json');reports.append(prepare)
    observed=prepare.get('RecoveryObservedSeconds')
    checks['preparation_summary']=(prepare.get('Stage')=='Prepare' and all(prepare.get(k) is True for k in ('PreparePassed','PermissionsPassed','CrashRecoveryPassed','CleanupTaskRegistered','ServiceLeftForReboot','NoBlockingPolicyApplied'))
                                   and prepare.get('Error') is None and type(observed) in (int,float) and math.isfinite(observed) and 0<observed<30)
    preparation_pass=all(value for key,value in checks.items() if key!='runner_errors_absent')
    boot=False;cleanup=False
    if stage=='Finish':
        post_names=[n for n in manifest if re.fullmatch(r'inspect-after-boot-[0-9TZ]+\.json',n)]
        if len(post_names)!=1:raise ValueError('Exactly one post-reboot inspection required')
        post=read(post_names[0]);reports.append(post);ph=post.get('Heartbeat',{})
        checks['post_boot_inspection']=inspection(post)
        checks['post_boot_permissions']=probe('after-boot',post)
        checks['actual_new_boot']=(bool(ph.get('BootTimeUtc')) and ph.get('BootTimeUtc')!=ah.get('BootTimeUtc') and ph.get('InstanceId')!=ah.get('InstanceId'))
        checks['test_state_persistence']=(type(ah.get('PersistentStartCount')) is int and ph.get('PersistentStartCount')==ah['PersistentStartCount']+1
                                          and type(ah.get('PersistentHeartbeatCount')) is int and type(ph.get('PersistentHeartbeatCount')) is int and ph['PersistentHeartbeatCount']>=ah['PersistentHeartbeatCount'])
        boot=preparation_pass and all(checks[k] for k in ('post_boot_inspection','post_boot_permissions','actual_new_boot','test_state_persistence'))
        clean_names=[n for n in manifest if re.fullmatch(r'cleanup-finish-[0-9TZ]+\.json',n)]
        if len(clean_names)!=1:raise ValueError('Final owned-service cleanup evidence missing')
        clean=read(clean_names[0]);reports.append(clean)
        cleanup=(clean.get('Kind')=='AICT_SERVICE_LAB_CLEANUP' and clean.get('ServiceAbsent') is True and clean.get('NoBlockingPolicyApplied') is True
                 and runner.get('ServiceAbsent') is True and runner.get('CleanupTaskRemoved') is True)
        checks['cleanup_complete']=cleanup
        checks['finish_flags']=all(runner.get(k) is True for k in ('PreparePassed','CrashRecoveryPassed','PermissionsPassed','BootObserved','ServiceAutostartObserved','TestStatePersisted','FinishPassed'))
    for value in reports:
        if value.get('RunId')!=run_id:raise ValueError('Report run mismatch')
    if {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}!=original:raise ValueError('Source evidence changed during audit')
    return {'kind':'AICT_SERVICE_LAB_AUDIT_RECEIPT','run_id':run_id,'stage':stage,'preparation_pass':preparation_pass,'service_permissions_pass':preparation_pass,
            'service_crash_recovery_pass':preparation_pass,'service_autostart_and_test_state_pass':boot,'cleanup_pass':cleanup,
            'complete_lab_pass':stage=='Finish' and all(checks.values()),'blocking_across_boot_proven':False,'quota_persistence_proven':False,'phase0_gate_awarded':False,
            'binary_matches_frozen_package':binary_match,'checks':checks,'artifacts':manifest}


def main():
    if len(sys.argv)!=2:
        print('Usage: python scripts/verify_service_lab.py reports/service-lab-finish-TIMESTAMP');return 1
    try:
        result=verify(Path(sys.argv[1]),Path(__file__).resolve().parents[1]/'dist/AICT-service-boot-lab-v1.zip')
        print(json.dumps(result,ensure_ascii=False,indent=2));return 0 if result['complete_lab_pass'] else 2
    except (OSError,ValueError,KeyError,TypeError,AttributeError,zipfile.BadZipFile) as error:
        print('SERVICE_EVIDENCE_INVALID_OR_INCOMPLETE: '+str(error));return 1


if __name__=='__main__':raise SystemExit(main())
