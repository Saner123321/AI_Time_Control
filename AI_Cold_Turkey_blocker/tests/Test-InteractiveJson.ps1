#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use inbox Windows PowerShell 5.1 so this test uses the bridge Framework runtime.' }
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'lab\build\interactive\last-build.json') | ConvertFrom-Json
Add-Type -AssemblyName System.Web.Extensions
$taskAssembly=[Reflection.Assembly]::LoadFile((Join-Path $taskBuild.payloadDirectory 'lab\AictInteractiveBridge.exe'))
$taskTarget=$taskAssembly.GetType('Aict.Enforcement.PolicyTarget',$true)
$taskSerializer=New-Object Web.Script.Serialization.JavaScriptSerializer
$taskJson='[{"id":"authenticode:sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","name":"sample","fileName":"Sample.exe","size":4096}]'
$taskApps=$taskSerializer.Deserialize($taskJson,$taskTarget.MakeArrayType())
if($taskApps.Length -ne 1) { throw 'Native JSON identity count mismatch.' }
$taskApps[0].Validate()
if($taskApps[0].Size -ne 4096 -or $taskApps[0].FileName -cne 'Sample.exe' -or $taskApps[0].Name -cne 'sample') { throw 'Native JSON identity casing mismatch.' }
Write-Output 'Framework serializer accepts the lowercase service-authored identity contract. No system policy read or write.'
