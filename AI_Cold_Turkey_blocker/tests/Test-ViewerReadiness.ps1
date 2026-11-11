#Requires -Version 5.1
[CmdletBinding()]param([string]$UpdaterPath='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $UpdaterPath){$UpdaterPath=Join-Path $PSScriptRoot '..\scripts\Update-Mask.ps1'}
$taskTokens=$null;$taskParseErrors=$null
$taskAst=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $UpdaterPath).Path,[ref]$taskTokens,[ref]$taskParseErrors)
if($taskParseErrors.Count){throw 'Updater parse failure'}
$taskNode=@($taskAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Test-UpdateViewerReady'},$true))
if($taskNode.Count -ne 1){throw 'Viewer readiness function missing'}
. ([scriptblock]::Create($taskNode[0].Extent.Text))
function New-Viewing([string]$Observation,[bool]$Applied,[int]$Pages=0,[int]$Required=0,[int]$Rendered=0,[bool]$Protected=$true,[string]$Fault='') {
    [pscustomobject]@{connected=$true;processProtected=$Protected;observation=$Observation;applied=$Applied;visiblePages=$Pages;requiredMasks=$Required;renderedMasks=$Rendered;fault=$Fault}
}
$taskCases=@(
    @{state=(New-Viewing 'OutsideClient' $true);expected=$true},
    @{state=(New-Viewing 'Confirmed' $true 1 1 1);expected=$true},
    @{state=(New-Viewing 'Confirmed' $false 1 1 0);expected=$false},
    @{state=(New-Viewing 'Unavailable' $false -Fault '内置网页暂未识别：committed_document_url_unavailable');expected=$true},
    @{state=(New-Viewing 'Unavailable' $false 1 1 0 -Fault '内置网页暂未识别：probe_timeout');expected=$false},
    @{state=(New-Viewing 'Unavailable' $false -Fault '遮挡窗口未确认，请导出诊断');expected=$false},
    @{state=(New-Viewing 'OutsideClient' $true -Protected $false);expected=$false}
)
foreach($taskCase in $taskCases){if((Test-UpdateViewerReady $taskCase.state) -ne $taskCase.expected){throw 'Viewer readiness mismatch'}}
Write-Output ($taskCases.Count.ToString()+' viewer readiness cases passed; no service or registry changes.')
