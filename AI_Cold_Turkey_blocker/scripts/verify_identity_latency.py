"""Read-only audit of a 20-cycle guest run; timing failure cannot erase identity evidence."""
from __future__ import annotations
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import zipfile
from verify_enforce_smoke import FIXTURES, allowed, blocked


def verify(directory: Path, package: Path | None = None) -> dict:
    directory = directory.resolve()
    original = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    manifest = {}
    for line in (directory / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]*)", line)
        if not match or match[2] in manifest:
            raise ValueError("Bad evidence manifest")
        manifest[match[2]] = match[1]
    if set(manifest) != set(original) - {"SHA256SUMS.txt"} or any(original[k] != v for k, v in manifest.items()):
        raise ValueError("Evidence set/hash mismatch")

    def read(name):
        if name not in manifest:
            raise ValueError("Missing evidence: " + name)
        value = json.loads((directory / name).read_text(encoding="utf-8-sig"))
        if not isinstance(value, dict):
            raise ValueError("Expected JSON object: " + name)
        return value

    runner, worker, baseline, probe = [read(name) for name in ("runner-result.json", "worker-result.json", "probe-baseline.json", "probe-probe-result.json")]
    run_id = runner.get("RunId")
    if not isinstance(run_id, str) or not re.fullmatch("[a-f0-9]{32}", run_id):
        raise ValueError("Invalid run identity")

    def same_run(value):
        if value.get("RunId") != run_id:
            raise ValueError("Report run mismatch")

    for value in (runner, worker, baseline, probe):
        same_run(value)
    if (runner.get("Kind") != "APPLOCKER_IDENTITY_LATENCY_RUNNER" or worker.get("Kind") != "APPLOCKER_IDENTITY_LATENCY_SMOKE"
            or probe.get("Kind") != "APPLOCKER_IDENTITY_LIMITED_PROBE"):
        raise ValueError("Wrong report kind")
    checks = {
        "vm_metadata": runner.get("VmwareGuestVerified") is True and bool(runner.get("EditionId")) and bool(runner.get("Build")) and bool(runner.get("SnapshotReferenceOperatorSupplied")),
        "cleanup": all(runner.get(k) is True for k in ("CleanupConfirmed", "CleanupWatchdogRegistered", "CleanupWatchdogRemoved", "WorkerTaskRemoved", "ProbeTaskRemoved")) and worker.get("OwnInstancesAbsent") is True,
        "worker": all(worker.get(k) is True for k in ("LocalSystem", "MutationAttempted", "CompanionAppxAllowVerified", "FunctionalPassed", "ProbeCompleted")) and all(worker.get(k) is None for k in ("Error", "CleanupError", "ProbeCompletionError")),
        "limited_token": baseline.get("LimitedToken") is True and baseline.get("BaselinePassed") is True and probe.get("LimitedToken") is True and probe.get("InteractiveSession") is True and probe.get("ProbeCompleted") is True and probe.get("Error") is None,
        "runner": runner.get("Error") is None and runner.get("FunctionalPassed") is True and type(runner.get("NativeExitCode")) is int and runner["NativeExitCode"] in (0, 2),
        "scope": runner.get("PolicyScope") == worker.get("PolicyScope") == "FIXTURE_AUTHENTICODE_SHA256_ONLY" and runner.get("Phase0PassAwarded") is False and worker.get("Phase0PassAwarded") is False,
    }
    sip = runner.get("TargetSipHash")
    checks["sip_identity"] = isinstance(sip, str) and bool(re.fullmatch(r"0x[A-F0-9]{64}", sip)) and worker.get("TargetSipHash") == sip
    checks["package_identities_present"] = all(isinstance(runner.get(k), str) and re.fullmatch(r"[a-f0-9]{64}", runner[k]) for k in ("FixtureSha256", "ControlSha256", "WorkerSha256")) and runner.get("FixtureSha256") != runner.get("ControlSha256")
    binary_match = False
    if package is not None:
        with zipfile.ZipFile(package) as bundle:
            release = json.loads(bundle.read("MANIFEST.json"))
            binary_match = (release.get("kind") == "AICT_APPLOCKER_IDENTITY_LATENCY_V1" and release.get("target_sip_hash") == sip
                            and all(runner[field] == release["files"][path] == hashlib.sha256(bundle.read(path)).hexdigest() for field, path in (
                                ("WorkerSha256", "lab/AictAppLockerIdentityLatency.exe"), ("FixtureSha256", "lab/AictIdentityTarget.exe"), ("ControlSha256", "lab/AictIdentityControl.exe"))))
        checks["binary_matches_frozen_release"] = binary_match
    verified_trials = 0

    def trials(report, is_blocked, repetitions):
        nonlocal verified_trials
        rows = report.get("Trials")
        expected_keys = {(path, repetition) for path in FIXTURES for repetition in range(1, repetitions + 1)}
        if not isinstance(rows, list) or len(rows) != len(expected_keys) or any(type(r.get("Repetition")) is not int for r in rows) or {(r.get("RelativePath"), r.get("Repetition")) for r in rows} != expected_keys:
            raise ValueError("Missing or duplicate formal trial")
        passed = True
        for row in rows:
            deny = is_blocked and row["RelativePath"] != "Targets\\AllowedControl.exe"
            valid = row.get("ExpectationMet") is True and row.get("Expected") == ("POLICY_DENIED_1260" if deny else "ALLOWED_WITH_MARKER") and (blocked(row) if deny else allowed(row))
            verified_trials += int(valid)
            passed &= valid
        return passed

    checks["baseline_trials"] = trials(baseline, False, 3)
    cycles = worker.get("Cycles")
    if (not isinstance(cycles, list) or len(cycles) != 20 or any(type(c.get("Cycle")) is not int for c in cycles)
            or [c["Cycle"] for c in cycles] != list(range(1, 21)) or worker.get("RequestedCycles") != 20 or worker.get("CompletedCycles") != 20
            or probe.get("CompletedCycles") != 20 or runner.get("CompletedCycles") != 20):
        raise ValueError("Twenty complete, ordered cycles are required")
    block_times, allow_times = [], []
    for summary in cycles:
        cycle = summary["Cycle"]
        label = f"cycle-{cycle:02d}"
        checks[label + "_summary"] = all(summary.get(k) is True for k in ("CreateReadbackVerified", "ExeAbsent", "FunctionalPassed", "BlockPhasePassed", "RecoveryPhasePassed"))
        for phase, deny, target, tick_key, metric, observed in (
                ("blocked", True, "applied", "ApplyStartTick", "FirstObservedBlockSeconds", block_times),
                ("recovered", False, "removed", "RemoveStartTick", "FirstObservedAllowSeconds", allow_times)):
            phase_report = read(f"probe-{label}-{phase}.json")
            marker = read(f"{label}-{target}.json")
            same_run(phase_report); same_run(marker)
            if phase_report.get("Cycle") != cycle or marker.get("Cycle") != cycle:
                raise ValueError("Cycle handshake mismatch")
            phase_good = phase_report.get("LimitedToken") is True and phase_report.get("BlockedPhasePassed" if deny else "RecoveryPassed") is True
            phase_good &= trials(phase_report, deny, 1)
            attempts = phase_report.get("PropagationAttempts")
            if not isinstance(attempts, list) or not 1 <= len(attempts) <= 250:
                raise ValueError("Missing propagation attempts")
            phase_good &= (blocked(attempts[-1]) if deny else allowed(attempts[-1])) and all((allowed(row) if deny else blocked(row)) for row in attempts[:-1])
            if any(row.get("RelativePath") != "Targets\\Target.exe" for row in attempts):
                raise ValueError("Propagation sample mismatch")
            ticks, start, freq = attempts[-1].get("CompletedTick"), summary.get(tick_key), summary.get("ClockFrequency")
            value = phase_report.get(metric)
            if (type(value) not in (int, float) or not math.isfinite(value) or value < 0 or type(ticks) is not int or type(start) is not int or type(freq) is not int or freq <= 0
                    or marker.get(tick_key) != start or marker.get("ClockFrequency") != freq or summary.get(metric) != value
                    or not math.isclose((ticks - start) / freq, value, abs_tol=1e-9, rel_tol=1e-9)):
                raise ValueError("Invalid or inconsistent monotonic latency")
            observed.append(value)
            checks[label + "_" + phase] = phase_good
    max_block, max_allow = max(block_times), max(allow_times)
    functional = all(checks.values()) and verified_trials == 172
    timing = functional and max_block <= 2 and max_allow <= 2
    if (worker.get("MaxObservedBlockSeconds") != max_block or worker.get("MaxObservedAllowSeconds") != max_allow
            or worker.get("TimingTargetMet") is not timing or runner.get("TimingTargetMet") is not timing
            or worker.get("BatchPassed") is not (functional and timing) or runner.get("BatchPassed") is not (functional and timing)):
        checks["aggregate_flags_consistent"] = False
        functional = timing = False
    current = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir() if p.is_file()}
    if current != original:
        raise ValueError("Source evidence changed during audit")
    return {"kind": "APPLOCKER_IDENTITY_LATENCY_RECEIPT", "run_id": run_id, "identity_functional_pass": functional, "latency_target_pass": timing,
            "verified_formal_trials": verified_trials, "completed_cycles": 20, "max_observed_block_seconds": max_block, "max_observed_allow_seconds": max_allow,
            "binary_identities_match_frozen_package": binary_match, "checks": checks, "phase0_gate_awarded": False, "artifacts": manifest}


def main():
    if len(sys.argv) != 2:
        print("Usage: python scripts/verify_identity_latency.py reports/identity-latency-TIMESTAMP")
        return 1
    try:
        package = Path(__file__).resolve().parents[1] / "dist/AICT-applocker-identity-latency-v1.zip"
        receipt = verify(Path(sys.argv[1]), package)
        print(json.dumps(receipt, indent=2, ensure_ascii=False))
        return 0 if receipt["identity_functional_pass"] and receipt["latency_target_pass"] else 2
    except (OSError, ValueError, KeyError, TypeError, AttributeError, zipfile.BadZipFile) as error:
        print("EVIDENCE_INVALID_OR_INCOMPLETE: " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
