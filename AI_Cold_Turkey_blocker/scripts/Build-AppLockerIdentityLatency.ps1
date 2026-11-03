#Requires -Version 5.1
[CmdletBinding()]
param([switch]$HashOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskCompiler="$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskMi="$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
if (-not (Test-Path -LiteralPath $taskCompiler) -or -not (Test-Path -LiteralPath $taskMi)) { throw 'Required inbox compiler/MI assembly missing. No installation attempted.' }
$taskBuild=Join-Path $taskRoot 'lab\build\identity-latency'
$null=New-Item -ItemType Directory -Path $taskBuild -Force
$taskTarget=Join-Path $taskBuild 'AictIdentityTarget.exe'
$taskControl=Join-Path $taskBuild 'AictIdentityControl.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ ('/out:'+$taskTarget) (Join-Path $taskRoot 'lab\AppLockerIdentityFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Target fixture compilation failed' }
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /define:CONTROL ('/out:'+$taskControl) (Join-Path $taskRoot 'lab\AppLockerIdentityFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Control fixture compilation failed' }
$taskTargetMarker=& $taskTarget
if ($LASTEXITCODE -ne 0 -or $taskTargetMarker -cne 'AICT_FIXTURE_OK') { throw 'Target marker mismatch' }
$taskControlMarker=& $taskControl
if ($LASTEXITCODE -ne 0 -or $taskControlMarker -cne 'AICT_CONTROL_OK') { throw 'Control marker mismatch' }
$taskHasher=Join-Path $taskBuild 'AictAuthenticodeHashCheck.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Web.Extensions.dll ('/out:'+$taskHasher) (Join-Path $taskRoot 'lab\AuthenticodeHash.cs') (Join-Path $taskRoot 'lab\AuthenticodeHashCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Read-only SIP checker compilation failed' }
$taskHashJson=& $taskHasher $taskTarget $taskControl
if ($LASTEXITCODE -ne 0) { throw 'Read-only SIP identity check failed' }
$taskHash=$taskHashJson | ConvertFrom-Json
$taskContract='namespace AictEnforceLab { static class FixtureIdentity {' +
    'internal const string TargetSipHash="'+$taskHash.TargetSipHash+'";' +
    'internal const string TargetSha256="'+$taskHash.TargetSha256+'";' +
    'internal const string ControlSha256="'+$taskHash.ControlSha256+'";' +
    'internal const long TargetSize='+$taskHash.TargetSize+'L; } }'
$taskContractPath=Join-Path $taskBuild 'IdentityFixtureContract.cs'
[IO.File]::WriteAllText($taskContractPath,$taskContract,(New-Object Text.UTF8Encoding($false)))
Write-Output $taskHashJson
if ($HashOnly) { return }
$taskBinary=Join-Path $taskBuild 'AictAppLockerIdentityLatency.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:'+$taskMi) ('/out:'+$taskBinary) (Join-Path $taskRoot 'lab\AppLockerIdentityLatencySmoke.cs') (Join-Path $taskRoot 'lab\AuthenticodeHash.cs') $taskContractPath
if ($LASTEXITCODE -ne 0) { throw 'Identity/timing worker compilation failed' }
& $taskBinary --validate
if ($LASTEXITCODE -ne 0) { throw 'Identity/timing offline contracts failed' }
Write-Host 'Built laboratory only; no AppLocker policy access or task registration.'
