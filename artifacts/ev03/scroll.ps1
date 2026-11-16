param(
    [string]$Title = "Emulator Workspace",
    [int]$MinX = 1000,
    [string]$Direction = "Down",
    [int]$Steps = 2
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if (-not $window) { Write-Error "Window '$Title' not found."; exit 1 }

$all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    $rect = $element.Current.BoundingRectangle
    if ($rect.X -lt $MinX -or $rect.Width -lt 100) { continue }
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { continue }
    $vertical = $pattern.Current.VerticalScrollPercent
    $horizontal = $pattern.Current.HorizontalScrollPercent
    Write-Output ("scrollable: {0} at {1:N0},{2:N0} {3:N0}x{4:N0} v={5:N1} h={6:N1}" -f `
        $element.Current.ControlType.ProgrammaticName, $rect.X, $rect.Y, $rect.Width, $rect.Height, $vertical, $horizontal)
    if ($vertical -lt 0) { continue }
    $amount = if ($Direction -eq "Down") {
        [System.Windows.Automation.ScrollAmount]::LargeIncrement
    } else {
        [System.Windows.Automation.ScrollAmount]::LargeDecrement
    }
    for ($i = 0; $i -lt $Steps; $i++) {
        $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, $amount)
        Start-Sleep -Milliseconds 250
    }
    Write-Output "scrolled $Direction x$Steps"
    exit 0
}
Write-Error "No vertical scroll pattern found right of $MinX."
exit 1
