"""Validate frozen VM evidence without editing it or awarding a full phase-0 gate."""
from __future__ import annotations

import hashlib
import json
import math
import re
import sys
from pathlib import Path

FIXTURES = ("Targets\\Target.exe", "Targets\\Renamed.exe", "Targets\\AllowedControl.exe", "MovedTarget.exe")
REQUIRED = ("runner-result.json", "worker-result.json", "probe-baseline.json", "probe-blocked.json", "probe-recovered.json", "probe-probe-result.json")


def allowed(row: dict) -> bool:
    return (row.get("Started") is True and row.get("Completed") is True
            and type(row.get("ExitCode")) is int and row["ExitCode"] == 0
            and row.get("MarkerMatched") is True and row.get("Error") is None
            and row.get("NativeErrorCode") is None)


def blocked(row: dict) -> bool:
    return (row.get("Started") is False and type(row.get("NativeErrorCode")) is int
            and row["NativeErrorCode"] == 1260)


def verify(directory: Path) -> dict:
    directory = directory.resolve()
    original = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    lines = (directory / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines()
    manifest: dict[str, str] = {}
    for line in lines:
        match = re.fullmatch(r"([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]*)", line)
        if not match or match[2] in manifest:
            raise ValueError("Invalid or duplicate hash entry")
        manifest[match[2]] = match[1]
    if set(manifest) != set(original) - {"SHA256SUMS.txt"}:
        raise ValueError("Evidence set differs from manifest")
    if any(original[name] != digest for name, digest in manifest.items()):
        raise ValueError("Evidence hash mismatch")
    if not set(REQUIRED) <= set(manifest):
        raise ValueError("Incomplete execution evidence; preserve failure logs")
    reports = {name: json.loads((directory / name).read_text(encoding="utf-8-sig")) for name in REQUIRED}
    runner, worker, baseline, during, recovery, probe = [reports[name] for name in REQUIRED]
    run_id = runner.get("RunId")
    if not isinstance(run_id, str) or not re.fullmatch(r"[a-f0-9]{32}", run_id) or any(r.get("RunId") != run_id for r in reports.values()):
        raise ValueError("Report run identity mismatch")
    if any(r.get("SchemaVersion", 1) != 1 for r in reports.values()):
        raise ValueError("Unsupported evidence schema")
    if runner.get("Kind") != "APPLOCKER_ENFORCE_GUEST_RUNNER" or worker.get("Kind") != "APPLOCKER_ENFORCE_DIRECTORY_SMOKE" or probe.get("Kind") != "APPLOCKER_LIMITED_TOKEN_PROBE":
        raise ValueError("Unexpected report kind")
    if not runner.get("SnapshotReferenceOperatorSupplied") or not runner.get("EditionId") or not runner.get("Build") or runner.get("VmwareGuestVerified") is not True:
        raise ValueError("Guest metadata missing")
    if any(not re.fullmatch(r"[a-f0-9]{64}", str(runner.get(k, ""))) for k in ("FixtureSha256", "WorkerSha256")):
        raise ValueError("Missing source binary identities")
    checks = {
        "runner_pass": runner.get("SmokePassed") is True and runner.get("Error") is None and type(runner.get("NativeExitCode")) is int and runner["NativeExitCode"] == 0,
        "tasks_removed": all(runner.get(k) is True for k in ("CleanupWatchdogRegistered", "CleanupWatchdogRemoved", "WorkerTaskRemoved", "ProbeTaskRemoved", "CleanupConfirmed")),
        "management": all(worker.get(k) is True for k in ("LocalSystem", "MutationAttempted", "CompanionAppxAllowVerified", "CreateReturned", "ReadbackVerified", "OwnInstancesAbsent", "ProbeContractPassed", "RecoveredPhasePassed", "SmokePassed")),
        "worker_errors_absent": all(worker.get(k) is None for k in ("Error", "CleanupError", "RecoveryError")),
        "baseline": all(baseline.get(k) is True for k in ("LimitedToken", "BaselinePassed", "HolderReady")),
        "blocked": all(during.get(k) is True for k in ("LimitedToken", "BlockedPhasePassed", "ExistingProcessStillRunning", "CopyOutsideDirectoryExpectedAllowed")),
        "recovery": recovery.get("LimitedToken") is True and recovery.get("RecoveryPassed") is True,
        "probe": probe.get("LimitedToken") is True and probe.get("InteractiveSession") is True and probe.get("ProbeCompleted") is True and probe.get("Error") is None and probe.get("HolderCleanupError") is None,
        "honest_scope": (runner.get("IdentityBypassResistanceProven") is False and during.get("IdentityBypassResistanceProven") is False
                         and all(worker.get(k) is False for k in ("LatencyGateAwarded", "IdentityGateAwarded", "Phase0PassAwarded"))
                         and runner.get("Phase0PassAwarded") is False and during.get("LatencyGateAwarded") is False
                         and worker.get("PolicyScope") == runner.get("PolicyScope") == "FIXTURE_DIRECTORY_PATH_ONLY"),
    }
    verified_trials = 0
    expected_keys = {(path, repeat) for path in FIXTURES for repeat in (1, 2, 3)}
    for phase_name, report in (("baseline", baseline), ("blocked", during), ("recovered", recovery)):
        rows = report.get("Trials")
        if not isinstance(rows, list) or len(rows) != 12 or any(type(r.get("Repetition")) is not int for r in rows) or {(r.get("RelativePath"), r.get("Repetition")) for r in rows} != expected_keys:
            raise ValueError("Missing/duplicate trial in " + phase_name)
        for row in rows:
            deny = phase_name == "blocked" and row["RelativePath"] in FIXTURES[:2]
            if row.get("Expected") != ("POLICY_DENIED_1260" if deny else "ALLOWED_WITH_MARKER") or row.get("ExpectationMet") is not True or not (blocked(row) if deny else allowed(row)):
                checks[phase_name + "_trials"] = False
            else:
                verified_trials += 1
    attempts = during.get("PropagationAttempts", [])
    recovery_attempts = recovery.get("PropagationAttempts", [])
    checks["propagation_evidence"] = (isinstance(attempts, list) and 1 <= len(attempts) <= 200
        and blocked(attempts[-1]) and all(allowed(row) for row in attempts[:-1])
        and isinstance(recovery_attempts, list) and 1 <= len(recovery_attempts) <= 200
        and allowed(recovery_attempts[-1]) and all(blocked(row) for row in recovery_attempts[:-1]))
    latency = during.get("FirstObservedBlockSeconds")
    checks["latency_observation_present"] = type(latency) in (int, float) and math.isfinite(latency) and latency >= 0
    if checks["latency_observation_present"]:
        tick = attempts[-1].get("CompletedTick") if attempts else None
        start, frequency = worker.get("ApplyStartTick"), worker.get("ClockFrequency")
        checks["latency_clock_matches"] = (type(tick) is int and type(start) is int and type(frequency) is int and frequency > 0
            and math.isclose((tick - start) / frequency, latency, rel_tol=1e-9, abs_tol=1e-9)
            and worker.get("FirstObservedBlockSeconds") == latency)
    current = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    if current != original:
        raise ValueError("Source evidence changed during verification")
    return {"kind": "APPLOCKER_ENFORCE_DIRECTORY_EVIDENCE_RECEIPT", "run_id": run_id,
            "smoke_pass": all(checks.values()) and verified_trials == 36, "checks": checks,
            "verified_trials": verified_trials, "first_observed_block_seconds": latency,
            "latency_gate_awarded": False, "identity_gate_awarded": False, "phase0_gate_awarded": False,
            "snapshot_restore_verified": runner.get("SnapshotRestoreVerified") is True,
            "scope": "VM directory path enforcement only; outside copy is intentionally allowed; limited token of existing account, not separate standard account",
            "artifacts": manifest}


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python scripts/verify_enforce_smoke.py reports/applocker-enforce-TIMESTAMP")
        return 1
    try:
        receipt = verify(Path(sys.argv[1]))
        print(json.dumps(receipt, ensure_ascii=False, indent=2))
        return 0 if receipt["smoke_pass"] else 2
    except (OSError, ValueError, KeyError, TypeError, AttributeError, IndexError) as error:
        print("EVIDENCE_INCOMPLETE_OR_INVALID: " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
