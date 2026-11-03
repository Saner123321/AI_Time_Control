"""Freeze a single immutable VM laboratory release, including its fixture contract."""
import hashlib
import json
from pathlib import Path
import re
import zipfile


def main():
    root=Path(__file__).resolve().parents[1]
    output=root/'dist/AICT-applocker-identity-latency-v1.zip'
    checksum=output.with_suffix('.zip.sha256')
    if output.exists() or checksum.exists():
        raise FileExistsError('Frozen release exists; use a new version')
    paths={
        'lab/AictAppLockerIdentityLatency.exe':'lab/build/identity-latency/AictAppLockerIdentityLatency.exe',
        'lab/AictIdentityTarget.exe':'lab/build/identity-latency/AictIdentityTarget.exe',
        'lab/AictIdentityControl.exe':'lab/build/identity-latency/AictIdentityControl.exe',
        'lab/IdentityFixtureContract.cs':'lab/build/identity-latency/IdentityFixtureContract.cs',
        'lab/AppLockerIdentityLatencySmoke.cs':'lab/AppLockerIdentityLatencySmoke.cs',
        'lab/AppLockerIdentityFixture.cs':'lab/AppLockerIdentityFixture.cs',
        'lab/AuthenticodeHash.cs':'lab/AuthenticodeHash.cs',
        'scripts/Invoke-AppLockerIdentityLatency.ps1':'scripts/Invoke-AppLockerIdentityLatency.ps1',
        'scripts/Read-AppLockerLabEvent.ps1':'scripts/Read-AppLockerLabEvent.ps1',
        'docs/APPLOCKER_IDENTITY_LATENCY.md':'docs/APPLOCKER_IDENTITY_LATENCY.md',
    }
    payload={name:(root/path).read_bytes() for name,path in paths.items()}
    contract=payload['lab/IdentityFixtureContract.cs'].decode('utf-8')
    sip=re.search(r'TargetSipHash="(0x[A-F0-9]{64})"',contract)[1]
    for field,file in (('TargetSha256','lab/AictIdentityTarget.exe'),('ControlSha256','lab/AictIdentityControl.exe')):
        assert re.search(field+r'="([a-f0-9]{64})"',contract)[1]==hashlib.sha256(payload[file]).hexdigest()
    payload['README.md']='''# 文件身份和 20 次切换实验 v1

只在已有快照的 VMware Windows 来宾中运行。完整解压到新目录，打开管理员、64 位 Windows PowerShell 5.1：

```powershell
.\\scripts\\Invoke-AppLockerIdentityLatency.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

自动完成 20 次启用/撤销、172 次正式样本观察。仅目标是本包生成的无害样本及其同内容副本。
结束后复制完整 identity-latency-时间 目录回主机项目 reports。不重跑之前已经通过的包。
如失败，保留报告与 ProgramData 实验目录；独立清理任务在实验开始十分钟后或下次启动时运行。
即时触发/恢复和范围详见 docs/APPLOCKER_IDENTITY_LATENCY.md。
本包不是完整软件，不在主机上封锁，不改变执行策略、服务配置、浏览器或网络。
'''.encode('utf-8')
    manifest={'schema_version':1,'kind':'AICT_APPLOCKER_IDENTITY_LATENCY_V1','target_sip_hash':sip,'cycles':20,
              'files':{name:hashlib.sha256(raw).hexdigest() for name,raw in payload.items()}}
    payload['MANIFEST.json']=json.dumps(manifest,ensure_ascii=False,indent=2).encode('utf-8')
    payload['SHA256SUMS.txt']=''.join(hashlib.sha256(raw).hexdigest()+'  '+name+'\n' for name,raw in payload.items()).encode('ascii')
    with zipfile.ZipFile(output,'x',compression=zipfile.ZIP_DEFLATED) as bundle:
        for name,raw in payload.items():bundle.writestr(name,raw)
    with zipfile.ZipFile(output) as bundle:
        assert bundle.testzip() is None and set(bundle.namelist())==set(payload)
        assert all(bundle.read(name)==raw for name,raw in payload.items())
    digest=hashlib.sha256(output.read_bytes()).hexdigest()
    with checksum.open('x',encoding='ascii') as out:out.write(digest+'  '+output.name+'\n')
    print(json.dumps({'package':str(output),'sha256':digest,'entries':len(payload),'bytes':output.stat().st_size}))


if __name__=='__main__':main()
