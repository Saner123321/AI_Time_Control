#Requires -Version 5.1
# Pure XML parsing: no event-log access, policy changes or file writes.
function ConvertFrom-AppLockerLabEventXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Xml,
        [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId
    )
    $taskSettings=New-Object Xml.XmlReaderSettings
    $taskSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $taskSettings.XmlResolver=$null
    $taskSettings.MaxCharactersInDocument=1048576
    $taskText=New-Object IO.StringReader($Xml)
    $taskReader=[Xml.XmlReader]::Create($taskText,$taskSettings)
    try {
        $taskDocument=New-Object Xml.XmlDocument
        $taskDocument.XmlResolver=$null
        $taskDocument.Load($taskReader)
    } finally { $taskReader.Dispose(); $taskText.Dispose() }
    # AppLocker emits both EventData and provider-specific UserData. Some events have neither.
    $taskData=[ordered]@{}
    foreach ($taskNode in $taskDocument.SelectNodes("/*[local-name()='Event']/*[local-name()='EventData']/*[local-name()='Data'] | /*[local-name()='Event']/*[local-name()='UserData']//*[not(*)]")) {
        $taskName=if ($taskNode.LocalName -eq 'Data') { $taskNode.GetAttribute('Name') } else { $taskNode.LocalName }
        if ($taskName -in @('FilePath','RuleId','RuleName','PolicyName','FileHash','Fqbn','TargetProcessId')) { $taskData[$taskName]=$taskNode.InnerText }
    }
    if (-not $taskData.Contains('FilePath')) { return }
    # Match a complete run-directory component, not a substring in an unrelated path.
    $taskPattern='(?i)(?:^|[\\/])AICT-(?:Enforce|Identity)-' + [regex]::Escape($RunId) + '[\\/]'
    if ($taskData.FilePath -notmatch $taskPattern) { return }
    return $taskData
}
