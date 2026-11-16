param(
    [string]$Title = "Emulator Workspace",
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Value
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if (-not $window) { Write-Error "Window '$Title' not found."; exit 1 }

$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
if (-not $element) { Write-Error "Element '$Name' not found."; exit 1 }

$valuePattern = $null
if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
    Write-Error "Element '$Name' has no ValuePattern."
    exit 1
}
$valuePattern.SetValue($Value)
Write-Output "set '$Name' = '$Value'"
