#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Join-Path $taskRoot 'lab\build\service-policy'
$null=New-Item -ItemType Directory -Path $taskBuild -Force
$taskCompiler="$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskMi="$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
if(-not(Test-Path -LiteralPath $taskCompiler) -or -not(Test-Path -LiteralPath $taskMi)) { throw 'Inbox Framework compiler/MI required; no global installation attempted.' }
# Reuse the same independently built inert fixture and read-only SIP hasher; old frozen packages are not changed.
& (Join-Path $PSScriptRoot 'Build-AppLockerIdentityLatency.ps1') -HashOnly
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\build\identity-latency\AictIdentityTarget.exe') -Destination (Join-Path $taskBuild 'AictPolicyTarget.exe')
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\build\identity-latency\AictIdentityControl.exe') -Destination (Join-Path $taskBuild 'AictPolicyControl.exe')
$taskContract=Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'lab\build\identity-latency\IdentityFixtureContract.cs')
$taskContract=$taskContract.Replace('namespace AictEnforceLab','namespace AictServicePolicyLab')
[IO.File]::WriteAllText((Join-Path $taskBuild 'FixtureIdentity.cs'),$taskContract,(New-Object Text.UTF8Encoding($false)))
$taskHashSource=(Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'lab\AuthenticodeHash.cs')).Replace('namespace AictEnforceLab','namespace AictServicePolicyLab')
[IO.File]::WriteAllText((Join-Path $taskBuild 'AuthenticodeHash.cs'),$taskHashSource,(New-Object Text.UTF8Encoding($false)))
$taskBridge=Join-Path $taskBuild 'AictPolicyBridge.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:'+$taskMi) ('/out:'+$taskBridge) (Join-Path $taskRoot 'lab\ServicePolicyBridge.cs') (Join-Path $taskRoot 'lab\ServicePolicyNative.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\AppLockerXml.cs') (Join-Path $taskBuild 'AuthenticodeHash.cs') (Join-Path $taskBuild 'FixtureIdentity.cs')
if($LASTEXITCODE -ne 0) { throw 'Fixed native MI/SCM bridge build failed.' }
& $taskBridge --validate
if($LASTEXITCODE -ne 0) { throw 'Offline policy contracts failed.' }
$taskSdk=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot '.tools\dotnet-sdk.json') | ConvertFrom-Json
$taskValues=@{
    DOTNET_ROOT=$taskSdk.sdk_directory;DOTNET_CLI_HOME=(Join-Path $taskRoot '.tools\cli-home');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';MSBUILDDISABLENODEREUSE='1'
    NUGET_PACKAGES=(Join-Path $taskRoot '.tools\nuget');NUGET_HTTP_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-http')
    NUGET_PLUGINS_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-plugins');NUGET_SCRATCH=(Join-Path $taskRoot '.tools\nuget-scratch')
}
$taskSaved=@{}
foreach($taskKey in $taskValues.Keys) { $taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process');[Environment]::SetEnvironmentVariable($taskKey,$taskValues[$taskKey],'Process') }
Push-Location -LiteralPath $taskRoot
try {
    $taskDotnet=Join-Path $taskSdk.sdk_directory 'dotnet.exe'
    & $taskDotnet publish (Join-Path $taskRoot 'src\Aict.Service\Aict.Service.csproj') --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $taskBuild 'Service')
    if($LASTEXITCODE -ne 0) { throw 'Service candidate build failed.' }
    & (Join-Path $taskBuild 'Service\Aict.Service.exe') --offline-check
    if($LASTEXITCODE -ne 0) { throw 'Offline service pipe DACL check failed.' }
} finally {
    Pop-Location
    foreach($taskKey in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process') }
}
Write-Host ('Built VM-only integration candidate: '+$taskBuild+'. No service/policy installation performed.')
