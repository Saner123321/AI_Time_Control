#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# ASCII-only source: the fixture deliberately contains real non-ASCII UTF-8 bytes.
$taskMessage = -join (@(0x66F4,0x5177,0x4F53,0x7684,0x9519,0x8BEF,0x4EE3,0x7801,0x4E0D,0x5305,0x542B,0x53D1,0x751F,0x7684,0x5E38,0x89C4,0x9519,0x8BEF,0x3002) | ForEach-Object { [char]$_ })
$taskFixture = [ordered]@{ Error = @{ Message = $taskMessage }; OwnInstancesAbsent = $true; LegacyInstancesAbsent = $true; AuditCreateReturned = $false }
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) ('aict-utf8-test-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($taskRoot)
$taskPath = Join-Path $taskRoot 'result.json'
[IO.File]::WriteAllText($taskPath, ($taskFixture | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
$taskResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskPath | ConvertFrom-Json
if ($taskResult.Error.Message -cne $taskMessage -or -not $taskResult.OwnInstancesAbsent -or -not $taskResult.LegacyInstancesAbsent -or $taskResult.AuditCreateReturned) { throw 'UTF-8 result decoding regression' }
Write-Output '{"kind":"UTF8_JSON_ROUNDTRIP_WITH_CHINESE","passed":true,"policy_changes_made":false}'
