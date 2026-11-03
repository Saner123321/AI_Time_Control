"""Freeze a clean, self-contained DEVELOPMENT preview after the local checks pass.

Copies only App/Backend/docs/exit script, never a test or user database. No system APIs.
Existing releases are not overwritten, including an interrupted partial release.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile
import xml.etree.ElementTree as ET


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--trx", type=Path, required=True)
    parser.add_argument("--name", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    bundle, trx = args.bundle.resolve(), args.trx.resolve()
    if not re.fullmatch(r"AICT-desktop-preview-v[0-9]+", args.name):
        raise ValueError("A new versioned development-preview name is required")
    if not bundle.is_relative_to(root / "artifacts") or not trx.is_relative_to(root / "artifacts"):
        raise ValueError("Use this project's isolated build/test artifacts")
    pipe_file, ui_file = bundle / "pipe-smoke/result.json", bundle / "ui-self-test/result.json"
    pipe, ui = json.loads(pipe_file.read_text(encoding="utf-8")), json.loads(ui_file.read_text(encoding="utf-8"))
    if pipe.get("passed") is not True or ui.get("passed") is not True or ui.get("systemEnforcementActive") is not False:
        raise ValueError("The real pipe and instrumented UI smoke checks must pass")
    counters = ET.parse(trx).find(".//{*}Counters")
    if counters is None or int(counters.attrib["failed"]) or int(counters.attrib["passed"]) != int(counters.attrib["total"]):
        raise ValueError("The specified .NET regression result must be fully passing")
    folder, archive = root / "dist" / args.name, root / "dist" / (args.name + ".zip")
    if folder.exists() or archive.exists() or archive.with_suffix(".zip.sha256").exists():
        raise FileExistsError("Frozen or partial release already exists; use a new version")
    folder.mkdir(parents=True)
    for component in ("App", "Backend"):
        shutil.copytree(bundle / component, folder / component)
    for filename in ("Stop-Preview.ps1", "README.md"):
        shutil.copy2(bundle / filename, folder / filename)
    # A portable launcher retains its bundle layout. It neither elevates nor registers startup.
    (folder / "打开开发预览.cmd").write_bytes(b'@echo off\r\nstart "" "%~dp0App\\Aict.Desktop.exe"\r\n')
    files = {p.relative_to(folder).as_posix(): sha(p) for p in sorted(folder.rglob("*")) if p.is_file()}
    if any("data" in p.lower().split("/")[:-1] or p.endswith(".sqlite") for p in files):
        raise ValueError("Development databases must not be shipped")
    manifest = {
        "mode": "DEVELOPMENT_PREVIEW_NO_SYSTEM_ENFORCEMENT", "system_enforcement_active": False,
        "self_contained": True, "runtime": "win-x64", "dotnet_tests_passed": int(counters.attrib["passed"]),
        "pipe_observations_passed": len(pipe["observations"]), "ui_self_test": ui,
        "evidence_sha256": {str(p.relative_to(root)): sha(p) for p in (trx, pipe_file, ui_file)},
        "files_sha256": files,
    }
    (folder / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for file in sorted(folder.rglob("*")):
            if file.is_file():
                zipped.write(file, args.name + "/" + file.relative_to(folder).as_posix())
    archive.with_suffix(".zip.sha256").write_text(sha(archive) + "  " + archive.name + "\n", encoding="ascii")
    print(json.dumps({"folder": str(folder), "archive": str(archive), "files": len(files), "bytes": archive.stat().st_size}, ensure_ascii=False))


if __name__ == "__main__":
    main()
