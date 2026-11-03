#Requires -Version 5.1
[CmdletBinding()]
param([switch]$SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Join-Path $taskRoot ('lab\build\desktop-browser\'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null=New-Item -ItemType Directory -Path $taskBuild -Force
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:Microsoft.CSharp.dll ('/out:'+(Join-Path $taskBuild '专注计划.exe')) (Join-Path $taskRoot 'lab\DesktopPortal.cs') (Join-Path $taskRoot 'src\Aict.Enforcement\BrowserPolicySlot.cs')
if($LASTEXITCODE -ne 0) { throw 'Desktop entry compilation failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskBase=Join-Path $taskRoot 'dist\AICT-interactive-lab-v1.zip'
if((Get-FileHash -LiteralPath $taskBase -Algorithm SHA256).Hash.ToLowerInvariant() -cne '62e74c2ab5f7cb31685450780653d5f73da95371d1edef8ab8ff6b7c58ef38e4') { throw 'Passed candidate source changed.' }
$taskZip=[IO.Compression.ZipFile]::OpenRead($taskBase)
try { $taskReader=New-Object IO.StreamReader($taskZip.GetEntry('MANIFEST.json').Open());try { $taskManifest=$taskReader.ReadToEnd() | ConvertFrom-Json }finally { $taskReader.Dispose() };$taskHashes=[ordered]@{};foreach($taskFile in $taskManifest.files.PSObject.Properties) { if($taskFile.Name.StartsWith('App/')) { $taskHashes.Add($taskFile.Name,$taskFile.Value) } };[IO.File]::WriteAllText((Join-Path $taskBuild 'BASE-APP-HASHES.json'),($taskHashes | ConvertTo-Json),(New-Object Text.UTF8Encoding($false))) }finally { $taskZip.Dispose() }
if($SelfTest) { $taskRender=Join-Path $taskBuild 'render';$taskChild=Start-Process -FilePath (Join-Path $taskBuild '专注计划.exe') -ArgumentList ('--self-test "'+$taskRender+'"') -WindowStyle Hidden -PassThru;if(-not $taskChild.WaitForExit(20000) -or $taskChild.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $taskRender 'browser.png'))) { throw 'Own-window render did not pass.' } }
[IO.File]::WriteAllText((Join-Path $taskRoot 'lab\build\desktop-browser\last-build.json'),(@{buildDirectory=$taskBuild} | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Desktop/browser candidate: '+$taskBuild+'. No shortcut, service, task or browser policy was installed on host.')
