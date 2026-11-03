#Requires -Version 5.1
[CmdletBinding()]
param([switch]$SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Join-Path $taskRoot ('lab\build\interactive\'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$taskPayload=Join-Path $taskBuild 'payload'
$null=New-Item -ItemType Directory -Path (Join-Path $taskPayload 'lab') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskBase=Join-Path $taskRoot 'dist\AICT-service-policy-lab-v1.zip'
$taskAudit=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'reports\service-policy-v1-audit-20261103T014001416Z.json') | ConvertFrom-Json
if((Get-FileHash -LiteralPath $taskBase -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskAudit.package_sha256) { throw 'Frozen fixture source does not match the passed audit.' }
$taskZip=[IO.Compression.ZipFile]::OpenRead($taskBase)
try { foreach($taskName in @('lab/FixtureIdentity.cs','lab/AuthenticodeHash.cs','lab/ServicePolicyNative.cs','lab/AictPolicyTarget.exe','lab/AictPolicyControl.exe')) {
    [IO.Compression.ZipFileExtensions]::ExtractToFile($taskZip.GetEntry($taskName),(Join-Path $taskPayload $taskName),$false)
} } finally { $taskZip.Dispose() }
$taskCompiler="$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe";$taskMi="$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
$taskBridge=Join-Path $taskPayload 'lab\AictInteractiveBridge.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:'+$taskMi) ('/out:'+$taskBridge) (Join-Path $taskRoot 'lab\InteractivePolicyBridge.cs') (Join-Path $taskPayload 'lab\ServicePolicyNative.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\AppLockerXml.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\InteractivePolicyXml.cs') (Join-Path $taskPayload 'lab\AuthenticodeHash.cs') (Join-Path $taskPayload 'lab\FixtureIdentity.cs')
if($LASTEXITCODE -ne 0) { throw 'Interactive native bridge compilation failed.' }
& $taskBridge --validate
if($LASTEXITCODE -ne 0) { throw 'Interactive ownership offline contract failed.' }
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll ('/out:'+(Join-Path $taskPayload 'lab\AictDemo.exe')) (Join-Path $taskRoot 'lab\InteractiveDemo.cs')
if($LASTEXITCODE -ne 0) { throw 'Inert window sample compilation failed.' }
$taskSdk=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot '.tools\dotnet-sdk.json') | ConvertFrom-Json
$taskValues=@{
    DOTNET_ROOT=$taskSdk.sdk_directory;DOTNET_CLI_HOME=(Join-Path $taskRoot '.tools\cli-home');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1';DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';MSBUILDDISABLENODEREUSE='1'
    NUGET_PACKAGES=(Join-Path $taskRoot '.tools\nuget');NUGET_HTTP_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-http');NUGET_PLUGINS_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-plugins');NUGET_SCRATCH=(Join-Path $taskRoot '.tools\nuget-scratch')
}
$taskSaved=@{};foreach($taskKey in $taskValues.Keys) { $taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process');[Environment]::SetEnvironmentVariable($taskKey,$taskValues[$taskKey],'Process') }
Push-Location -LiteralPath $taskRoot
try {
    $taskDotnet=Join-Path $taskSdk.sdk_directory 'dotnet.exe'
    foreach($taskProject in @(@{Name='Aict.Service';Folder='Service'},@{Name='Aict.Desktop';Folder='App'},@{Name='Aict.Backend';Folder='Backend'})) {
        & $taskDotnet publish (Join-Path $taskRoot ('src\'+$taskProject.Name+'\'+$taskProject.Name+'.csproj')) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $taskPayload $taskProject.Folder)
        if($LASTEXITCODE -ne 0) { throw ('Interactive build failed: '+$taskProject.Name) }
    }
    if($SelfTest) {
        $taskProcess=Start-Process -FilePath (Join-Path $taskPayload 'App\Aict.Desktop.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
        if(-not $taskProcess.WaitForExit(60000) -or $taskProcess.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $taskPayload 'ui-self-test\result.json'))) { throw 'Own-window rendering/editor smoke did not pass.' }
        & (Join-Path $taskRoot 'scripts\Stop-Preview.ps1') -BundleRoot $taskPayload -SelfTest
    }
} finally { Pop-Location;foreach($taskKey in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process') } }
[IO.File]::WriteAllText((Join-Path $taskRoot 'lab\build\interactive\last-build.json'),(@{buildDirectory=$taskBuild;payloadDirectory=$taskPayload;basePackageSha256=$taskAudit.package_sha256} | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Interactive VM candidate: '+$taskBuild+'. No service or system policy installed on the development host.')
