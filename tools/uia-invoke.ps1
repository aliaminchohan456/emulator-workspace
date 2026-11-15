# Invokes a UIA-named button in a top-level window (by exact title, or by
# window handle for a window UIA does not list as a desktop child).
# Usage: powershell -NoProfile -File tools/uia-invoke.ps1 -Title "Emulator Workspace" -Name "Theme"
param(
    [Parameter(Mandatory = $false)][string]$Title = "",
    [Parameter(Mandatory = $true)][string]$Name,
    [long]$Handle = 0
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

$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
if (-not $element) {
    Write-Error "Element '$Name' not found in '$Title'."
    exit 1
}

$invoke = $null
if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
    $invoke.Invoke()
    Write-Output "invoked '$Name'"
    exit 0
}

$toggle = $null
if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
    $toggle.Toggle()
    Write-Output "toggled '$Name'"
    exit 0
}

# Tab items and radio-style destinations expose SelectionItemPattern.
$select = $null
if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) {
    $select.Select()
    Write-Output "selected '$Name'"
    exit 0
}

Write-Error "Element '$Name' supports no invoke/toggle/select pattern."
exit 1
