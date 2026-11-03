#Requires -Version 5.1
[CmdletBinding()]
param([switch]$SelfTest,[switch]$HostTrial,[switch]$SourceFixtures)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskFamily=if($HostTrial){'host-trial'}else{'beta'}
$taskBuild=Join-Path $taskRoot ('lab\build\'+$taskFamily+'\'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$taskPayload=Join-Path $taskBuild 'payload'
$null=New-Item -ItemType Directory -Path (Join-Path $taskPayload 'Targets') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskBase=Join-Path $taskRoot 'dist\AICT-interactive-lab-v1.zip'
$taskBaseHash='62e74c2ab5f7cb31685450780653d5f73da95371d1edef8ab8ff6b7c58ef38e4'
if($SourceFixtures -or -not(Test-Path -LiteralPath $taskBase)){$taskBase=Join-Path $taskRoot 'build-fixtures\inert-apps-v1.zip';$taskBaseHash='5eea58c9b6c67c1e8301c9cab8c92e69218da6d1c64f3e576619cbd8f3142ea1'}
if((Get-FileHash -LiteralPath $taskBase -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskBaseHash) { throw 'Frozen fixture source changed.' }
$taskZip=[IO.Compression.ZipFile]::OpenRead($taskBase)
try { foreach($taskPair in @(@('lab/AictPolicyTarget.exe','Targets/Target.exe'),@('lab/AictPolicyTarget.exe','Targets/Renamed.exe'),@('lab/AictPolicyTarget.exe','MovedTarget.exe'),@('lab/AictPolicyControl.exe','Targets/AllowedControl.exe'),@('lab/AictDemo.exe','Targets/AictDemo.exe'),@('lab/FixtureIdentity.cs','FixtureIdentity.cs'),@('lab/AuthenticodeHash.cs','AuthenticodeHash.cs'),@('lab/ServicePolicyNative.cs','ServicePolicyNative.cs'))) { [IO.Compression.ZipFileExtensions]::ExtractToFile($taskZip.GetEntry($taskPair[0]),(Join-Path $taskPayload $taskPair[1]),$false) } }finally { $taskZip.Dispose() }
$taskMi="$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
$taskNativeDefines=@();if($HostTrial){$taskNativeDefines+=('/define:HOST_TRIAL')}
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" @taskNativeDefines /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:'+$taskMi) ('/out:'+(Join-Path $taskPayload 'BetaBridge.exe')) (Join-Path $taskRoot 'lab\BetaBridge.cs') (Join-Path $taskRoot 'lab\BetaPolicyXml.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\AppLockerXml.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\InteractivePolicyXml.cs') (Join-Path $taskPayload 'AuthenticodeHash.cs') (Join-Path $taskPayload 'FixtureIdentity.cs') (Join-Path $taskRoot 'lab\BetaNative.cs')
if($LASTEXITCODE -ne 0) { throw 'Beta native compiler failed.' }
& (Join-Path $taskPayload 'BetaBridge.exe') --validate
if($LASTEXITCODE -ne 0) { throw 'Beta XML ownership failed.' }
& (Join-Path $taskRoot 'tests\Test-BetaSetupLayout.ps1') -Bridge (Join-Path $taskPayload 'BetaBridge.exe') -Payload $taskPayload
$taskSdk=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot '.tools\dotnet-sdk.json') | ConvertFrom-Json
$taskValues=@{DOTNET_ROOT=$taskSdk.sdk_directory;DOTNET_CLI_HOME=(Join-Path $taskRoot '.tools\cli-home');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1';DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';MSBUILDDISABLENODEREUSE='1';NUGET_PACKAGES=(Join-Path $taskRoot '.tools\nuget');NUGET_HTTP_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-http');NUGET_PLUGINS_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-plugins');NUGET_SCRATCH=(Join-Path $taskRoot '.tools\nuget-scratch')}
$taskSaved=@{};foreach($taskKey in $taskValues.Keys){$taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process');[Environment]::SetEnvironmentVariable($taskKey,$taskValues[$taskKey],'Process')}
Push-Location -LiteralPath $taskRoot
try {
    $taskPublishDefines=@();if($HostTrial){$taskPublishDefines+=('-p:DefineConstants=HOST_TRIAL')}
    foreach($taskProject in @(@{Name='Aict.Beta.Service';Folder='Service'},@{Name='Aict.Beta.Agent';Folder='Agent'},@{Name='Aict.Beta.Desktop';Folder='App'})) { & (Join-Path $taskSdk.sdk_directory 'dotnet.exe') publish @taskPublishDefines (Join-Path $taskRoot ('src\'+$taskProject.Name+'\'+$taskProject.Name+'.csproj')) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $taskPayload $taskProject.Folder);if($LASTEXITCODE -ne 0){throw ('Beta publish failed: '+$taskProject.Name)} }
    foreach($taskName in @('专注计划.exe','管理员恢复工具.exe')) { & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll ('/out:'+(Join-Path $taskPayload $taskName)) (Join-Path $taskRoot 'lab\BetaEntry.cs');if($LASTEXITCODE -ne 0){throw 'Entry build failed.'} }
    $null=New-Item -ItemType Directory -Path (Join-Path $taskPayload 'scripts') -Force
    $taskInstallText=[IO.File]::ReadAllText((Join-Path $taskRoot 'scripts\Install-Beta.ps1'))
    if($HostTrial){$taskInstallText=$taskInstallText.Replace('$taskHostBuild=$false','$taskHostBuild=$true')}
    [IO.File]::WriteAllText((Join-Path $taskPayload 'scripts\Install-Beta.ps1'),$taskInstallText,(New-Object Text.UTF8Encoding($true)))
    Copy-Item -LiteralPath (Join-Path $taskRoot 'browser-extension\beta') -Destination (Join-Path $taskPayload 'Extension') -Recurse
    if($SelfTest){$taskUi=Start-Process -FilePath (Join-Path $taskPayload 'App\Aict.Beta.Desktop.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru;if(-not $taskUi.WaitForExit(60000) -or $taskUi.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $taskPayload 'ui-self-test\result.json'))){throw 'Integrated UI self-test failed.'}}
}finally {Pop-Location;foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process')}}
[IO.File]::WriteAllText((Join-Path $taskRoot ('lab\build\'+$taskFamily+'\last-build.json')),(@{buildDirectory=$taskBuild;payloadDirectory=$taskPayload}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Integrated beta candidate: '+$taskPayload+'. No host installation performed.')
