#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Read-AppLockerLabEvent.ps1')
$taskRunId='a'*32
$taskPath='C:\ProgramData\AICT-Enforce-' + $taskRunId + '\Targets\Target.exe'
$taskEvent='<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><EventData><Data Name="FilePath">' + $taskPath + '</Data><Data Name="RuleId">fixture</Data><Data Name="UserSid">PRIVATE</Data></EventData></Event>'
$taskResult=ConvertFrom-AppLockerLabEventXml -Xml $taskEvent -RunId $taskRunId
if ($taskResult.FilePath -cne $taskPath -or $taskResult.RuleId -cne 'fixture' -or $taskResult.Contains('UserSid')) { throw 'EventData projection/privacy failed' }
$taskUserEvent='<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><UserData><RuleAndFileData xmlns="urn:fixture"><FilePath>' + $taskPath + '</FilePath><RuleName>test</RuleName></RuleAndFileData></UserData></Event>'
$taskResult=ConvertFrom-AppLockerLabEventXml -Xml $taskUserEvent -RunId $taskRunId
if ($taskResult.FilePath -cne $taskPath -or $taskResult.RuleName -cne 'test') { throw 'UserData projection failed' }
foreach ($taskEmpty in @('<Event><System><EventID>8001</EventID></System></Event>','<Event><EventData/></Event>','<Event><UserData><PolicyApplied/></UserData></Event>')) {
    if ($null -ne (ConvertFrom-AppLockerLabEventXml -Xml $taskEmpty -RunId $taskRunId)) { throw 'No-file event was exported' }
}
foreach ($taskWrong in @($taskEvent.Replace($taskRunId,('b'*32)),$taskEvent.Replace($taskRunId,($taskRunId+'extra')))) {
    if ($null -ne (ConvertFrom-AppLockerLabEventXml -Xml $taskWrong -RunId $taskRunId)) { throw 'Unrelated path was exported' }
}
$taskDtdRejected=$false
try { ConvertFrom-AppLockerLabEventXml -Xml ('<!DOCTYPE Event [<!ENTITY x "x">]>'+$taskEvent) -RunId $taskRunId | Out-Null }
catch { $taskDtdRejected=$true }
if (-not $taskDtdRejected) { throw 'DTD accepted' }
Write-Output 'Eight event XML cases passed in WinPS 5.1; no log or policy access.'
