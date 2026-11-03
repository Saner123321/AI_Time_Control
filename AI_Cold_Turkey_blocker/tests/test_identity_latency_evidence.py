"""Synthetic protocol tests; these records are not Windows execution evidence."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from verify_identity_latency import verify
from test_enforce_evidence import fixture_reports


def records(slow=False):
    original = fixture_reports()
    runner, worker = original["runner-result.json"], original["worker-result.json"]
    baseline, probe = original["probe-baseline.json"], original["probe-probe-result.json"]
    runner.update(Kind="APPLOCKER_IDENTITY_LATENCY_RUNNER", FunctionalPassed=True, TimingTargetMet=not slow, BatchPassed=not slow,
                  ControlSha256="d"*64, TargetSipHash="0x"+"A"*64, NativeExitCode=2 if slow else 0, CompletedCycles=20, PolicyScope="FIXTURE_AUTHENTICODE_SHA256_ONLY")
    worker.update(Kind="APPLOCKER_IDENTITY_LATENCY_SMOKE", FunctionalPassed=True, ProbeCompleted=True, RequestedCycles=20, CompletedCycles=20,
                  TargetSipHash=runner["TargetSipHash"], PolicyScope=runner["PolicyScope"], TimingTargetMet=not slow, BatchPassed=not slow,
                  MaxObservedBlockSeconds=3.0 if slow else 0.1, MaxObservedAllowSeconds=0.1, Cycles=[])
    probe.update(Kind="APPLOCKER_IDENTITY_LIMITED_PROBE", CompletedCycles=20)
    result={"runner-result.json":runner,"worker-result.json":worker,"probe-baseline.json":baseline,"probe-probe-result.json":probe}
    for cycle in range(1,21):
        latency=3.0 if slow and cycle==20 else 0.1
        apply=cycle*10000; remove=apply+5000
        summary=dict(Cycle=cycle, ClockFrequency=1000, ApplyStartTick=apply, RemoveStartTick=remove,
                     CreateReadbackVerified=True, ExeAbsent=True, FunctionalPassed=True, BlockPhasePassed=True, RecoveryPhasePassed=True,
                     FirstObservedBlockSeconds=latency, FirstObservedAllowSeconds=0.1)
        worker["Cycles"].append(summary)
        for phase, denied, tick, seconds in (("blocked",True,apply,latency),("recovered",False,remove,0.1)):
            phase_rows=copy.deepcopy(original["probe-blocked.json" if denied else "probe-recovered.json"]["Trials"][:4])
            for row in phase_rows:
                if denied and row["RelativePath"]=="MovedTarget.exe":
                    row.update(Started=False,Completed=False,ExitCode=None,MarkerMatched=False,NativeErrorCode=1260,Error={"Type":"Win32Exception"},Expected="POLICY_DENIED_1260")
                row["CompletedTick"]=tick+int(seconds*1000)
            report=dict(RunId=runner["RunId"],Cycle=cycle,LimitedToken=True,Trials=phase_rows,PropagationAttempts=[copy.deepcopy(phase_rows[0])])
            report.update({"BlockedPhasePassed" if denied else "RecoveryPassed":True,"FirstObservedBlockSeconds" if denied else "FirstObservedAllowSeconds":seconds})
            result[f"probe-cycle-{cycle:02d}-{phase}.json"]=report
            marker=dict(RunId=runner["RunId"],Cycle=cycle,ClockFrequency=1000)
            marker["ApplyStartTick" if denied else "RemoveStartTick"]=tick
            result[f"cycle-{cycle:02d}-{'applied' if denied else 'removed'}.json"]=marker
    return result


class IdentityEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.data=records()

    def write(self):
        lines=[]
        for name,value in self.data.items():
            raw=json.dumps(value).encode('utf-8');(self.root/name).write_bytes(raw)
            lines.append(hashlib.sha256(raw).hexdigest()+'  '+name)
        (self.root/'SHA256SUMS.txt').write_text('\n'.join(lines),encoding='utf-8')

    def test_complete_contract_172_trials_no_full_phase0(self):
        self.write();before={p.name:p.read_bytes() for p in self.root.iterdir()}
        result=verify(self.root)
        self.assertTrue(result['identity_functional_pass']);self.assertTrue(result['latency_target_pass'])
        self.assertEqual(result['verified_formal_trials'],172);self.assertFalse(result['phase0_gate_awarded'])
        self.assertEqual(before,{p.name:p.read_bytes() for p in self.root.iterdir()})

    def test_slow_cycle_preserves_identity_pass(self):
        self.data=records(slow=True);self.write();result=verify(self.root)
        self.assertTrue(result['identity_functional_pass']);self.assertFalse(result['latency_target_pass'])
        self.assertEqual(result['max_observed_block_seconds'],3.0)

    def test_outside_copy_allowed_does_not_prove_hash_enforcement(self):
        row=self.data['probe-cycle-01-blocked.json']['Trials'][3]
        row.update(Started=True,Completed=True,ExitCode=0,MarkerMatched=True,NativeErrorCode=None,Error=None)
        self.write();self.assertFalse(verify(self.root)['identity_functional_pass'])

    def test_control_denied_does_not_pass(self):
        self.data['probe-cycle-02-blocked.json']['Trials'][2]['Started']=False
        self.write();self.assertFalse(verify(self.root)['identity_functional_pass'])

    def test_nineteen_cycles_rejected(self):
        self.data['worker-result.json']['Cycles'].pop();self.write()
        with self.assertRaisesRegex(ValueError,'Twenty'):verify(self.root)

    def test_marker_clock_mismatch_rejected(self):
        self.data['cycle-07-applied.json']['ApplyStartTick']+=1;self.write()
        with self.assertRaisesRegex(ValueError,'latency'):verify(self.root)

    def test_hash_mismatch_rejected(self):
        self.write()
        with (self.root/'probe-cycle-01-blocked.json').open('a') as f:f.write(' ')
        with self.assertRaisesRegex(ValueError,'hash'):verify(self.root)

    def test_foreign_cycle_report_rejected(self):
        self.data['probe-cycle-11-recovered.json']['RunId']='f'*32;self.write()
        with self.assertRaisesRegex(ValueError,'run mismatch'):verify(self.root)


if __name__=='__main__':unittest.main()
