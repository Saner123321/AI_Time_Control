"""Read-only evidence checks and local beta receipt. No guest acceptance is granted."""
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, data):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


def main():
    root = Path(__file__).resolve().parents[1]
    now = datetime.now(timezone.utc).isoformat()
    original = root / 'reports/desktop-browser-20261103T045617399Z'
    files = {}
    for line in (original / 'SHA256SUMS.txt').read_text('utf-8-sig').splitlines():
        expected, name = line.split('  ', 1)
        path = (original / name).resolve()
        path.relative_to(original.resolve())
        assert sha(path) == expected, name
        files[name] = expected
    entry = json.loads((original / 'entry-result.json').read_text('utf-8-sig'))
    frozen = root / 'dist/AICT-desktop-browser-v1.zip'
    assert sha(frozen) == '9bfcf9b623b33c4c9651d6fb9173a6f1098d422de6de24df94f7927186ed5d69'
    with zipfile.ZipFile(frozen) as archive:
        assert hashlib.sha256(archive.read('专注计划.exe')).hexdigest() == entry['entrySha256']
    pilots = [json.loads(p.read_text('utf-8-sig')) for p in sorted(original.glob('browser-*.json'))]
    assert len(files) == 5 and len(pilots) == 2
    assert all(not p['RegistryWritten'] and p['RegistryRestored'] and p['TaskRemoved'] for p in pilots)
    assert all(not p['ownSlotPresent'] and not p['ownValueMatches'] for p in entry['browserPolicyReadbacks'])
    assert not entry['browserAcceptanceAutomaticallyAwarded']
    audit_path = root / 'reports/desktop-browser-v1-failure-audit-20261103.json'
    audit = {'kind': 'AICT_DESKTOP_BROWSER_V1_READONLY_AUDIT', 'at_utc': now,
             'source_report': original.relative_to(root).as_posix(), 'source_sha256': files,
             'source_package_sha256': sha(frozen), 'entry_source_verified': True,
             'service_running_in_report': [x['serviceRunningAtEntryOpen'] for x in entry['applicationSessions']],
             'retained_rule_groups': len(json.loads((original / '3adcc4b2458a415fab0d6c66b6b1dc1b/workspace-status.json').read_text('utf-8-sig'))['status']['policy']['groups']),
             'website_policy_writes': 0, 'website_block_acceptance': 'NOT_PASSED',
             'operator_observation': entry['operatorObservation'],
             'pilot_cleanup_recorded_for_both_attempts': True,
             'live_guest_state_not_probed': True, 'source_files_not_modified': True}

    build = json.loads((root / 'lab/build/beta/last-build.json').read_text('utf-8-sig'))
    payload = Path(build['payloadDirectory'])
    package = root / 'dist/AICT-self-use-beta-v1.zip'
    expected_package = package.with_suffix('.zip.sha256').read_text('utf-8').split('  ', 1)[0]
    assert sha(package) == expected_package
    with zipfile.ZipFile(package) as archive:
        assert archive.testzip() is None
        manifest = json.loads(archive.read('MANIFEST.json'))
        assert manifest['kind'] == 'AICT_INTEGRATED_BETA_V1'
        assert manifest['windows_integration_acceptance'] == 'NOT_RUN'
        for name, expected in manifest['files'].items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == expected, name
        for name in ('App/Aict.Beta.Desktop.dll', 'Service/Aict.Beta.Service.dll',
                     'Agent/Aict.Beta.Agent.dll', 'BetaBridge.exe', 'scripts/Install-Beta.ps1'):
            assert sha(payload / name) == manifest['files'][name]
        manifest_sha = hashlib.sha256(archive.read('MANIFEST.json')).hexdigest()

    checks_folder = root / 'artifacts' / ('beta-delivery-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    checks_folder.mkdir()
    powershell = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    results = {}
    for key, command in (
        ('host_refusal', [powershell, '-NoProfile', '-File', str(root / 'tests/Test-BetaGuards.ps1'), '-Payload', str(payload)]),
        ('applocker_xml', [powershell, '-NoProfile', '-File', str(root / 'tests/Test-BetaBindings.ps1'), '-Payload', str(payload)]),
        ('extension_rules', [r'C:\Program Files\nodejs\node.exe', '--test', '--test-reporter=tap', str(root / 'tests/extension-rules.test.mjs')]),
        ('extension_worker_syntax', [r'C:\Program Files\nodejs\node.exe', '--check', str(root / 'browser-extension/beta/worker.js')]),
    ):
        result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90)
        text = result.stdout.decode('utf-8', errors='replace')
        path = checks_folder / (key + '.txt')
        path.write_text(text, encoding='utf-8')
        assert result.returncode == 0, text
        if key == 'extension_rules':
            assert '# pass 6' in text and '# fail 0' in text
        results[key] = {'passed': True, 'log': path.relative_to(root).as_posix(), 'sha256': sha(path)}
    trx = root / 'artifacts/core-tests-20261103T074315604Z/core.trx'
    counters = ET.parse(trx).getroot().find('.//{*}Counters').attrib
    assert counters['total'] == counters['passed'] == '94' and counters['failed'] == '0'
    ui_path = payload / 'ui-self-test/result.json'
    ui = json.loads(ui_path.read_text('utf-8-sig'))
    assert ui['passed'] and not ui['systemWrites']
    phase = root / 'reports/phase0-results.json'
    assert sha(phase) == '80759f9146f9ed187a2706abcfeff5aab9b23da33be757111f2b32a33551e22b'
    assert sha(root / 'dist/AICT-interactive-lab-v1.zip') == '62e74c2ab5f7cb31685450780653d5f73da95371d1edef8ab8ff6b7c58ef38e4'
    host_script = "$taskServices=@(Get-CimInstance Win32_Service -Filter \"Name LIKE 'AICT%'\");$taskTasks=@(Get-ScheduledTask -TaskName 'AICT*' -ErrorAction SilentlyContinue);$taskProcesses=@(Get-Process -Name 'Aict.Beta.*','BetaBridge' -ErrorAction SilentlyContinue);@{services=$taskServices.Count;tasks=$taskTasks.Count;betaProcesses=$taskProcesses.Count}|ConvertTo-Json -Compress"
    result = subprocess.run([powershell, '-NoProfile', '-Command', host_script], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    assert result.returncode == 0
    host = json.loads(result.stdout.decode('utf-8-sig'))
    assert host == {'services': 0, 'tasks': 0, 'betaProcesses': 0}, host
    assert all(sha(original / name) == value for name, value in files.items())
    receipt = {'kind': 'AICT_INTEGRATED_BETA_V1_LOCAL_VALIDATION', 'at_utc': now,
               'package': package.relative_to(root).as_posix(), 'bytes': package.stat().st_size,
               'sha256': expected_package, 'manifest_sha256': manifest_sha,
               'build_directory': str(payload.parent), 'core_tests': {'passed': 94, 'failed': 0, 'trx': trx.relative_to(root).as_posix(), 'sha256': sha(trx)},
               'checks': results, 'ui_self_test': ui, 'ui_self_test_sha256': sha(ui_path),
               'renders': {p.name: sha(p) for p in (payload / 'ui-self-test').glob('*.png')},
               'official_applocker_schema_sha256': sha(root / 'artifacts/official-applocker-schema/policy.xsd'),
               'host_postcheck': host, 'host_enforcement_installation_performed': False,
               'old_packages_and_phase0_results_unchanged': True,
               'received_report_audit': audit_path.relative_to(root).as_posix(),
               'vm_integrated_install_enforce_quota_browser_uninstall_recovery': 'NOT_RUN',
               'timing_2s_5s_target_in_new_integrated_beta': 'NOT_RUN',
               'formal_extension_store_deployment': 'NOT_RUN',
               'platform_gates_automatically_awarded': False}
    save(audit_path, audit)
    receipt_path = root / 'reports/self-use-beta-v1-local-checks-20261103.json'
    save(receipt_path, receipt)
    print(json.dumps({'passed': True, 'receipt': str(receipt_path), 'package_sha256': expected_package}, ensure_ascii=False))


if __name__ == '__main__':
    main()
