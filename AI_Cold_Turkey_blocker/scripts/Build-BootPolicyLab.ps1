#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBase=Join-Path $taskRoot 'dist\AICT-service-policy-lab-v1.zip'
$taskAudit=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'reports\service-policy-v1-audit-20261103T014001416Z.json') | ConvertFrom-Json
if(-not $taskAudit.functional_batch_passed -or (Get-FileHash -LiteralPath $taskBase -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskAudit.package_sha256) { throw 'The already-passed service release must match its evidence audit.' }
$taskBuild=Join-Path $taskRoot ('lab\build\boot-policy\'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskPayload=Join-Path $taskBuild 'payload';$null=New-Item -ItemType Directory -Path $taskPayload
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip=[IO.Compression.ZipFile]::OpenRead($taskBase)
try {
    $taskManifestEntry=$taskZip.GetEntry('MANIFEST.json');$taskReader=New-Object IO.StreamReader($taskManifestEntry.Open())
    try { $taskBaseManifest=$taskReader.ReadToEnd() | ConvertFrom-Json } finally { $taskReader.Dispose() }
    foreach($taskEntry in $taskZip.Entries) {
        if($taskEntry.FullName.EndsWith('/')) { continue }
        if($taskEntry.FullName -notmatch '^(Service/|src/|lab/(FixtureIdentity.cs|AuthenticodeHash.cs|AictPolicyTarget.exe|AictPolicyControl.exe|ServicePolicyNative.cs)$)') { continue }
        if($taskEntry.FullName -match '(^[\\/]|\.\.|:)') { throw 'Unsafe frozen entry path.' }
        $taskDestination=Join-Path $taskPayload $taskEntry.FullName
        $null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,$taskDestination,$false)
        if((Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskBaseManifest.files.($taskEntry.FullName)) { throw 'Frozen entry integrity failed.' }
    }
} finally { $taskZip.Dispose() }
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\ServicePolicyBridge.cs') -Destination (Join-Path $taskPayload 'lab\ServicePolicyBridge.cs')
$taskCompiler="$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskMi="$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
if(-not(Test-Path -LiteralPath $taskCompiler) -or -not(Test-Path -LiteralPath $taskMi)) { throw 'Inbox Framework compiler/MI missing; no global installation attempted.' }
$taskBridge=Join-Path $taskPayload 'lab\AictPolicyBridge.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:'+$taskMi) ('/out:'+$taskBridge) (Join-Path $taskPayload 'lab\ServicePolicyBridge.cs') (Join-Path $taskPayload 'lab\ServicePolicyNative.cs') (Join-Path $taskPayload 'src\Aict.Enforcement\AppLockerXml.cs') (Join-Path $taskPayload 'lab\AuthenticodeHash.cs') (Join-Path $taskPayload 'lab\FixtureIdentity.cs')
if($LASTEXITCODE -ne 0) { throw 'Boot bridge compilation failed.' }
& $taskBridge --validate
if($LASTEXITCODE -ne 0) { throw 'Offline fixed-policy contracts failed.' }
# All service/runtime files remain byte-identical to the already-tested v1 release.
foreach($taskEntry in $taskBaseManifest.files.PSObject.Properties) {
    if($taskEntry.Name.StartsWith('Service/') -and (Get-FileHash -LiteralPath (Join-Path $taskPayload $taskEntry.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw 'Tested service runtime changed.' }
}
$taskInfo=[ordered]@{buildDirectory=$taskBuild;payloadDirectory=$taskPayload;basePackageSha256=$taskAudit.package_sha256;baseAuditSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'reports\service-policy-v1-audit-20261103T014001416Z.json') -Algorithm SHA256).Hash.ToLowerInvariant();serviceRuntimeUnchanged=$true;targetSipHash=$taskBaseManifest.target_sip_hash;targetSize=$taskBaseManifest.target_size}
[IO.File]::WriteAllText((Join-Path $taskRoot 'lab\build\boot-policy\last-build.json'),($taskInfo | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Boot policy candidate built: '+$taskBuild+'. No service, task or policy installation performed.')
