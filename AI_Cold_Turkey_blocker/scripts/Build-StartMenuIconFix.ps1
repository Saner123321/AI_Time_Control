#Requires -Version 5.1
[CmdletBinding()] param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Join-Path $taskRoot ('artifacts\start-menu-icon-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskOut=Join-Path $taskRoot 'dist\AICT-start-menu-icon-v1'
if(Test-Path -LiteralPath $taskOut){throw 'Frozen icon package already exists.'}
$null=New-Item -ItemType Directory -Path $taskBuild
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll ('/win32icon:'+(Join-Path $taskRoot 'assets\focus.ico')) ('/resource:'+(Join-Path $taskRoot 'assets\focus.ico')+',AICT.FocusIcon') ('/out:'+(Join-Path $taskBuild '添加开始菜单图标.exe')) (Join-Path $taskRoot 'lab\StartMenuIconFix.cs')
if($LASTEXITCODE -ne 0){throw 'Icon tool build failed'}
$taskProcess=Start-Process -FilePath (Join-Path $taskBuild '添加开始菜单图标.exe') -ArgumentList ('--self-test "'+(Join-Path $taskBuild 'self-test')+'"') -WindowStyle Hidden -PassThru
if(-not $taskProcess.WaitForExit(20000) -or $taskProcess.ExitCode -ne 0){throw 'Icon tool self-test failed'}
$taskResult=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskBuild 'self-test\result.json')|ConvertFrom-Json
if(-not $taskResult.passed -or $taskResult.realStartMenuOrDesktopChanges){throw 'Icon fixture check failed'}
$null=New-Item -ItemType Directory -Path $taskOut
Copy-Item -LiteralPath (Join-Path $taskBuild '添加开始菜单图标.exe') -Destination $taskOut
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\START_MENU_ICON.md') -Destination (Join-Path $taskOut 'README.md')
$null=New-Item -ItemType Directory -Path (Join-Path $taskOut 'Sources')
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\StartMenuIconFix.cs'),(Join-Path $taskRoot 'assets\focus.ico') -Destination (Join-Path $taskOut 'Sources')
$taskSums=@(Get-ChildItem -LiteralPath $taskOut -Recurse -File|Sort-Object FullName|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+$_.FullName.Substring($taskOut.Length+1).Replace('\','/')})
[IO.File]::WriteAllText((Join-Path $taskOut 'SHA256SUMS.txt'),(($taskSums -join "`n")+"`n"),(New-Object Text.UTF8Encoding($false)))
$taskResult|Add-Member -NotePropertyName build -NotePropertyValue $taskBuild
$taskResult|Add-Member -NotePropertyName package -NotePropertyValue $taskOut
[IO.File]::WriteAllText((Join-Path $taskRoot 'reports\start-menu-icon-v1-local-checks.json'),($taskResult|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Start menu icon tool built: '+$taskOut+'. No real shortcuts changed.')
