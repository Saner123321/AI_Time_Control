"""Freeze the narrow VM integration release. No overwrite, install, policy, or task actions."""
import hashlib
import json
from pathlib import Path
import re
import zipfile


def main():
    root=Path(__file__).resolve().parents[1];build=root/'lab/build/service-policy'
    archive=root/'dist/AICT-service-policy-lab-v1.zip';sidecar=archive.with_suffix('.zip.sha256')
    if archive.exists() or sidecar.exists():raise FileExistsError('Frozen release exists; use a new version')
    paths={
        'lab/AictPolicyBridge.exe':build/'AictPolicyBridge.exe','lab/AictPolicyTarget.exe':build/'AictPolicyTarget.exe','lab/AictPolicyControl.exe':build/'AictPolicyControl.exe',
        'lab/FixtureIdentity.cs':build/'FixtureIdentity.cs','lab/AuthenticodeHash.cs':build/'AuthenticodeHash.cs',
        'lab/ServicePolicyBridge.cs':root/'lab/ServicePolicyBridge.cs','lab/ServicePolicyNative.cs':root/'lab/ServicePolicyNative.cs',
    }
    for name in ('ServicePolicyLabCommon.ps1','Invoke-ServicePolicyLab.ps1','Recover-ServicePolicyLab.ps1','verify_service_policy_lab.py'):
        paths['scripts/'+name]=root/'scripts'/name
    paths['docs/SERVICE_POLICY_LAB.md']=root/'docs/SERVICE_POLICY_LAB.md'
    paths['README.md']=root/'docs/SERVICE_POLICY_LAB.md'
    for p in (build/'Service').rglob('*'):
        if p.is_file():paths['Service/'+p.relative_to(build/'Service').as_posix()]=p
    for project in ('Aict.Core','Aict.Storage','Aict.Contracts','Aict.Enforcement','Aict.Service'):
        base=root/'src'/project
        for p in base.rglob('*'):
            if p.is_file() and not {'obj','bin'}.intersection(p.relative_to(base).parts):paths['src/'+project+'/'+p.relative_to(base).as_posix()]=p
    payload={name:p.read_bytes() for name,p in paths.items()}
    contract=payload['lab/FixtureIdentity.cs'].decode('utf-8')
    field=lambda name:re.search(name+r'="([A-Fa-f0-9x]+)"',contract)[1]
    for name,file in [('TargetSha256','lab/AictPolicyTarget.exe'),('ControlSha256','lab/AictPolicyControl.exe')]:
        assert field(name)==hashlib.sha256(payload[file]).hexdigest()
    manifest={'schema_version':1,'kind':'AICT_SERVICE_POLICY_LAB_V1','fixed_fixture_only':True,'target_sip_hash':field('TargetSipHash'),
        'target_size':len(payload['lab/AictPolicyTarget.exe']),'windows_reboot_tested_by_this_batch':False,
        'files':{name:hashlib.sha256(data).hexdigest() for name,data in payload.items()}}
    payload['MANIFEST.json']=(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    payload['SHA256SUMS.txt']=''.join(hashlib.sha256(data).hexdigest()+'  '+name+'\n' for name,data in payload.items()).encode('ascii')
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as zipped:
        for name,data in payload.items():zipped.writestr(name,data)
    with zipfile.ZipFile(archive) as zipped:
        assert zipped.testzip() is None and set(zipped.namelist())==set(payload)
        assert all(hashlib.sha256(zipped.read(name)).digest()==hashlib.sha256(data).digest() for name,data in payload.items())
    digest=hashlib.sha256(archive.read_bytes()).hexdigest()
    with sidecar.open('x',encoding='ascii') as out:out.write(digest+'  '+archive.name+'\n')
    print(json.dumps({'package':str(archive),'sha256':digest,'bytes':archive.stat().st_size,'entries':len(payload)}))


if __name__=='__main__':main()
