import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("evidence", Path(__file__).parents[1] / "scripts/verify_guest_evidence.py")
evidence = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(evidence)


class GuestEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = {"SchemaVersion": 1, "ReadOnlyInventory": True, "EnforcementTested": False,
                    "Edition": "CoreCountrySpecific", "Os": {"Build": "26200"},
                    "UpdateBuildRevision": 7840, "DisplayVersion": "25H2", "CollectedAtUtc": "synthetic"}
        self.pre = {"SchemaVersion": 1, "Kind": "READ_ONLY_GUEST_PREFLIGHT_NOT_ENFORCEMENT",
                    "PolicyChangesMade": False, "ServicesInstalledOrChanged": False,
                    "VirtualMachineManufacturer": "VMware, Inc.",
                    "AppLocker": {"GetPolicyAvailable": False, "SetPolicyAvailable": False},
                    "Wfp": {"EngineOpenStatus": 50}}
        self.write_bundle()

    def write_bundle(self):
        for name, value in (("environment.json", self.env), ("preflight.json", self.pre)):
            (self.root / name).write_text(json.dumps(value), encoding="utf-8")
        sums = [hashlib.sha256((self.root / name).read_bytes()).hexdigest() + "  " + name for name in sorted(evidence.REQUIRED)]
        (self.root / "SHA256SUMS.txt").write_text("\n".join(sums), encoding="utf-8")

    def test_v1_preserved_and_never_awards_wfp_pass(self):
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        result = evidence.verify(self.root)
        self.assertEqual("INVALID_V1_NULL_STRING_ABI_RETEST_REQUIRED", result["wfp_interpretation"])
        self.assertFalse(result["enforcement_pass_awarded"])
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.root.iterdir()})

    def test_v2_success_not_enforcement_pass(self):
        self.pre["SchemaVersion"] = 2
        self.pre["Wfp"]["EngineOpenStatus"] = 0
        self.write_bundle()
        self.assertFalse(evidence.verify(self.root)["enforcement_pass_awarded"])

    def test_tampering_rejected(self):
        (self.root / "preflight.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "mismatch"):
            evidence.verify(self.root)

    def test_incomplete_manifest_rejected(self):
        (self.root / "SHA256SUMS.txt").write_text("", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            evidence.verify(self.root)

    def test_traversal_rejected(self):
        (self.root / "SHA256SUMS.txt").write_text("0" * 64 + "  ../preflight.json", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            evidence.verify(self.root)

    def test_missing_readonly_attestation_rejected(self):
        self.pre.pop("PolicyChangesMade")
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "read-only"):
            evidence.verify(self.root)
