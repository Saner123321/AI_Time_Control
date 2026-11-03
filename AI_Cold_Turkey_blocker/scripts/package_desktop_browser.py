"""Freeze the graphical entry and fixed-domain VM pilot, without system installation."""
import hashlib,json,zipfile
from pathlib import Path

def main():
    root=Path(__file__).resolve().parents[1]
    build=Path(json.loads((root/'lab/build/desktop-browser/last-build.json').read_text('utf-8-sig'))['buildDirectory'])
    build.resolve().relative_to((root/'lab/build/desktop-browser').resolve())
    out=root/'dist/AICT-desktop-browser-v1.zip'
    sidecar=out.with_suffix('.zip.sha256')
    if out.exists() or sidecar.exists():raise FileExistsError('Frozen release exists; use a new version')
    files={'专注计划.exe':build/'专注计划.exe','BASE-APP-HASHES.json':build/'BASE-APP-HASHES.json',
           'README.md':root/'docs/DESKTOP_BROWSER_PILOT.md','src/DesktopPortal.cs':root/'lab/DesktopPortal.cs',
           'src/BrowserPolicySlot.cs':root/'src/Aict.Enforcement/BrowserPolicySlot.cs',
           'tests/BrowserPolicySlotTests.cs':root/'tests/Aict.Core.Tests/BrowserPolicySlotTests.cs',
           'tests/Test-DesktopPortal.ps1':root/'tests/Test-DesktopPortal.ps1',
           'scripts/Build-DesktopPortal.ps1':root/'scripts/Build-DesktopPortal.ps1'}
    assert (build/'render/entry.png').exists() and (build/'render/browser.png').exists()
    payload={name:path.read_bytes() for name,path in files.items()}
    sha=lambda b:hashlib.sha256(b).hexdigest()
    manifest={'kind':'AICT_DESKTOP_BROWSER_PILOT_V1','vmware_only':True,'normal_entry':'专注计划.exe',
              'uses_existing_application_service':True,'installs_or_resets_application_service':False,
              'test_domain':'example.com','browser_policy_scope':'CURRENT_USER_ONLY','browser_acceptance':'NOT_RUN',
              'browser_policy_cleanup_attempt_after_minutes':15,'requires_command_line':False,
              'files':{name:sha(data) for name,data in payload.items()}}
    payload['MANIFEST.json']=(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    payload['SHA256SUMS.txt']=''.join(sha(data)+'  '+name+'\n' for name,data in payload.items()).encode('utf-8')
    with zipfile.ZipFile(out,'x',zipfile.ZIP_DEFLATED) as z:
        for name,data in payload.items():z.writestr(name,data)
    with zipfile.ZipFile(out) as z:
        assert z.testzip() is None
        assert all(z.read(name)==data for name,data in payload.items())
    digest=sha(out.read_bytes())
    with sidecar.open('x',encoding='utf-8') as f:f.write(digest+'  '+out.name+'\n')
    print(json.dumps({'package':str(out),'bytes':out.stat().st_size,'sha256':digest,'entries':len(payload)},ensure_ascii=False))

if __name__=='__main__':main()
