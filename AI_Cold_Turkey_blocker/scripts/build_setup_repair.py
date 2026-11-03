"""Build a small, report-scoped VMware repair; never runs privileged actions."""
import hashlib
import json
import subprocess
import zipfile
from datetime import datetime, timezone
from pathlib import Path


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    root = Path(__file__).resolve().parents[1]
    report = root / 'reports/setup-result.json'
    result = json.loads(report.read_text('utf-8-sig'))
    assert not result['completed'] and result['root'].endswith('AICT-Beta-111e0fe3cfa348b4a055eafb99e25f06')
    assert 'probe' in result['error'] and 'FileNotFoundException' in result['error']
    original = root / 'dist/AICT-self-use-beta-v1.zip'
    assert sha(original) == '9cf460f469dc64a1935121db7677b80e8e58ca3ad4a526307437ffcffe22a6fa'
    build = root / 'lab/build/setup-repair' / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    build.mkdir(parents=True)
    repair = root / 'scripts/Repair-BetaSetup.ps1'
    code = (root / 'lab/BetaSetupRepair.cs').read_text('utf-8-sig').replace('REPAIR_SCRIPT_SHA256', sha(repair))
    (build / 'BetaSetupRepair.cs').write_text(code, encoding='utf-8-sig')
    compiler = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    exe = build / '修复并继续安装.exe'
    subprocess.run([compiler, '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
                    '/out:' + str(exe), str(build / 'BetaSetupRepair.cs')], cwd=root, check=True)
    # Compile the shared production native guard for a real filesystem layout regression.
    payload = Path(json.loads((root / 'lab/build/beta/last-build.json').read_text('utf-8-sig'))['payloadDirectory'])
    bridge = build / 'BetaBridge.exe'
    mi = r'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
    source = [root / 'lab/BetaBridge.cs', root / 'lab/BetaPolicyXml.cs', root / 'src/Aict.Enforcement/AppLockerXml.cs',
              root / 'src/Aict.Enforcement/InteractivePolicyXml.cs', payload / 'AuthenticodeHash.cs',
              payload / 'FixtureIdentity.cs', root / 'lab/BetaNative.cs']
    subprocess.run([compiler, '/nologo', '/target:exe', '/platform:x64', '/optimize+',
                    '/r:System.Management.dll', '/r:System.Web.Extensions.dll', '/r:' + mi,
                    '/out:' + str(bridge)] + [str(p) for p in source], cwd=root, check=True)
    powershell = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    tests = {}
    def run(name, args):
        p = subprocess.run(args, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
        (build / (name + '.txt')).write_bytes(p.stdout)
        assert p.returncode == 0, p.stdout.decode('utf-8', 'replace')
        tests[name] = {'passed': True, 'log': (build / (name + '.txt')).relative_to(root).as_posix(), 'sha256': sha(build / (name + '.txt'))}
    run('layout_regression', [powershell, '-NoProfile', '-File', str(root / 'tests/Test-BetaSetupLayout.ps1'), '-Bridge', str(bridge), '-Payload', str(payload)])
    # Installer and UI both refuse the physical host before any repair writes.
    refused = subprocess.run([str(exe), '--guard-check'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
    assert refused.returncode == 1 and b'VMware guest required' in refused.stderr
    guard = "$taskRefused=$false;try { & '" + str(repair).replace("'", "''") + "' }catch{if($_.Exception.Message -match 'VMware guest required'){$taskRefused=$true}else{throw}};if(-not $taskRefused){throw 'Repair wrote on host'};Write-Output 'Repair refused physical host before writes'"
    run('repair_host_guard', [powershell, '-NoProfile', '-Command', guard])
    run('xml_validation', [str(bridge), '--validate'])
    files = {'修复并继续安装.exe': exe.read_bytes(), 'Repair-BetaSetup.ps1': repair.read_bytes(),
             'Sources/BetaSetupRepair.cs': (build / 'BetaSetupRepair.cs').read_bytes(),
             'Sources/Repair-BetaSetup.ps1': repair.read_bytes()}
    files['开始使用.txt'] = ('专注计划 · 安装修复\r\n\r\n'
        '1. 将本小包复制到刚才的 Windows 11 VMware 虚拟机，完整解压到新文件夹。\r\n'
        '2. 关闭旧的安装提示窗口，双击“修复并继续安装.exe”。\r\n'
        '3. 点击“修复并继续安装”，完成管理员确认，保留窗口等结果。\r\n'
        '4. 成功后从桌面“专注计划”打开；不需要重新复制原来的大安装包。\r\n'
        '5. 若未完成，把 repair-result.json 和 setup-result-after-repair.json 复制回项目 reports。\r\n\r\n'
        '仅补齐你报告中这一份安装缺失的 probe 目录，再调用原来的安装程序。\r\n'
        '已有文件先校验，不清空规则、不新建会话，不恢复快照，不在物理主机执行。\r\n'
        '本地已验证目录缺失的复现/修复与主机拒绝；虚拟机安装仍需确认。\r\n').encode('utf-8-sig')
    manifest = {'kind': 'AICT_BETA_SETUP_DIRECTORY_REPAIR_V1', 'vmware_only': True,
                'target_run_id': '111e0fe3cfa348b4a055eafb99e25f06', 'original_package_sha256': sha(original),
                'files': {n: hashlib.sha256(b).hexdigest() for n, b in files.items()},
                'guest_repair_acceptance': 'NOT_RUN'}
    files['MANIFEST.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    files['SHA256SUMS.txt'] = ''.join(hashlib.sha256(b).hexdigest() + '  ' + n + '\n' for n, b in files.items()).encode('utf-8')
    output = root / 'dist/AICT-beta-setup-repair-v1.zip'
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED) as archive:
        for n, b in files.items():
            archive.writestr(n, b)
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        assert all(archive.read(n) == b for n, b in files.items())
    with output.with_suffix('.zip.sha256').open('x', encoding='utf-8') as stream:
        stream.write(sha(output) + '  ' + output.name + '\n')
    receipt = {'kind': 'AICT_BETA_SETUP_REPAIR_LOCAL_VALIDATION', 'source_report_sha256': sha(report),
               'reported_failure': 'MISSING_RUNTIME_PROBE_DIRECTORY_DURING_LEGACY_IMPORT',
               'package': output.relative_to(root).as_posix(), 'sha256': sha(output), 'bytes': output.stat().st_size,
               'checks': tests, 'gui_physical_host_refused': True,
               'existing_v1_package_unchanged': sha(original) == manifest['original_package_sha256'],
               'guest_repair_installation': 'NOT_RUN', 'host_installation_performed': False}
    with (root / 'reports/beta-setup-repair-v1-local-checks-20261103.json').open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, ensure_ascii=False, indent=2)
    print(json.dumps(receipt, ensure_ascii=False))


if __name__ == '__main__':
    main()
