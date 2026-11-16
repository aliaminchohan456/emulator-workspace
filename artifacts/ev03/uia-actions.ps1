param(
    [string]$Title = "Emulator Workspace",
    [string]$Dump = "",
    [string]$Hover = "",
    [int]$WaitMs = 1400
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if (-not $window) { Write-Error "Window '$Title' not found."; exit 1 }

if ($Dump) {
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $lines = foreach ($element in $all) {
        $name = $element.Current.Name
        $type = $element.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        $rect = $element.Current.BoundingRectangle
        if ($name -and $type -match 'Button|MenuItem|TabItem|ListItem') {
            '{0}|{1}|enabled={2}|{3:N0},{4:N0},{5:N0},{6:N0}' -f `
                $type, $name, $element.Current.IsEnabled, $rect.X, $rect.Y, $rect.Width, $rect.Height
        }
    }
    $lines | Set-Content -Encoding utf8 $Dump
    Write-Output "wrote $Dump ($($lines.Count) rows)"
}

if ($Hover) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Hover)
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
    if (-not $element) { Write-Error "Element '$Hover' not found."; exit 1 }
    $rect = $element.Current.BoundingRectangle
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Ev03Cursor {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
"@
    [void][Ev03Cursor]::SetThreadDpiAwarenessContext([IntPtr](-4))
    [void][Ev03Cursor]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds $WaitMs
    Write-Output "hovering '$Hover' at ($x,$y), enabled=$($element.Current.IsEnabled)"
}
