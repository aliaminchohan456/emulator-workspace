# EV-02: dumps the names UI Automation reads for the Agent Sessions tables.
# For every List control in the window it prints the list's own name and then
# each ListItem's Name, which is what a screen reader announces. A row whose
# container carries no name falls back to its item type
# ("AgentSessionsPage+PairingRow"), so this is the exact check EV-02 needs.
# Usage: powershell -NoProfile -File tools/ev02-uia-rows.ps1 -Title "Emulator Workspace" -Out artifacts/ev02/uia-rows.txt
param(
    [Parameter(Mandatory = $false)][string]$Title = "Emulator Workspace",
    [long]$Handle = 0,
    [Parameter(Mandatory = $true)][string]$Out
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$window = $null
if ($Handle -ne 0) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
}
if (-not $window) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}
if (-not $window) {
    Write-Error "Window '$Title' not found."
    exit 1
}

$listType = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::List)
$itemType = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add(("EV-02 UIA row names - window '{0}' at {1}" -f $Title, (Get-Date).ToString("o")))
$lines.Add("")

$lists = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listType)
$rowCount = 0
foreach ($list in $lists) {
    if ($list.Current.IsOffscreen) { continue }
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $itemType)
    if ($items.Count -eq 0) { continue }

    $lines.Add(("List '{0}' ({1} rows)" -f $list.Current.Name, $items.Count))
    foreach ($item in $items) {
        $rowCount++
        $lines.Add(("  [{0}] {1}" -f $item.Current.AutomationId, $item.Current.Name))
    }

    $lines.Add("")
}

$lines.Add(("lists={0} rows={1}" -f $lists.Count, $rowCount))

$text = $lines -join [Environment]::NewLine
$directory = Split-Path -Parent $Out
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
Set-Content -Path $Out -Value $text -Encoding utf8
Write-Output $text
