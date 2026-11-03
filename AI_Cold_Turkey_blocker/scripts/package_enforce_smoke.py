"""Freeze the checked lab binaries and sources; never overwrite an existing release."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import zipfile


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    name = "AICT-applocker-enforce-smoke-v1.zip"
    output = root / "dist" / name
    checksum = output.with_suffix(output.suffix + ".sha256")
    if output.exists() or checksum.exists():
        raise FileExistsError("Frozen release already exists; publish a new version instead")
    paths = {
        "lab/AictAppLockerEnforceSmoke.exe": "lab/build/AictAppLockerEnforceSmoke.exe",
        "lab/AictAppLockerFixture.exe": "lab/build/AictAppLockerFixture.exe",
        "lab/AppLockerEnforceSmoke.cs": "lab/AppLockerEnforceSmoke.cs",
        "lab/AppLockerFixture.cs": "lab/AppLockerFixture.cs",
        "scripts/Invoke-AppLockerEnforceSmoke.ps1": "scripts/Invoke-AppLockerEnforceSmoke.ps1",
        "scripts/Read-AppLockerLabEvent.ps1": "scripts/Read-AppLockerLabEvent.ps1",
        "docs/APPLOCKER_ENFORCE_SMOKE.md": "docs/APPLOCKER_ENFORCE_SMOKE.md",
    }
    payload = {name: (root / path).read_bytes() for name, path in paths.items()}
    payload["README.md"] = """# 家庭版应用真实封锁实验 v1

只在已有基线快照的 VMware Windows 来宾内运行；物理主机会被拒绝。
完整解压到一个新目录，在该目录打开管理员、64 位 Windows PowerShell 5.1：

```powershell
.\\scripts\\Invoke-AppLockerEnforceSmoke.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

自动检查 36 次样本启动及策略撤销。仅阻断本轮生成目录中的无害样本，
并临时配套允许所有已签名打包应用；不修改主机、服务配置、浏览器或网络。
正常结束后将完整 applocker-enforce-时间 文件夹复制回主机项目 reports。

如失败，先保留报告，至少等三分钟让独立清理任务运行，不删除受保护目录或反复重跑。
详见 docs/APPLOCKER_ENFORCE_SMOKE.md 中的范围、独立恢复和证据说明。
这是执行机制实验，不是已完成的软件；目录外复制、身份识别、重启和时延验收仍待后续。
""".encode("utf-8")
    manifest = {"schema_version": 1, "kind": "AICT_APPLOCKER_ENFORCE_SMOKE_V1",
                "files": {name: hashlib.sha256(raw).hexdigest() for name, raw in payload.items()}}
    payload["MANIFEST.json"] = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
    payload["SHA256SUMS.txt"] = "".join(hashlib.sha256(raw).hexdigest() + "  " + name + "\n" for name, raw in payload.items()).encode("ascii")
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name, raw in payload.items():
            bundle.writestr(name, raw)
    with zipfile.ZipFile(output) as bundle:
        if bundle.testzip() is not None or set(bundle.namelist()) != set(payload):
            raise ValueError("Archive integrity check failed")
        for name, raw in payload.items():
            if bundle.read(name) != raw:
                raise ValueError("Archive contents differ: " + name)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    with checksum.open("x", encoding="ascii") as out:
        out.write(digest + "  " + output.name + "\n")
    print(json.dumps({"package": str(output), "sha256": digest, "entries": len(payload), "bytes": output.stat().st_size}))


if __name__ == "__main__":
    main()
