"""Check recorded lab evidence, never execute policy or turn missing data into PASS."""
from __future__ import annotations

import hashlib
import json
import math
import sys
from pathlib import Path

CASES = (
    "baseline-recovery", "applocker-deploy", "applocker-identity",
    "applocker-exceptions", "applocker-existing-policy", "applocker-latency",
    "service-permissions", "service-restart", "boot-persistence",
    "browser-policy", "browser-profiles", "browser-extension-loss",
    "wfp-dual-stack", "wfp-existing-flow", "cleanup-recovery",
)
EDITIONS = {"home": {"Core", "CoreCountrySpecific", "CoreSingleLanguage"},
            "pro": {"Professional", "ProfessionalN"}}
LIMITS = {"applocker-latency": 2.0, "browser-extension-loss": 5.0}
STATES = {"NOT_RUN", "BLOCKED", "PASS", "FAIL"}


def check(path: Path) -> tuple[int, list[str]]:
    """0 complete, 1 invalid/failed, 2 incomplete. Evidence is operator-recorded."""
    problems: list[str] = []
    pending: list[str] = []
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if data["schema_version"] != 1 or not isinstance(data["environments"], list):
            raise ValueError("Unsupported schema")
        environments = data["environments"]
        if {e["edition_family"] for e in environments} != set(EDITIONS) or len(environments) != 2:
            raise ValueError("Exactly one home and one pro environment required")
        for env in environments:
            family = env["edition_family"]
            rows = env["cases"]
            if not isinstance(rows, list) or len(rows) != len(CASES) or {r["id"] for r in rows} != set(CASES):
                raise ValueError(f"{family}: missing, duplicate or unknown cases")
            if any(row["status"] == "PASS" for row in rows):
                if env.get("isolated_vm") is not True or not env.get("snapshot_reference"):
                    problems.append(f"{family}: PASS requires isolated VM and baseline snapshot reference")
                if env.get("edition_id") not in EDITIONS[family] or not env.get("build"):
                    problems.append(f"{family}: PASS requires matching measured edition and build")
            for row in rows:
                label = f"{family}/{row['id']}"
                status = row["status"]
                if status not in STATES:
                    raise ValueError(f"{label}: unknown status")
                if status == "FAIL":
                    problems.append(f"{label}: FAIL")
                elif status in {"NOT_RUN", "BLOCKED"}:
                    pending.append(f"{label}: {status}")
                else:
                    if not row.get("observed_at_utc") or not row.get("observation"):
                        problems.append(f"{label}: missing timestamp or observed result")
                    artifacts = row.get("artifacts")
                    if not isinstance(artifacts, list) or not artifacts:
                        problems.append(f"{label}: missing evidence artifacts")
                        continue
                    for artifact in artifacts:
                        relative = Path(artifact["path"])
                        resolved = (path.parent / relative).resolve()
                        if relative.is_absolute() or not resolved.is_relative_to(path.parent.resolve()):
                            problems.append(f"{label}: artifact outside report directory")
                            continue
                        if not resolved.is_file():
                            problems.append(f"{label}: evidence file missing")
                        elif hashlib.sha256(resolved.read_bytes()).hexdigest() != artifact["sha256"]:
                            problems.append(f"{label}: evidence hash mismatch")
                    if row["id"] in LIMITS:
                        latency = row.get("max_observed_seconds")
                        if type(latency) not in (int, float) or not math.isfinite(latency) or not 0 <= latency <= LIMITS[row["id"]]:
                            problems.append(f"{label}: missing/out-of-target measured latency")
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        return 1, [f"INVALID: {error}"]
    if problems:
        return 1, problems + pending
    if pending:
        return 2, pending
    return 0, ["RECORDED_EVIDENCE_COMPLETE: manual review of lab authenticity still required."]


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python scripts/check_phase0.py reports/phase0-results.json")
        return 1
    code, messages = check(Path(sys.argv[1]).resolve())
    print("PHASE0_GATE=" + {0: "RECORDED_EVIDENCE_COMPLETE", 1: "FAILED_OR_INVALID", 2: "PENDING"}[code])
    print("\n".join(messages))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
