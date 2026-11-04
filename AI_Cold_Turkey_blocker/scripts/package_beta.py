"""Freeze the integrated VMware beta; never installs or changes Windows policy."""
import hashlib
import json
import re
import zipfile
from datetime import datetime, timezone
from pathlib import Path


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    import argparse
    parser=argparse.ArgumentParser()
    parser.add_argument('--host',action='store_true',help='Package the persistent host build; short trial is an optional installer choice')
    parser.add_argument('--host-trial',action='store_true',help=argparse.SUPPRESS)
    args=parser.parse_args()
    if args.host_trial:parser.error('Use --host for the current release; choose optional short trial in the installation wizard')
    host=args.host
    root = Path(__file__).resolve().parents[1]
    build = json.loads((root / ('lab/build/host-trial/last-build.json' if host else 'lab/build/beta/last-build.json')).read_text('utf-8-sig'))
    payload = Path(build['payloadDirectory']).resolve()
    payload.relative_to((root / ('lab/build/host-trial' if host else 'lab/build/beta')).resolve())
    ui = json.loads((payload / 'ui-self-test/result.json').read_text('utf-8-sig'))
    assert ui['passed'] and ui['systemWrites'] is False
    output = root / ('dist/AICT-self-use-full-v11.zip' if host else 'dist/AICT-self-use-beta-v1.zip')
    sidecar = output.with_suffix('.zip.sha256')
    if output.exists() or sidecar.exists():
        raise FileExistsError('Frozen release exists; choose a new version')

    files = {}
    for folder in ('Service', 'Agent', 'App', 'Extension', 'Targets', 'scripts'):
        for path in sorted((payload / folder).rglob('*')):
            if path.is_file():
                assert not path.is_symlink()
                files[path.relative_to(payload).as_posix()] = path
    for name in ('BetaBridge.exe', 'MovedTarget.exe', '专注计划.exe', '管理员恢复工具.exe'):
        files[name] = payload / name
    files['README.md'] = root / ('docs/TELEMETRY_V11.md' if host else 'docs/INTEGRATED_BETA.md')
    files['集中测试清单.md'] = root / ('docs/HOST_TEST_CHECKLIST.md' if host else 'docs/BETA_TEST_CHECKLIST.md')
    for project in sorted((root / 'src').iterdir()):
        if not project.is_dir():
            continue
        for path in sorted(project.rglob('*')):
            rel = path.relative_to(root)
            if path.is_file() and not {'bin', 'obj'}.intersection(rel.parts) and path.suffix in (
                    '.cs', '.csproj', '.xaml', '.json', '.manifest'):
                files['Sources/' + rel.as_posix()] = path
    for path in (root / 'browser-extension/beta').glob('*'):
        if path.is_file():
            files['Sources/browser-extension/beta/' + path.name] = path
    for name in ('Directory.Build.props', 'NuGet.config', 'global.json'):
        path = root / name
        if path.exists():
            files['Sources/' + name] = path
    for name in ('BetaBridge.cs', 'BetaPolicyXml.cs', 'BetaNative.cs', 'BetaEntry.cs'):
        files['Sources/lab/' + name] = root / 'lab' / name
    for name in ('FixtureIdentity.cs', 'AuthenticodeHash.cs'):
        files['Sources/lab/' + name] = payload / name
    for path in (root / 'tests/Aict.Core.Tests').glob('*'):
        if path.is_file() and path.suffix in ('.cs', '.csproj', '.json'):
            files['Sources/tests/Aict.Core.Tests/' + path.name] = path
    for name in ('Test-BetaGuards.ps1', 'Test-BetaBindings.ps1', 'extension-rules.test.mjs'):
        files['Sources/tests/' + name] = root / 'tests' / name
    for name in ('Build-Beta.ps1', 'package_beta.py'):
        files['Sources/scripts/' + name] = root / 'scripts' / name
    contents = {
        '开始使用.txt': ('专注计划 · 自用测试版\r\n\r\n'
                       '1. 将本包完整解压到 VMware Windows 11 虚拟机的新文件夹。无需先恢复快照。\r\n'
                       '2. 双击本目录“专注计划.exe”，点击安装并完成管理员确认。\r\n'
                       '3. 日常使用桌面“专注计划”快捷方式。\r\n'
                       '4. 在“浏览器设置”按图形引导加载组件一次，再启用网站规则。\r\n'
                       '5. 请先用短规则，按“集中测试清单.md”集中测试。\r\n'
                       '6. 诊断/恢复 → 导出规则、用量和日志，回传导出文件夹。\r\n\r\n'
                       '详细说明见 README.md。物理主机安装被禁止，来宾新集成功能等待实测。\r\n'
                       '误封/损坏才使用独立“管理员恢复工具.exe”，数据保留。\r\n').encode('utf-8-sig')
    }
    assert all(p.exists() for p in files.values())
    assert files['scripts/Install-Beta.ps1'].read_bytes().startswith(b'\xef\xbb\xbf')
    hashes = {name: digest(path) for name, path in sorted(files.items())}
    hashes.update({name: hashlib.sha256(data).hexdigest() for name, data in contents.items()})
    identity = (payload / 'FixtureIdentity.cs').read_text('utf-8-sig')
    target_hash = re.search(r'TargetSipHash="([^"]+)"', identity).group(1)
    target_size = int(re.search(r'TargetSize=(\d+)L', identity).group(1))
    extension = json.loads((payload / 'Extension/manifest.json').read_text('utf-8-sig'))
    import base64
    key_hash = hashlib.sha256(base64.b64decode(extension['key'])).hexdigest()[:32]
    extension_id = ''.join(chr(ord('a') + int(x, 16)) for x in key_hash)
    assert extension_id == (payload / 'Extension/extension-id.txt').read_text().strip()
    manifest = {'kind': 'AICT_INTEGRATED_BETA_V1', 'version': '0.8.0' if host else '0.1.0',
                'created_utc': datetime.now(timezone.utc).isoformat(), 'vmware_only': not host, 'host_trial':False, 'continuous_use_default':host, 'optional_short_trial':host, 'trial_minutes':None,
                'normal_entry': '专注计划.exe', 'requires_command_line': False,
                'extension_id': extension_id, 'extension_deployment': 'UNPACKED_SELF_USE' if host else 'UNPACKED_VM_TEST_ONLY',
                'windows_integration_acceptance': 'NOT_RUN',
                'host_installation_authorized': host,
                'target_sip_hash': target_hash, 'target_size': target_size,
                'files': dict(sorted(hashes.items()))}
    if host:
        contents['开始使用.txt']=('专注计划 · 持续使用\r\n\r\n双击“专注计划.exe”安装，之后使用桌面“专注计划”入口。初始为空规则，提交后按日程、额度和锁定条件持续执行，没有统一的30分钟截止时间。\r\n新安装可选择短测，但默认不勾选；已有安装修复保留原模式和数据库。\r\n首次连接浏览器组件后再启用网站规则。误封时使用“管理员恢复工具.exe”。详细说明见 README.md。\r\n').encode('utf-8-sig')
        hashes['开始使用.txt']=hashlib.sha256(contents['开始使用.txt']).hexdigest()
        manifest['files']['开始使用.txt']=hashes['开始使用.txt']
    contents['MANIFEST.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    all_hashes = dict(hashes)
    all_hashes['MANIFEST.json'] = hashlib.sha256(contents['MANIFEST.json']).hexdigest()
    contents['SHA256SUMS.txt'] = ''.join(h + '  ' + n + '\n' for n, h in sorted(all_hashes.items())).encode('utf-8')
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, path in sorted(files.items()):
            archive.write(path, name)
        for name, data in contents.items():
            archive.writestr(name, data)
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist()) == set(files) | set(contents)
        for name, expected in all_hashes.items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == expected, name
        assert not any('ui-self-test' in n or n.endswith('.sqlite') for n in archive.namelist())
    final_hash = digest(output)
    with sidecar.open('x', encoding='utf-8') as stream:
        stream.write(final_hash + '  ' + output.name + '\n')
    receipt = {'package': str(output), 'bytes': output.stat().st_size, 'sha256': final_hash,
               'entries': len(files) + len(contents), 'build_directory': build['buildDirectory'],
               'manifest_sha256': all_hashes['MANIFEST.json'],
               'windows_integration_acceptance': 'NOT_RUN'}
    if host:
        extracted=root/'dist/AICT-self-use-full-v11'
        if extracted.exists():raise FileExistsError('Host trial folder already exists')
        with zipfile.ZipFile(output) as archive:archive.extractall(extracted)
        assert all(digest(extracted/name)==expected for name,expected in all_hashes.items())
        receipt['extracted_directory']=str(extracted)
    print(json.dumps(receipt, ensure_ascii=False))


if __name__ == '__main__':
    main()
