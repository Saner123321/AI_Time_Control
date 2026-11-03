#Requires -Version 5.1
[CmdletBinding()]
param([switch]$SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSdk=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot '.tools\dotnet-sdk.json') | ConvertFrom-Json
$taskDotnet=Join-Path $taskSdk.sdk_directory 'dotnet.exe'
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
    $taskBundle=Join-Path $taskRoot ('artifacts\desktop-preview-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    foreach($taskProject in @(@{Name='Aict.Backend';Folder='Backend'},@{Name='Aict.Desktop';Folder='App'})) {
        & $taskDotnet publish (Join-Path $taskRoot ('src\'+$taskProject.Name+'\'+$taskProject.Name+'.csproj')) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $taskBundle $taskProject.Folder) -p:NuGetAudit=true
        if($LASTEXITCODE -ne 0) { throw ('Preview build failed: '+$taskProject.Name) }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stop-Preview.ps1') -Destination (Join-Path $taskBundle 'Stop-Preview.ps1')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\DESKTOP_PREVIEW.md') -Destination (Join-Path $taskBundle 'README.md')
    if($SelfTest) {
        & $taskDotnet run --project (Join-Path $taskRoot 'samples\Aict.PipeSmoke\Aict.PipeSmoke.csproj') --configuration Release -- (Join-Path $taskBundle 'Backend\Aict.Backend.exe') (Join-Path $taskBundle 'pipe-smoke')
        if($LASTEXITCODE -ne 0) { throw 'Named-pipe integration smoke failed.' }
        $taskProcess=Start-Process -FilePath (Join-Path $taskBundle 'App\Aict.Desktop.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
        if(-not $taskProcess.WaitForExit(60000)) { throw 'Desktop self-test timed out. Preserve its data and inspect the process.' }
        if($taskProcess.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $taskBundle 'ui-self-test\result.json'))) { throw 'Desktop self-test did not pass.' }
        $taskReceipt=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskBundle 'ui-self-test-data\backend-process.json') | ConvertFrom-Json
        if($null -eq (Get-Process -Id $taskReceipt.id -ErrorAction SilentlyContinue)) { throw 'Backend did not survive UI exit.' }
        & (Join-Path $taskBundle 'Stop-Preview.ps1') -SelfTest
    }
    Write-Host ('Development preview (NO system enforcement): '+$taskBundle)
} finally {
    Pop-Location
    foreach($taskKey in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process') }
}
