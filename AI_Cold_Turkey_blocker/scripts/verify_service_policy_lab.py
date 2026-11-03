"""Read-only verification of the VM service/AppLocker integration contract, never system/phase0 approval."""
from __future__ import annotations
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile

PHASES=("baseline","blocked","during-crash","after-crash","restored")
PATHS=(r"Targets\Target.exe",r"Targets\Renamed.exe","MovedTarget.exe",r"Targets\AllowedControl.exe")
OPS={"Stop","Reconfigure","Delete","ChangeDacl","CustomControl","Start","TerminateService","Write config.json","Write protection-canary.bin"}


def verify(directory: Path, package: Path | None=None) -> dict:
    directory=directory.resolve()
    original={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    manifest={}
    for line in (directory/'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        match=re.fullmatch(r'([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]*)',line)
        if not match or match[2] in manifest: raise ValueError('Invalid/duplicate evidence manifest entry')
        manifest[match[2]]=match[1]
    if set(manifest)!=set(original)-{'SHA256SUMS.txt'} or any(original[k]!=v for k,v in manifest.items()): raise ValueError('Evidence set/hash mismatch')
    def read(name):
        if name not in manifest: return {}
        value=json.loads((directory/name).read_text(encoding='utf-8-sig'))
        if not isinstance(value,dict): raise ValueError('Expected JSON object')
        return value
    runner=read('runner-result.json');run=runner.get('runId')
    if runner.get('kind')!='AICT_SERVICE_POLICY_LAB_RUNNER' or not isinstance(run,str) or not re.fullmatch('[a-f0-9]{32}',run): raise ValueError('Wrong integration runner identity')
    def boolean(value): return value is True
    checks={
        'vm_metadata':boolean(runner.get('vmwareGuestVerified')) and bool(runner.get('editionId')) and bool(runner.get('build')),
        'honest_scope':boolean(runner.get('fixedFixtureOnly')) and boolean(runner.get('snapshotRestoreOperatorConfirmed')) and bool(runner.get('snapshotReference')) and runner.get('snapshotRestoreIndependentlyVerified') is False and runner.get('phase0PassAwarded') is False,
        'runner_errors_absent':runner.get('error') is None and runner.get('cleanupError') is None and runner.get('exportRefusedFiles')==[],
        'frozen_source_match':False,
    }
    if package is not None:
        with zipfile.ZipFile(package) as zipped:
            raw=zipped.read('MANIFEST.json');release=json.loads(raw)
            checks['frozen_source_match']=release.get('kind')=='AICT_SERVICE_POLICY_LAB_V1' and hashlib.sha256(raw).hexdigest()==runner.get('sourceManifestSha256') and all(hashlib.sha256(zipped.read(name)).hexdigest()==expected for name,expected in release.get('files',{}).items()) and bool(release.get('files'))
    probes={p:read('probe-'+p+'.json') for p in PHASES}
    for phase,probe in probes.items():
        rows=probe.get('trials',[]);expected=Counter((path,rep) for path in PATHS for rep in (1,2,3))
        shape=bool(isinstance(rows,list) and len(rows)==12 and Counter((r.get('relativePath'),r.get('repetition')) for r in rows)==expected)
        good=shape
        for row in rows:
            block=phase not in ('baseline','restored') and row.get('relativePath')!=PATHS[-1]
            outcome=(row.get('started') is False and row.get('nativeErrorCode')==1260) if block else (row.get('started') is True and row.get('markerMatched') is True and row.get('nativeErrorCode')==0)
            good=good and row.get('expectedBlocked') is block and row.get('expectationMet') is True and outcome and type(row.get('startTick')) is int and type(row.get('endTick')) is int and row['endTick']>=row['startTick']
        checks['launch_'+phase]=bool(good and probe.get('runId')==run and probe.get('phase')==phase and probe.get('limitedToken') is True and probe.get('passed') is True)
        if phase in ('blocked','after-crash'):
            rights=probe.get('permissions',{});observations=rights.get('checks',[])
            checks['permissions_'+phase]=(rights.get('passed') is True and rights.get('noStopKillDeleteOrFileBytesWritten') is True and len(observations)==len(OPS) and {r.get('operation') for r in observations}==OPS
                and all(r.get('denied') is True and (r.get('nativeErrorCode')==5 if 'nativeErrorCode' in r else r.get('unexpectedFailure') is None) for r in observations))
    during=probes['during-crash'].get('trials',[])
    checks['denial_during_observed_service_outage']=any(r.get('serviceState') in (1,2,3) and r.get('expectedBlocked') is True and r.get('started') is False and r.get('nativeErrorCode')==1260 for r in during)
    before,after=read('before-crash-state.json'),read('after-crash-state.json')
    def status(value):
        return (value.get('runId')==run and value.get('mode')=='VM_FIXTURE_INTEGRATION_NOT_PRODUCTION' and value.get('telemetryConnected') is False and value.get('desiredBlocked') is True and value.get('policyReadbackBlocked') is True
            and value.get('fault') is None and type(value.get('processId')) is int and value['processId']>0 and isinstance(value.get('instanceId'),str) and bool(re.fullmatch('[a-f0-9]{32}',value['instanceId']))
            and isinstance(value.get('policySha256'),str) and bool(re.fullmatch('[a-f0-9]{64}',value['policySha256'])) and isinstance(value.get('pendingTargetSha256'),str) and bool(re.fullmatch('[a-f0-9]{64}',value['pendingTargetSha256']))
            and bool(value.get('pendingId')) and bool(value.get('pendingNotBeforeUtc')) and type(value.get('storageRevision')) is int and value['storageRevision']>0)
    checks['durable_recovery_and_untrusted_clock']=bool(status(before) and status(after) and before.get('clockTrusted') is True and after.get('clockTrusted') is False and before['processId']!=after['processId'] and before['instanceId']!=after['instanceId']
        and after['storageRevision']>before['storageRevision'] and all(before.get(k)==after.get(k) for k in ('policySha256','pendingId','pendingTargetSha256','pendingNotBeforeUtc')))
    for phase,value in (('before-crash',before),('after-crash',after)):
        inspected=read('inspect-'+phase+'.json')
        checks['service_'+phase]=inspected.get('runId')==run and inspected.get('state')==4 and inspected.get('processId')==value.get('processId') and inspected.get('serviceSecurityMatches') is True and inspected.get('recoveryMatches') is True
    client=read('probe-pipe-client.json')
    checks['authorized_status_and_rejected_authority']=(client.get('runId')==run and client.get('limitedToken') is True and client.get('stopRejected') is True and client.get('clockInjectionRejected') is True and client.get('status',{}).get('processId')==before.get('processId'))
    cleanup=read('cleanup-result.json')
    checks['cleanup']=cleanup.get('runId')==run and cleanup.get('serviceAbsent') is True and cleanup.get('ownPoliciesAbsent') is True and runner.get('cleanupTaskRemoved') is True
    checks['install_and_crash']=read('install-result.json').get('runId')==run and read('install-result.json').get('installed') is True and read('crash-request.json').get('runId')==run and read('crash-request.json').get('crashRequested') is True
    attempts=probes['blocked'].get('transitionAttempts',[]);frequency=before.get('clockFrequency');boundary=before.get('cycleStartTick');upper=None
    first=next((r for r in attempts if r.get('started') is False and r.get('nativeErrorCode')==1260),None)
    if first and type(frequency) is int and frequency>0 and type(boundary) is int and type(first.get('endTick')) is int and probes['blocked'].get('clockFrequency')==frequency:
        upper=(first['endTick']-boundary)/frequency
    checks['scheduled_transition_not_before_boundary']=upper is not None and upper>=0
    same={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    if same!=original: raise ValueError('Source evidence changed during verification')
    return {'kind':'SERVICE_POLICY_INTEGRATION_EVIDENCE_AUDIT','run_id':run,'checks':checks,'functional_batch_passed':all(checks.values()),
        'observed_block_upper_bound_seconds':upper,'observed_under_two_seconds':upper is not None and 0<=upper<=2,
        'formal_launch_observations':sum(len(p.get('trials',[])) for p in probes.values()),'full_phase0_pass_awarded':False,
        'actual_reboot_proven':False,'real_usage_persistence_proven':False,'snapshot_restore_independently_verified':False}


def main():
    if len(sys.argv) not in (2,3): raise SystemExit('Usage: verify_service_policy_lab.py EVIDENCE [FROZEN_ZIP]')
    package=Path(sys.argv[2]) if len(sys.argv)==3 else Path(__file__).resolve().parents[1]/'dist/AICT-service-policy-lab-v1.zip'
    result=verify(Path(sys.argv[1]),package)
    print(json.dumps(result,ensure_ascii=False,indent=2));raise SystemExit(0 if result['functional_batch_passed'] else 2)


if __name__=='__main__': main()
