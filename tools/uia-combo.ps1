# Expands a UIA-named ComboBox in a top-level window (by exact title).
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Name,
    [ValidateSet('Expand', 'Collapse')][string]$Action = 'Expand'
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
if (-not $window) {
    Write-Error "Window '$Title' not found."
    exit 1
}

$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
if (-not $element) {
    Write-Error "Element '$Name' not found in '$Title'."
    exit 1
}

$expand = $null
if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
    Write-Error "Element '$Name' supports no expand/collapse pattern."
    exit 1
}

if ($Action -eq 'Expand') { $expand.Expand() } else { $expand.Collapse() }
Write-Output "$Action '$Name'"
