"""Synthetic evidence is solely a verifier test, never a Windows experiment result."""
import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from verify_enforce_smoke import FIXTURES, REQUIRED, verify


def fixture_reports():
    run_id = "a" * 32
    runner = dict(Kind="APPLOCKER_ENFORCE_GUEST_RUNNER", SnapshotReferenceOperatorSupplied="SYNTHETIC_NOT_A_VM", EditionId="CoreCountrySpecific", Build="SYNTHETIC", VmwareGuestVerified=True,
                  FixtureSha256="b" * 64, WorkerSha256="c" * 64, NativeExitCode=0, Error=None,
                  PolicyScope="FIXTURE_DIRECTORY_PATH_ONLY", Phase0PassAwarded=False, IdentityBypassResistanceProven=False)
    for key in ("SmokePassed", "CleanupWatchdogRegistered", "CleanupWatchdogRemoved", "WorkerTaskRemoved", "ProbeTaskRemoved", "CleanupConfirmed"):
        runner[key] = True
    worker = dict(Kind="APPLOCKER_ENFORCE_DIRECTORY_SMOKE", Error=None, CleanupError=None, PolicyScope="FIXTURE_DIRECTORY_PATH_ONLY",
                  LatencyGateAwarded=False, IdentityGateAwarded=False, Phase0PassAwarded=False, ApplyStartTick=100, ClockFrequency=1000, FirstObservedBlockSeconds=0.2)
    for key in ("LocalSystem", "MutationAttempted", "CompanionAppxAllowVerified", "CreateReturned", "ReadbackVerified", "OwnInstancesAbsent", "ProbeContractPassed", "RecoveredPhasePassed", "SmokePassed"):
        worker[key] = True
    baseline = dict(LimitedToken=True, BaselinePassed=True, HolderReady=True)
    during = dict(LimitedToken=True, BlockedPhasePassed=True, ExistingProcessStillRunning=True, CopyOutsideDirectoryExpectedAllowed=True,
                  IdentityBypassResistanceProven=False, LatencyGateAwarded=False, FirstObservedBlockSeconds=0.2)
    recovery = dict(LimitedToken=True, RecoveryPassed=True)
    for phase, report in (("baseline", baseline), ("blocked", during), ("recovered", recovery)):
        rows = []
        for repeat in (1, 2, 3):
            for path in FIXTURES:
                denied = phase == "blocked" and path in FIXTURES[:2]
                rows.append(dict(RelativePath=path, Repetition=repeat, Started=not denied, Completed=not denied, ExitCode=None if denied else 0,
                                 MarkerMatched=not denied, NativeErrorCode=1260 if denied else None, Error={"Type": "Win32Exception"} if denied else None,
                                 Expected="POLICY_DENIED_1260" if denied else "ALLOWED_WITH_MARKER", ExpectationMet=True, CompletedTick=300))
        report["Trials"] = rows
    during["PropagationAttempts"] = [copy.deepcopy(during["Trials"][0])]
    recovery["PropagationAttempts"] = [copy.deepcopy(recovery["Trials"][0])]
    probe = dict(Kind="APPLOCKER_LIMITED_TOKEN_PROBE", LimitedToken=True, InteractiveSession=True, ProbeCompleted=True, Error=None)
    reports = dict(zip(REQUIRED, (runner, worker, baseline, during, recovery, probe)))
    for report in reports.values():
        report.update(RunId=run_id, SchemaVersion=1)
    return reports


class EnforceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.reports = fixture_reports()

    def write(self):
        lines = []
        for name, report in self.reports.items():
            raw = json.dumps(report, ensure_ascii=False).encode("utf-8")
            (self.root / name).write_bytes(raw)
            lines.append(hashlib.sha256(raw).hexdigest() + "  " + name)
        (self.root / "SHA256SUMS.txt").write_text("\n".join(lines), encoding="utf-8")

    def test_complete_synthetic_contract_does_not_award_product_gates(self):
        self.write()
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        result = verify(self.root)
        self.assertTrue(result["smoke_pass"])
        self.assertEqual(result["verified_trials"], 36)
        self.assertFalse(result["latency_gate_awarded"])
        self.assertFalse(result["identity_gate_awarded"])
        self.assertFalse(result["phase0_gate_awarded"])
        self.assertFalse(result["snapshot_restore_verified"])
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.root.iterdir()})

    def test_generic_access_denied_does_not_prove_policy_block(self):
        self.reports["probe-blocked.json"]["Trials"][0]["NativeErrorCode"] = 5
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_exit_zero_without_fixture_marker_is_not_success(self):
        self.reports["probe-recovered.json"]["Trials"][0]["MarkerMatched"] = False
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_repeated_same_trial_is_rejected(self):
        self.reports["probe-baseline.json"]["Trials"][1] = self.reports["probe-baseline.json"]["Trials"][0]
        self.write()
        with self.assertRaisesRegex(ValueError, "duplicate trial"):
            verify(self.root)

    def test_retained_cleanup_watchdog_is_incomplete(self):
        self.reports["runner-result.json"]["CleanupWatchdogRemoved"] = False
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_missing_companion_allow_is_incomplete(self):
        self.reports["worker-result.json"]["CompanionAppxAllowVerified"] = False
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_single_sample_cannot_award_latency_gate(self):
        self.reports["worker-result.json"]["LatencyGateAwarded"] = True
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_latency_clock_mismatch_is_rejected(self):
        self.reports["worker-result.json"]["ApplyStartTick"] = 500
        self.write()
        self.assertFalse(verify(self.root)["smoke_pass"])

    def test_modified_source_fails_hash_verification(self):
        self.write()
        with (self.root / "probe-baseline.json").open("a", encoding="utf-8") as out:
            out.write(" ")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            verify(self.root)

    def test_extra_unmanifested_file_is_rejected(self):
        self.write()
        (self.root / "unexpected.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "set differs"):
            verify(self.root)

    def test_other_run_report_is_rejected(self):
        self.reports["probe-blocked.json"]["RunId"] = "d" * 32
        self.write()
        with self.assertRaisesRegex(ValueError, "run identity"):
            verify(self.root)


if __name__ == "__main__":
    unittest.main()
