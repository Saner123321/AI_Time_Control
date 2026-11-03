"""Freeze the VM-only interactive candidate; never install a service or modify OS policy."""
import hashlib
import json
from pathlib import Path
import re
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    root = Path(__file__).resolve().parents[1]
    build_info = json.loads((root / 'lab/build/interactive/last-build.json').read_text('utf-8'))
    build = Path(build_info['payloadDirectory']).resolve()
    build.relative_to((root / 'lab/build/interactive').resolve())
    result = json.loads((build / 'ui-self-test/result.json').read_text('utf-8'))
    if not result.get('passed') or result.get('systemEnforcementActive') is not False:
        raise ValueError('Own-window simulation rendering must pass without host enforcement')
    frozen = root / 'dist/AICT-service-policy-lab-v1.zip'
    if digest(frozen.read_bytes()) != build_info['basePackageSha256']:
        raise ValueError('Prior passed fixture release changed')
    archive = root / 'dist/AICT-interactive-lab-v1.zip'
    sidecar = archive.with_suffix('.zip.sha256')
    if archive.exists() or sidecar.exists():
        raise FileExistsError('Frozen candidate exists; use a new version')
    paths = {}
    for folder in ('Service', 'App'):
        for path in (build / folder).rglob('*'):
            if path.is_file():
                paths[folder + '/' + path.relative_to(build / folder).as_posix()] = path
    for name in ('AictInteractiveBridge.exe', 'AictDemo.exe', 'AictPolicyTarget.exe',
                 'AictPolicyControl.exe', 'FixtureIdentity.cs', 'AuthenticodeHash.cs', 'ServicePolicyNative.cs'):
        paths['lab/' + name] = build / 'lab' / name
    for name in ('InteractivePolicyBridge.cs', 'InteractiveDemo.cs'):
        paths['lab/' + name] = root / 'lab' / name
    for name in ('ServicePolicyLabCommon.ps1', 'InteractiveLabCommon.ps1', 'Install-InteractiveLab.ps1',
                 'Open-InteractiveLab.ps1', 'Collect-InteractiveLab.ps1', 'Recover-InteractiveLab.ps1',
                 'Build-InteractiveLab.ps1', 'package_interactive_lab.py'):
        paths['scripts/' + name] = root / 'scripts' / name
    paths['README.md'] = root / 'docs/INTERACTIVE_LAB.md'
    paths['docs/INTERACTIVE_LAB.md'] = root / 'docs/INTERACTIVE_LAB.md'
    paths['tests/Test-InteractiveLabGuards.ps1'] = root / 'tests/Test-InteractiveLabGuards.ps1'
    for name in ('Directory.Build.props', 'global.json', 'NuGet.Config'):
        paths[name] = root / name
    for project in ('Aict.Core', 'Aict.Storage', 'Aict.Contracts', 'Aict.Enforcement',
                    'Aict.Backend', 'Aict.Service', 'Aict.Desktop'):
        base = root / 'src' / project
        for path in base.rglob('*'):
            if path.is_file() and not {'obj', 'bin'}.intersection(path.relative_to(base).parts):
                paths['src/' + project + '/' + path.relative_to(base).as_posix()] = path
    payload = {name: path.read_bytes() for name, path in sorted(paths.items())}
    if any(Path(name).suffix in ('.sqlite', '.db') or 'ui-self-test' in name for name in payload):
        raise ValueError('No test state may be shipped')
    contract = payload['lab/FixtureIdentity.cs'].decode('utf-8-sig')
    def field(name):
        return re.search(name + r'="([A-Fa-f0-9x]+)"', contract)[1]
    for name, file in (('TargetSha256', 'lab/AictPolicyTarget.exe'), ('ControlSha256', 'lab/AictPolicyControl.exe')):
        if field(name) != digest(payload[file]):
            raise ValueError('Frozen fixture identity mismatch')
    manifest = {
        'schema_version': 1, 'kind': 'AICT_INTERACTIVE_LAB_V1',
        'vmware_only': True, 'mode': 'VM_INTERACTIVE_APPLICATION_CONTROL',
        'target_sip_hash': field('TargetSipHash'), 'target_size': len(payload['lab/AictPolicyTarget.exe']),
        'base_package_sha256': build_info['basePackageSha256'],
        'supported': ['EXE_PE_SIP_HASH_BLOCKLIST', 'WEEKLY_SCHEDULE', 'FIXED_CYCLE', 'FROZEN_CHANGE_DELAY', 'SCHEDULE_LOCK'],
        'not_connected': ['WEBSITES', 'BROWSER_EXTENSION', 'ACTUAL_USAGE', 'DAILY_QUOTA', 'APPLICATION_ALLOWLIST', 'MSIX', 'HOST_INSTALLER'],
        'guest_acceptance': 'NOT_RUN', 'manual_resistance_checks': 'NOT_RUN',
        'lab_expiry_minutes': 60, 'windows_reboot_required': False,
        'files': {name: digest(data) for name, data in payload.items()},
    }
    payload['MANIFEST.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    payload['SHA256SUMS.txt'] = ''.join(digest(data) + '  ' + name + '\n' for name, data in payload.items()).encode('ascii')
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for name, data in payload.items():
            zipped.writestr(name, data)
    with zipfile.ZipFile(archive) as zipped:
        if zipped.testzip() is not None or set(zipped.namelist()) != set(payload):
            raise ValueError('Archive integrity failed; preserve candidate for investigation')
        for name, data in payload.items():
            if zipped.read(name) != data:
                raise ValueError('Archive payload changed')
    package_hash = digest(archive.read_bytes())
    with sidecar.open('x', encoding='ascii') as out:
        out.write(package_hash + '  ' + archive.name + '\n')
    print(json.dumps({'package': str(archive), 'sha256': package_hash, 'bytes': archive.stat().st_size,
                      'entries': len(payload), 'build': str(build), 'guest_acceptance': 'NOT_RUN'}))


if __name__ == '__main__':
    main()
