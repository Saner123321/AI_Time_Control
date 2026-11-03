"""Synthetic report parser tests: these never install a service or prove Windows enforcement."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from verify_service_policy_lab import verify,PATHS,PHASES,OPS


def records():
    run='c'*32
    before=dict(runId=run,mode='VM_FIXTURE_INTEGRATION_NOT_PRODUCTION',telemetryConnected=False,desiredBlocked=True,policyReadbackBlocked=True,fault=None,processId=111,instanceId='a'*32,policySha256='b'*64,
        pendingId='11111111-1111-1111-1111-111111111111',pendingTargetSha256='d'*64,pendingNotBeforeUtc='2030-01-02T01:00:00Z',clockTrusted=True,storageRevision=10,cycleStartTick=100000,clockFrequency=1000)
    after=before|dict(processId=222,instanceId='e'*32,clockTrusted=False,storageRevision=11)
    result={'runner-result.json':dict(kind='AICT_SERVICE_POLICY_LAB_RUNNER',runId=run,vmwareGuestVerified=True,editionId='CoreCountrySpecific',build='SYNTHETIC_NOT_WINDOWS',fixedFixtureOnly=True,snapshotRestoreOperatorConfirmed=True,snapshotReference='SYNTHETIC',snapshotRestoreIndependentlyVerified=False,phase0PassAwarded=False,error=None,cleanupError=None,exportRefusedFiles=[],cleanupTaskRemoved=True),
        'before-crash-state.json':before,'after-crash-state.json':after,'cleanup-result.json':dict(runId=run,serviceAbsent=True,ownPoliciesAbsent=True),
        'install-result.json':dict(runId=run,installed=True),'crash-request.json':dict(runId=run,crashRequested=True),
        'probe-pipe-client.json':dict(runId=run,limitedToken=True,stopRejected=True,clockInjectionRejected=True,status=before)}
    for phase,state in (('before-crash',before),('after-crash',after)):
        result['inspect-'+phase+'.json']=dict(runId=run,state=4,processId=state['processId'],serviceSecurityMatches=True,recoveryMatches=True)
    for phase in PHASES:
        rows=[]
        for rep in (1,2,3):
            for path in PATHS:
                block=phase not in ('baseline','restored') and path!=PATHS[-1]
                rows.append(dict(relativePath=path,repetition=rep,started=not block,markerMatched=not block,nativeErrorCode=1260 if block else 0,expectedBlocked=block,expectationMet=True,startTick=100200,endTick=100250,serviceState=1 if phase=='during-crash' else 4))
        probe=dict(runId=run,phase=phase,limitedToken=True,passed=True,trials=rows,clockFrequency=1000,transitionAttempts=[dict(started=False,nativeErrorCode=1260,startTick=100100,endTick=100150)] if phase=='blocked' else [])
        if phase in ('blocked','after-crash'):
            probe['permissions']=dict(passed=True,noStopKillDeleteOrFileBytesWritten=True,checks=[dict(operation=op,denied=True,nativeErrorCode=5) for op in sorted(OPS)])
        result['probe-'+phase+'.json']=probe
    return result


class IntegrationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name);self.evidence=self.root/'evidence';self.evidence.mkdir();self.package=self.root/'synthetic.zip';self.data=records()
        binary=b'SYNTHETIC PARSER TEST ONLY';release=json.dumps(dict(kind='AICT_SERVICE_POLICY_LAB_V1',files={'Service/Aict.Service.exe':hashlib.sha256(binary).hexdigest()})).encode()
        with zipfile.ZipFile(self.package,'w') as z:z.writestr('MANIFEST.json',release);z.writestr('Service/Aict.Service.exe',binary)
        self.data['runner-result.json']['sourceManifestSha256']=hashlib.sha256(release).hexdigest();self.write()
    def write(self):
        for name,value in self.data.items():(self.evidence/name).write_text(json.dumps(value),encoding='utf-8')
        text=''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in sorted(self.evidence.iterdir()) if p.name!='SHA256SUMS.txt')
        (self.evidence/'SHA256SUMS.txt').write_text(text,encoding='utf-8')
    def test_complete_synthetic_contract_never_awards_phase0_reboot_or_usage(self):
        result=verify(self.evidence,self.package);self.assertTrue(result['functional_batch_passed']);self.assertEqual(60,result['formal_launch_observations']);self.assertTrue(result['observed_under_two_seconds'])
        for key in ('full_phase0_pass_awarded','actual_reboot_proven','real_usage_persistence_proven','snapshot_restore_independently_verified'):self.assertFalse(result[key])
    def test_missing_source_match_never_passes(self):self.assertFalse(verify(self.evidence)['functional_batch_passed'])
    def test_access_denied_five_is_not_app_policy_denial(self):
        self.data['probe-blocked.json']['trials'][0]['nativeErrorCode']=5;self.write();self.assertFalse(verify(self.evidence,self.package)['functional_batch_passed'])
    def test_service_running_through_probe_does_not_prove_outage_coverage(self):
        for row in self.data['probe-during-crash.json']['trials']:row['serviceState']=4
        self.write();self.assertFalse(verify(self.evidence,self.package)['checks']['denial_during_observed_service_outage'])
    def test_pending_payload_or_time_changed_is_not_durable_recovery(self):
        self.data['after-crash-state.json']['pendingTargetSha256']='0'*64;self.write();self.assertFalse(verify(self.evidence,self.package)['functional_batch_passed'])
    def test_duplicate_trial_cannot_replace_renamed_copy(self):
        rows=self.data['probe-blocked.json']['trials'];rows[1]=copy.deepcopy(rows[0]);self.write();self.assertFalse(verify(self.evidence,self.package)['functional_batch_passed'])
    def test_cleanup_failure_prevents_batch_pass(self):
        self.data['cleanup-result.json']['ownPoliciesAbsent']=False;self.write();self.assertFalse(verify(self.evidence,self.package)['functional_batch_passed'])
    def test_slow_observation_keeps_functional_result_but_not_two_second_target(self):
        self.data['probe-blocked.json']['transitionAttempts'][0]['endTick']=104000;self.write();result=verify(self.evidence,self.package);self.assertTrue(result['functional_batch_passed']);self.assertFalse(result['observed_under_two_seconds'])
    def test_source_hash_tampering_is_refused(self):
        (self.evidence/'probe-blocked.json').write_text('{}');self.assertRaises(ValueError,verify,self.evidence,self.package)


if __name__=='__main__':unittest.main()
