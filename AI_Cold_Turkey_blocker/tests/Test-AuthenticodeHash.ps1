#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskBuild=Join-Path (Split-Path -Parent $PSScriptRoot) 'lab\build\identity-latency'
$taskTool=Join-Path $taskBuild 'AictAuthenticodeHashCheck.exe'
$taskTarget=Join-Path $taskBuild 'AictIdentityTarget.exe'
$taskControl=Join-Path $taskBuild 'AictIdentityControl.exe'
$taskOriginal=(Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash
$taskJson=& $taskTool $taskTarget $taskControl
if ($LASTEXITCODE -ne 0) { throw 'SIP baseline failed' }
$taskBaseline=$taskJson | ConvertFrom-Json
$taskTestDirectory=Join-Path $taskBuild ('readonly-sip-' + [guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $taskTestDirectory
$taskCopy=Join-Path $taskTestDirectory ('renamed & ' + [char]0x6D4B + [char]0x8BD5 + '.exe')
Copy-Item -LiteralPath $taskTarget -Destination $taskCopy
$taskJson=& $taskTool $taskCopy $taskControl
if ($LASTEXITCODE -ne 0) { throw 'SIP unicode/rename check failed' }
$taskRenamed=$taskJson | ConvertFrom-Json
if ($taskRenamed.TargetSipHash -cne $taskBaseline.TargetSipHash -or $taskRenamed.TargetSha256 -cne $taskBaseline.TargetSha256) { throw 'Renaming changed file identity' }
# Mutate ONLY a derived sample's PE checksum: Authenticode excludes this field.
$taskBytes=[IO.File]::ReadAllBytes($taskCopy)
$taskPeOffset=[BitConverter]::ToInt32($taskBytes,0x3C)
$taskChecksumOffset=$taskPeOffset+24+64
$taskBytes[$taskChecksumOffset]=$taskBytes[$taskChecksumOffset] -bxor 0x55
[IO.File]::WriteAllBytes($taskCopy,$taskBytes)
$taskJson=& $taskTool $taskCopy $taskControl
if ($LASTEXITCODE -ne 0) { throw 'SIP checksum exclusion check failed' }
$taskChecksum=$taskJson | ConvertFrom-Json
if ($taskChecksum.TargetSipHash -cne $taskBaseline.TargetSipHash -or $taskChecksum.TargetSha256 -ceq $taskBaseline.TargetSha256) { throw 'SIP hash was confused with distribution SHA256' }
if ($taskBaseline.TargetSipHash -ceq $taskBaseline.ControlSipHash) { throw 'Different-content control shares SIP identity' }
if ((Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash -cne $taskOriginal) { throw 'Read-only SIP calculation changed original fixture' }
Write-Output 'Read-only SIP checks passed: Unicode/rename/copy stable, checksum excluded, different content distinct, original unchanged. No AppLocker access.'
