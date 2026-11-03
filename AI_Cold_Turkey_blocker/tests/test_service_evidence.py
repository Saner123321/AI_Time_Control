"""Synthetic evidence tests do not install a service or prove real guest behavior."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from verify_service_lab import verify,SERVICE_OPS,FILE_OPS


def records():
    run='c'*32
    def snapshot(index,boot,pid):
        return dict(Kind='AICT_SERVICE_LAB_INSPECTION',RunId=run,State=4,ProcessId=pid,AutoStart=True,ImageAndAccountMatch=True,SecurityMatches=True,Recovery={'MatchesFixedRecovery':True},
                    Heartbeat=dict(Kind='AICT_SERVICE_LAB_HEARTBEAT',RunId=run,LocalSystem=True,ServicePhase='Running',ProcessId=pid,InstanceId=str(index)*32,BootTimeUtc=boot,PersistentStartCount=index,PersistentHeartbeatCount=index*2))
    before=snapshot(1,'2030-01-01T01:00:00Z',111)
    after=snapshot(2,'2030-01-01T01:00:00Z',222)
    boot=snapshot(3,'2030-01-01T01:05:00Z',111) # PID reuse after reboot is normal.
    prepare=dict(Kind='AICT_SERVICE_LAB_RUNNER',RunId=run,Stage='Prepare',PreparePassed=True,PermissionsPassed=True,CrashRecoveryPassed=True,CleanupTaskRegistered=True,ServiceLeftForReboot=True,NoBlockingPolicyApplied=True,Error=None,RecoveryObservedSeconds=1.25)
    runner=dict(Kind='AICT_SERVICE_LAB_RUNNER',RunId=run,Stage='Finish',VmwareGuestVerified=True,EditionId='CoreCountrySpecific',Build='SYNTHETIC_NOT_REAL_VM',SnapshotReferenceOperatorSupplied='SYNTHETIC',SnapshotRestoreVerified=False,NoBlockingPolicyApplied=True,
                Error=None,PreparePassed=True,CrashRecoveryPassed=True,PermissionsPassed=True,BootObserved=True,ServiceAutostartObserved=True,TestStatePersisted=True,FinishPassed=True,ServiceAbsent=True,CleanupTaskRemoved=True,BlockingAcrossBootProven=False)
    result={'runner-result.json':runner,'prepare-summary.json':prepare,'inspect-before-crash.json':before,'inspect-after-crash.json':after,'inspect-after-boot-20300101T010600000Z.json':boot,
            'install-result.json':dict(Kind='AICT_SERVICE_LAB_INSTALL',RunId=run,Installed=True,SecurityVerified=True,RecoveryVerified=True,NoBlockingPolicyApplied=True),
            'crash-request.json':dict(Kind='AICT_SERVICE_LAB_CRASH_REQUEST',RunId=run,OneTimeCrashRequested=True),
            'cleanup-finish-20300101T010601000Z.json':dict(Kind='AICT_SERVICE_LAB_CLEANUP',RunId=run,ServiceAbsent=True,NoBlockingPolicyApplied=True)}
    for phase,view in (('before-crash',before),('after-crash',after),('after-boot',boot)):
        checks=[dict(Operation=op,Denied=True,NativeErrorCode=5) for op in sorted(SERVICE_OPS)]
        checks += [dict(Operation=op,Denied=True,OwnerSystemOrAdministrators=True,AclSafe=True,UnexpectedFailure=None) for op in sorted(FILE_OPS)]
        result['probe-probe-'+phase+'.json']=dict(Kind='AICT_SERVICE_LAB_LIMITED_PROBE',RunId=run,Phase=phase,LimitedToken=True,InteractiveSession=True,ProtectedLayoutAclSafe=True,PermissionsPassed=True,NoStopDeleteKillOrFileBytesWritten=True,ProcessIdObserved=view['ProcessId'],Checks=checks)
    return result


class ServiceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name);self.data=records()
    def write(self):
        lines=[]
        for name,value in self.data.items():
            raw=json.dumps(value).encode();(self.root/name).write_bytes(raw);lines.append(hashlib.sha256(raw).hexdigest()+'  '+name)
        (self.root/'SHA256SUMS.txt').write_text('\n'.join(lines),encoding='utf-8')
    def test_complete_service_contract_not_blocking_or_quota_proof(self):
        self.write();before={p.name:p.read_bytes() for p in self.root.iterdir()};value=verify(self.root)
        self.assertTrue(value['complete_lab_pass']);self.assertTrue(value['service_crash_recovery_pass'])
        self.assertFalse(value['blocking_across_boot_proven']);self.assertFalse(value['quota_persistence_proven']);self.assertFalse(value['phase0_gate_awarded'])
        self.assertEqual(before,{p.name:p.read_bytes() for p in self.root.iterdir()})
    def test_same_boot_not_reboot(self):
        self.data['inspect-after-boot-20300101T010600000Z.json']['Heartbeat']['BootTimeUtc']='2030-01-01T01:00:00Z';self.write();self.assertFalse(verify(self.root)['complete_lab_pass'])
    def test_missing_boot_times_rejected(self):
        for name in ('inspect-before-crash.json','inspect-after-crash.json'):self.data[name]['Heartbeat']['BootTimeUtc']=None
        self.write();self.assertFalse(verify(self.root)['complete_lab_pass'])
    def test_error_other_than_access_denied_is_not_permission_proof(self):
        self.data['probe-probe-before-crash.json']['Checks'][0]['NativeErrorCode']=1060
        self.write();self.assertFalse(verify(self.root)['service_permissions_pass'])
    def test_user_can_control_service_does_not_pass(self):
        self.data['probe-probe-after-crash.json']['Checks'][0]['Denied']=False
        self.write();self.assertFalse(verify(self.root)['complete_lab_pass'])
    def test_counter_reset_does_not_pass(self):
        self.data['inspect-after-boot-20300101T010600000Z.json']['Heartbeat']['PersistentStartCount']=1
        self.write();self.assertFalse(verify(self.root)['complete_lab_pass'])
    def test_cleanup_failure_preserves_prior_service_observations(self):
        self.data['runner-result.json'].update(ServiceAbsent=False,CleanupError='SYNTHETIC',FinishPassed=False)
        self.write();value=verify(self.root);self.assertFalse(value['complete_lab_pass']);self.assertTrue(value['service_crash_recovery_pass'])
    def test_modified_source_is_rejected(self):
        self.write()
        with (self.root/'inspect-before-crash.json').open('a') as out:out.write(' ')
        with self.assertRaisesRegex(ValueError,'hash'):verify(self.root)


if __name__=='__main__':unittest.main()
