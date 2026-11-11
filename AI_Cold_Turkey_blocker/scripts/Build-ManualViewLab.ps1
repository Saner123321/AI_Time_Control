#Requires -Version 5.1
[CmdletBinding()]param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Join-Path $taskRoot ('lab\build\manual-view-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskOut=Join-Path $taskRoot 'dist\AICT-manual-view-lab-v3'
if(Test-Path -LiteralPath $taskOut){throw 'Frozen manual viewing lab exists.'}
$null=New-Item -ItemType Directory -Path $taskBuild
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ('/win32icon:'+(Join-Path $taskRoot 'assets\focus.ico')) ('/resource:'+(Join-Path $taskRoot 'assets\focus.ico')+',AICT.FocusIcon') ('/out:'+(Join-Path $taskBuild '内置浏览器验证-v3.exe')) (Join-Path $taskRoot 'lab\ManualBrowserProbe.cs') (Join-Path $taskRoot 'lab\ManualViewLab.cs') (Join-Path $taskRoot 'lab\ManualViewFixtureServer.cs') (Join-Path $taskRoot 'lab\ManualViewDialogs.cs') (Join-Path $taskRoot 'lab\ManualViewConfirmation.cs') (Join-Path $taskRoot 'lab\ManualViewMask.cs')
if($LASTEXITCODE -ne 0){throw 'Manual viewing lab compiler failed.'}
$taskSelfTest=Join-Path $taskBuild 'self-test'
$null=New-Item -ItemType Directory -Path $taskSelfTest
$taskChild=Start-Process -FilePath (Join-Path $taskBuild '内置浏览器验证-v3.exe') -ArgumentList ('--self-test "'+$taskSelfTest+'"') -WindowStyle Hidden -PassThru
if(-not $taskChild.WaitForExit(20000) -or $taskChild.ExitCode -ne 0){throw ('Manual viewing self-test failed; evidence: '+$taskSelfTest)}
$taskTest=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskSelfTest 'result.json')|ConvertFrom-Json
if(-not $taskTest.passed -or $taskTest.realChatgptProbeExecuted -or $taskTest.realAiScreenshotAndClickVerified){throw 'Manual viewing evidence scope mismatch.'}
$taskLayout=Join-Path $taskBuild 'layout.png'
$taskRender=Start-Process -FilePath (Join-Path $taskBuild '内置浏览器验证-v3.exe') -ArgumentList ('--render "'+$taskLayout+'"') -WindowStyle Hidden -PassThru
if(-not $taskRender.WaitForExit(10000) -or $taskRender.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $taskLayout)){throw 'Offscreen layout render failed.'}
$null=New-Item -ItemType Directory -Path $taskOut
Copy-Item -LiteralPath (Join-Path $taskBuild '内置浏览器验证-v3.exe') -Destination $taskOut
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\MANUAL_VIEWING_LAB_V3.md') -Destination (Join-Path $taskOut 'README.md')
$null=New-Item -ItemType Directory -Path (Join-Path $taskOut 'Sources')
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\ManualBrowserProbe.cs'),(Join-Path $taskRoot 'lab\ManualViewLab.cs'),(Join-Path $taskRoot 'lab\ManualViewFixtureServer.cs'),(Join-Path $taskRoot 'lab\ManualViewDialogs.cs'),(Join-Path $taskRoot 'lab\ManualViewConfirmation.cs'),(Join-Path $taskRoot 'lab\ManualViewMask.cs') -Destination (Join-Path $taskOut 'Sources')
$taskSums=@(Get-ChildItem -LiteralPath $taskOut -Recurse -File|Sort-Object FullName|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+$_.FullName.Substring($taskOut.Length+1).Replace('\','/')})
[IO.File]::WriteAllText((Join-Path $taskOut 'SHA256SUMS.txt'),(($taskSums -join "`n")+"`n"),(New-Object Text.UTF8Encoding($false)))
$taskTest|Add-Member -NotePropertyName package -NotePropertyValue $taskOut
$taskTest|Add-Member -NotePropertyName build -NotePropertyValue $taskBuild
[IO.File]::WriteAllText((Join-Path $taskRoot 'reports\manual-view-lab-v3-local-checks.json'),($taskTest|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Manual viewing lab ready: '+$taskOut+'. No installed rules, browser settings, service or current user windows were changed.')
