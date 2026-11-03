#Requires -Version 5.1
[CmdletBinding()]
param([string]$Payload)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use inbox Windows PowerShell 5.1.' }
$taskRoot=Split-Path -Parent $PSScriptRoot
if(-not $Payload) { $Payload=(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'lab\build\beta\last-build.json') | ConvertFrom-Json).payloadDirectory }
$taskAssembly=[Reflection.Assembly]::LoadFile((Join-Path $Payload 'BetaBridge.exe'))
$taskType=$taskAssembly.GetType('AictServicePolicyLab.BetaPolicyXml',$true);$taskFlags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$taskApps=@();for($taskN=1;$taskN -le 64;$taskN++) { $taskApps+=@{id=('authenticode:sha256:'+$taskN.ToString('X64'));name='app';fileName=('App'+$taskN+'.exe');size=3072} }
$taskPlan=[ordered]@{denied=$taskApps;whitelists=@(@{id='12345678123412341234123456789abc';applications=$taskApps},@{id='22345678123412341234123456789abc';applications=$taskApps})}
$taskRaw=$taskPlan | ConvertTo-Json -Depth 15 -Compress
$taskRun='12345678123412341234123456789abc';$taskSid='S-1-5-21-1-2-3-1001';$taskInstall='C:\ProgramData\AICT-Beta-'+$taskRun
$taskXml=[string]$taskType.GetMethod('Exe',$taskFlags).Invoke($null,[object[]]@([string]$taskRun,[string]$taskSid,[string]$taskInstall,[string]$taskRaw))
$taskReadArgs=New-Object object[] 4;$taskReadArgs[0]=$taskXml;$taskReadArgs[1]=$taskRun;$taskReadArgs[2]=$taskInstall;$taskReadArgs[3]=''
$taskRead=[string]$taskType.GetMethod('ReadOwned',$taskFlags).Invoke($null,$taskReadArgs)
if($taskRead -cne $taskRaw -or $taskReadArgs[3] -cne $taskSid) { throw 'Large allowlist XML ownership roundtrip failed.' }
$taskDoc=New-Object Xml.XmlDocument;$taskDoc.LoadXml($taskXml)
foreach($taskRule in $taskDoc.DocumentElement.ChildNodes) { if($taskRule.GetAttribute('Description').Length -gt 1024) { throw 'Description exceeds Windows schema.' } }
$taskSchemas=New-Object Xml.Schema.XmlSchemaSet;$null=$taskSchemas.Add($null,(Join-Path $taskRoot 'artifacts\official-applocker-schema\policy.xsd'))
$taskPolicy=New-Object Xml.XmlDocument;$taskPolicy.LoadXml('<AppLockerPolicy Version="1">'+$taskXml+'</AppLockerPolicy>');$taskPolicy.Schemas=$taskSchemas
$taskPolicy.Validate($null)
$taskBad=$taskXml.Replace('AICT application allowlist','Foreign allowlist')
$taskReadArgs[0]=$taskBad;$taskRejected=$false
try { $null=$taskType.GetMethod('ReadOwned',$taskFlags).Invoke($null,$taskReadArgs) } catch { $taskRejected=$true }
if(-not $taskRejected) { throw 'Foreign rule shape accepted.' }
Write-Output ('Large policy '+$taskXml.Length+' characters; official Microsoft XSD valid; schema-length bounds and foreign-change refusal passed. No policy execution.')
