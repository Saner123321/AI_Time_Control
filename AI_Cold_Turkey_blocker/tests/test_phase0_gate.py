"""Synthetic evidence tests only; none of these are Windows enforcement tests."""
import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("gate", Path(__file__).parents[1] / "scripts/check_phase0.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class Phase0GateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.report = self.root / "results.json"
        evidence = self.root / "synthetic-evidence.txt"
        evidence.write_text("SYNTHETIC UNIT TEST FIXTURE; NOT WINDOWS PROOF", encoding="utf-8")
        artifact = {"path": evidence.name, "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()}
        self.data = {"schema_version": 1, "environments": []}
        for family, edition in (("home", "CoreCountrySpecific"), ("pro", "Professional")):
            rows = [{"id": case, "status": "PASS", "observed_at_utc": "2026-11-02T00:00:00Z",
                     "observation": "Synthetic test", "artifacts": [copy.deepcopy(artifact)],
                     "max_observed_seconds": 1.0} for case in gate.CASES]
            self.data["environments"].append({"edition_family": family, "edition_id": edition,
                "build": "synthetic", "isolated_vm": True, "snapshot_reference": "synthetic", "cases": rows})

    def result(self):
        self.report.write_text(json.dumps(self.data), encoding="utf-8")
        return gate.check(self.report)[0]

    def test_complete_recorded_evidence(self):
        self.assertEqual(0, self.result())

    def test_not_run_is_pending(self):
        self.data["environments"][0]["cases"][0]["status"] = "NOT_RUN"
        self.assertEqual(2, self.result())

    def test_blocked_is_pending(self):
        self.data["environments"][0]["cases"][0]["status"] = "BLOCKED"
        self.assertEqual(2, self.result())

    def test_failure_wins_over_pending(self):
        self.data["environments"][0]["cases"][0]["status"] = "NOT_RUN"
        self.data["environments"][1]["cases"][0]["status"] = "FAIL"
        self.assertEqual(1, self.result())

    def test_host_cannot_satisfy_lab_gate(self):
        self.data["environments"][0]["isolated_vm"] = False
        self.assertEqual(1, self.result())

    def test_missing_snapshot(self):
        self.data["environments"][0]["snapshot_reference"] = None
        self.assertEqual(1, self.result())

    def test_wrong_edition(self):
        self.data["environments"][0]["edition_id"] = "Professional"
        self.assertEqual(1, self.result())

    def test_duplicate_case_rejected(self):
        rows = self.data["environments"][0]["cases"]
        rows[-1] = copy.deepcopy(rows[0])
        self.assertEqual(1, self.result())

    def test_missing_edition_rejected(self):
        self.data["environments"].pop()
        self.assertEqual(1, self.result())

    def test_missing_evidence(self):
        self.data["environments"][0]["cases"][0]["artifacts"] = []
        self.assertEqual(1, self.result())

    def test_modified_evidence(self):
        (self.root / "synthetic-evidence.txt").write_text("changed", encoding="utf-8")
        self.assertEqual(1, self.result())

    def test_path_escape_rejected(self):
        self.data["environments"][0]["cases"][0]["artifacts"][0]["path"] = "../outside.txt"
        self.assertEqual(1, self.result())

    def test_timing_must_be_measured_and_finite(self):
        row = next(r for r in self.data["environments"][0]["cases"] if r["id"] == "applocker-latency")
        for invalid in (None, True, -1, 2.01, float("nan"), float("inf")):
            with self.subTest(invalid=invalid):
                row["max_observed_seconds"] = invalid
                self.assertEqual(1, self.result())

    def test_malformed_json(self):
        self.report.write_text("{broken", encoding="utf-8")
        self.assertEqual(1, gate.check(self.report)[0])


if __name__ == "__main__":
    unittest.main()
