#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSdk=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot '.tools\dotnet-sdk.json') | ConvertFrom-Json
$taskDotnet=Join-Path $taskSdk.sdk_directory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Initialize the project-local SDK first.' }
$taskValues=@{
    DOTNET_ROOT=$taskSdk.sdk_directory
    DOTNET_CLI_HOME=(Join-Path $taskRoot '.tools\cli-home')
    DOTNET_CLI_TELEMETRY_OPTOUT='1'
    DOTNET_NOLOGO='1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
    MSBUILDDISABLENODEREUSE='1'
    NUGET_PACKAGES=(Join-Path $taskRoot '.tools\nuget')
    NUGET_HTTP_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-http')
    NUGET_PLUGINS_CACHE_PATH=(Join-Path $taskRoot '.tools\nuget-plugins')
    NUGET_SCRATCH=(Join-Path $taskRoot '.tools\nuget-scratch')
    AICT_TEST_ROOT=(Join-Path $taskRoot 'artifacts\core-test-data')
}
$taskSaved=@{}
foreach ($taskKey in $taskValues.Keys) { $taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process');[Environment]::SetEnvironmentVariable($taskKey,$taskValues[$taskKey],'Process') }
Push-Location -LiteralPath $taskRoot
try {
    $taskOutput=Join-Path $taskRoot ('artifacts\core-tests-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    & $taskDotnet test (Join-Path $taskRoot 'tests\Aict.Core.Tests\Aict.Core.Tests.csproj') --configuration Release --results-directory $taskOutput --logger 'trx;LogFileName=core.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed; preserve the test results.' }
    $taskPreview=Join-Path $taskRoot ('artifacts\core-preview-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    & $taskDotnet run --project (Join-Path $taskRoot 'samples\Aict.RulePreview\Aict.RulePreview.csproj') --configuration Release -- $taskPreview
    if ($LASTEXITCODE -ne 0) { throw 'Core simulation failed; no system enforcement attempted.' }
    Write-Host ('Core test evidence: '+$taskOutput)
} finally {
    Pop-Location
    foreach ($taskKey in $taskSaved.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process') }
}
