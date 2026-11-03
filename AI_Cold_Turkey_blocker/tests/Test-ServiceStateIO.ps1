#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskAssembly=[Reflection.Assembly]::LoadFrom((Join-Path $taskRoot 'lab\build\service\AictServiceLab.exe'))
$taskType=$taskAssembly.GetType('AictServiceLab.Program',$true)
$taskFlags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$taskSave=$taskType.GetMethod('Save',$taskFlags);$taskRead=$taskType.GetMethod('Read',$taskFlags)
$taskDirectory=Join-Path $taskRoot ('lab\build\service\state-io-'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($taskDirectory)
$taskPath=Join-Path $taskDirectory 'state.json'
$taskValue=New-Object 'Collections.Generic.Dictionary[string,object]'
$taskValue.Add('Counter',0L);$taskValue.Add('Text',([string][char]0x72B6+[char]0x6001))
$null=$taskSave.Invoke($null,@($taskPath.PSObject.BaseObject,$taskValue.PSObject.BaseObject,$false))
# Keep an old snapshot handle open while replacing the named file.
$taskStream=New-Object IO.FileStream($taskPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$taskReader=New-Object IO.StreamReader($taskStream,(New-Object Text.UTF8Encoding($false)))
try {
    $taskValue['Counter']=1L
    $null=$taskSave.Invoke($null,@($taskPath.PSObject.BaseObject,$taskValue.PSObject.BaseObject,$true))
    $taskOld=$taskReader.ReadToEnd() | ConvertFrom-Json
    if ($taskOld.Counter -ne 0) { throw 'Old shared handle lost coherent snapshot' }
} finally { $taskReader.Dispose() }
$taskNew=$taskRead.Invoke($null,@($taskPath.PSObject.BaseObject))
if ($taskNew['Counter'] -ne 1 -or $taskNew['Text'] -cne $taskValue['Text']) { throw 'Atomic/UTF8 snapshot read failed' }
$taskOverwriteRejected=$false
try { $null=$taskSave.Invoke($null,@($taskPath.PSObject.BaseObject,$taskValue.PSObject.BaseObject,$false)) }
catch { if ($_.Exception.InnerException -is [IO.IOException]) { $taskOverwriteRejected=$true } else { throw } }
if (-not $taskOverwriteRejected -or @(Get-ChildItem -LiteralPath $taskDirectory -Filter '*.tmp').Count -ne 0) { throw 'CreateNew evidence/temporary cleanup invariant failed' }
Write-Output 'Three state IO cases passed: concurrent old/new snapshots, UTF8 roundtrip, no evidence overwrite/temp leak. Workspace files only; no SCM access.'
