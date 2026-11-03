"""Download Microsoft's stable SDK ZIP into this project only; verify its published SHA512."""
import hashlib
import json
from pathlib import Path,PureWindowsPath
import re
import shutil
import urllib.request
import uuid
import zipfile

METADATA='https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'


def main():
    root=Path(__file__).resolve().parents[1]; tools=root/'.tools'; tools.mkdir(exist_ok=True)
    if shutil.disk_usage(root).free<2*1024**3:raise OSError('At least 2 GiB free required; no global installation attempted')
    raw=urllib.request.urlopen(METADATA,timeout=45).read(); data=json.loads(raw)
    pin=root/'global.json'
    version=json.loads(pin.read_text(encoding='utf-8'))['sdk']['version'] if pin.exists() else data['latest-sdk']
    if not re.fullmatch(r'10\.0\.\d+',version) or data['release-type']!='lts':raise ValueError('Stable .NET 10 LTS SDK required')
    candidates=[sdk for release in data['releases'] for sdk in release.get('sdks',[release.get('sdk',{})]) if sdk.get('version')==version]
    file=next(f for sdk in candidates for f in sdk['files'] if f['rid']=='win-x64' and f['name'].endswith('.zip'))
    if not file['url'].startswith('https://builds.dotnet.microsoft.com/dotnet/'):raise ValueError('Unexpected SDK download origin')
    expected=file['hash'].lower()
    if not re.fullmatch('[a-f0-9]{128}',expected):raise ValueError('Published SHA512 missing')
    downloads=tools/'downloads';downloads.mkdir(exist_ok=True)
    archive=downloads/('dotnet-sdk-'+version+'-win-x64.zip');destination=tools/('dotnet-'+version)
    if not archive.exists():
        partial=downloads/(archive.name+'.'+uuid.uuid4().hex+'.partial')
        digest=hashlib.sha512();total=0
        with urllib.request.urlopen(file['url'],timeout=90) as response,partial.open('xb') as out:
            while chunk:=response.read(1024*1024):out.write(chunk);digest.update(chunk);total+=len(chunk)
        if digest.hexdigest()!=expected:raise ValueError('SDK checksum mismatch; partial preserved and not executed')
        partial.rename(archive)
    with archive.open('rb') as source:digest=hashlib.file_digest(source,'sha512').hexdigest()
    if digest!=expected:raise ValueError('Existing archive differs from official SHA512')
    if not destination.exists():
        staging=tools/('staging-dotnet-'+uuid.uuid4().hex);staging.mkdir()
        with zipfile.ZipFile(archive) as z:
            for member in z.infolist():
                path=PureWindowsPath(member.filename)
                if path.is_absolute() or path.drive or '..' in path.parts:raise ValueError('Unsafe SDK ZIP entry')
                if not (staging/member.filename).resolve().is_relative_to(staging.resolve()):raise ValueError('SDK entry escapes staging')
            z.extractall(staging)
        # Both directory targets are resolved/checked before this one atomic directory rename.
        if not staging.resolve().is_relative_to(tools.resolve()) or not destination.resolve().is_relative_to(tools.resolve()):raise ValueError('SDK rename outside project tools')
        staging.rename(destination)
    receipt={'kind':'PROJECT_LOCAL_DOTNET_SDK','version':version,'metadata_url':METADATA,'archive_url':file['url'],'sha512':expected,'sdk_directory':str(destination),'global_environment_modified':False}
    (tools/'dotnet-sdk.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
    print(json.dumps(receipt),flush=True)


if __name__=='__main__':main()
