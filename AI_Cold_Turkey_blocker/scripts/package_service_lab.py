"""Freeze the service-only VM lab; never overwrite a prior published archive."""
import hashlib
import json
from pathlib import Path
import zipfile


def main():
    root=Path(__file__).resolve().parents[1]; package=root/'dist/AICT-service-boot-lab-v1.zip'; checksum=package.with_suffix('.zip.sha256')
    if package.exists() or checksum.exists():raise FileExistsError('Frozen package already exists; publish a new version')
    files={
        'lab/AictServiceLab.exe':'lab/build/service/AictServiceLab.exe',
        'lab/ServiceLab.cs':'lab/ServiceLab.cs',
        'scripts/ServiceLabCommon.ps1':'scripts/ServiceLabCommon.ps1',
        'scripts/Prepare-ServiceLab.ps1':'scripts/Prepare-ServiceLab.ps1',
        'scripts/Finish-ServiceLab.ps1':'scripts/Finish-ServiceLab.ps1',
        'scripts/Recover-ServiceLab.ps1':'scripts/Recover-ServiceLab.ps1',
        'docs/SERVICE_BOOT_LAB.md':'docs/SERVICE_BOOT_LAB.md',
    }
    payload={name:(root/path).read_bytes() for name,path in files.items()}
    payload['README.md']='''# 服务权限/崩溃恢复/重启实验 v1

只在有恢复快照的 VMware Windows 来宾运行；不配置应用或网站封锁。
在桌面完整解压为 AICT-service-boot-lab-v1，**在桌面**打开管理员、64 位 Windows PowerShell：

```powershell
.\\AICT-service-boot-lab-v1\\scripts\\Prepare-ServiceLab.ps1 -SnapshotReference 'AICT-phase0-baseline'
```

仅出现 Prepare passed 后，重启该虚拟机，登录同一账户，再在桌面的管理员 PowerShell 运行：

```powershell
.\\AICT-service-boot-lab-v1\\scripts\\Finish-ServiceLab.ps1
```

完成会移除本轮服务/任务。把 service-lab-finish-时间 整个目录复制回主机项目 reports。
尽快完成：独立清理在准备后30分钟或下次启动10分钟后执行；不要停用它。
失败或希望提前撤销：

```powershell
.\\AICT-service-boot-lab-v1\\scripts\\Recover-ServiceLab.ps1
```

保留报告和受保护目录，不反复重跑 Prepare。完整范围、故障与证据说明见 docs/SERVICE_BOOT_LAB.md。
仅验证测试服务及心跳状态，不证明封锁规则或每日额度已跨重启有效。
'''.encode('utf-8')
    manifest={'schema_version':1,'kind':'AICT_SERVICE_BOOT_LAB_V1','no_blocking_policy':True,'files':{name:hashlib.sha256(raw).hexdigest() for name,raw in payload.items()}}
    payload['MANIFEST.json']=json.dumps(manifest,ensure_ascii=False,indent=2).encode('utf-8')
    payload['SHA256SUMS.txt']=''.join(hashlib.sha256(raw).hexdigest()+'  '+name+'\n' for name,raw in payload.items()).encode('ascii')
    with zipfile.ZipFile(package,'x',compression=zipfile.ZIP_DEFLATED) as bundle:
        for name,raw in payload.items():bundle.writestr(name,raw)
    with zipfile.ZipFile(package) as bundle:
        assert bundle.testzip() is None and set(bundle.namelist())==set(payload)
        assert all(bundle.read(name)==raw for name,raw in payload.items())
    digest=hashlib.sha256(package.read_bytes()).hexdigest()
    with checksum.open('x',encoding='ascii') as out:out.write(digest+'  '+package.name+'\n')
    print(json.dumps({'package':str(package),'sha256':digest,'entries':len(payload),'bytes':package.stat().st_size}))


if __name__=='__main__':main()
