# Closes a top-level window politely via UIA WindowPattern (by exact title).
# Usage: powershell -NoProfile -File tools/uia-close.ps1 -Title "Agent Sessions"
param([Parameter(Mandatory = $true)][string]$Title)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
if (-not $window) {
    Write-Error "Window '$Title' not found."
    exit 1
}

$pattern = $null
if (-not $window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) {
    Write-Error "Window '$Title' supports no WindowPattern."
    exit 1
}

$pattern.Close()
Write-Output "closed '$Title'"
