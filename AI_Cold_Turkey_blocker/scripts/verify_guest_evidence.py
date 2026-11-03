"""Verify a read-only guest evidence bundle without modifying its source files."""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

REQUIRED = {"environment.json", "preflight.json"}


def verify(directory: Path) -> dict:
    directory = directory.resolve(strict=True)
    checksums = {}
    for line in (directory / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([a-fA-F0-9]{64})  ([^\r\n]+)", line)
        if not match or match[2] not in REQUIRED or match[2] in checksums:
            raise ValueError("Invalid, duplicate, or unexpected manifest entry")
        name = match[2]
        target = directory / name
        if target.is_symlink() or target.resolve().parent != directory:
            raise ValueError("Evidence must be an ordinary file inside its source directory")
        actual = hashlib.sha256(target.read_bytes()).hexdigest()
        if actual != match[1].lower():
            raise ValueError(f"SHA256 mismatch: {name}")
        checksums[name] = actual
    if set(checksums) != REQUIRED:
        raise ValueError("Incomplete evidence manifest")
    env = json.loads((directory / "environment.json").read_text(encoding="utf-8-sig"))
    pre = json.loads((directory / "preflight.json").read_text(encoding="utf-8-sig"))
    if env.get("SchemaVersion") != 1 or pre.get("SchemaVersion") not in (1, 2):
        raise ValueError("Unknown evidence schema")
    if env.get("ReadOnlyInventory") is not True or env.get("EnforcementTested") is not False:
        raise ValueError("Not a read-only inventory")
    if pre.get("Kind") != "READ_ONLY_GUEST_PREFLIGHT_NOT_ENFORCEMENT":
        raise ValueError("Unexpected preflight kind")
    if pre.get("PolicyChangesMade") is not False or pre.get("ServicesInstalledOrChanged") is not False:
        raise ValueError("Evidence does not attest read-only collection")
    if "VMware" not in pre.get("VirtualMachineManufacturer", ""):
        raise ValueError("Guest manufacturer not confirmed")
    return {
        "kind": "VERIFIED_SOURCE_HASHES_NOT_ENFORCEMENT",
        "source_directory": directory.name,
        "source_hashes": checksums,
        "edition_id": env["Edition"],
        "build": f"{env['Os']['Build']}.{env['UpdateBuildRevision']}",
        "display_version": env["DisplayVersion"],
        "collected_at_utc": env["CollectedAtUtc"],
        "collector_schema": pre["SchemaVersion"],
        "applocker_get_available": pre["AppLocker"]["GetPolicyAvailable"],
        "applocker_set_available": pre["AppLocker"]["SetPolicyAvailable"],
        "wfp_open_status": pre["Wfp"]["EngineOpenStatus"],
        "wfp_close_status": pre["Wfp"].get("EngineCloseStatus"),
        "applocker_management_class_metadata": pre["AppLocker"].get("ManagementClassMetadata", []),
        "wfp_interpretation": (
            "INVALID_V1_NULL_STRING_ABI_RETEST_REQUIRED" if pre["SchemaVersion"] == 1
            else "READ_ONLY_SESSION_RESULT_NOT_FILTERING_PROOF"
        ),
        "enforcement_pass_awarded": False,
    }


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: python scripts/verify_guest_evidence.py reports/guest-evidence-TIMESTAMP")
        print(json.dumps(verify(Path(sys.argv[1])), indent=2, ensure_ascii=False))
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        print(f"EVIDENCE_REJECTED: {error}", file=sys.stderr)
        raise SystemExit(1)
