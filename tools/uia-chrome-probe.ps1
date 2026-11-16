# Walks the UI Automation control view of the workspace window and reports every
# control with its enabled / keyboard-focusable / offscreen state, so the chrome
# behind a floating surface can be proven inert before and after it opens.
# Usage: powershell -NoProfile -File tools/uia-chrome-probe.ps1 -Title "Emulator Workspace" -Out tree.txt
#        [-Name "Studio,Devices,Logcat page,Agent sessions,Root Lab,Settings,Search commands"]
# The -RequireInert switch exits 1 when any named control is still enabled and
# keyboard-focusable; -RequireLive is the opposite check.
param(
    [Parameter(Mandatory = $false)][string]$Title = "Emulator Workspace",
    [long]$Handle = 0,
    [string]$Out = "",
    [string]$Name = "Back to Studio,Device library,Logcat page,Agent sessions,Root Lab,Settings,Search commands,Switch theme,Refresh workspace state,Embedded Android viewport",
    [switch]$RequireInert,
    [switch]$RequireLive
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

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$lines = New-Object System.Collections.Generic.List[string]
$index = @{}

function Walk($element, $depth) {
    if ($depth -gt 30) { return }
    $name = $element.Current.Name
    $type = $element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")
    $enabled = $element.Current.IsEnabled
    $focusable = $element.Current.IsKeyboardFocusable
    $offscreen = $element.Current.IsOffscreen
    $pad = "  " * $depth
    $lines.Add(("{0}{1,-34} {2,-12} enabled={3,-5} focusable={4,-5} offscreen={5}" -f $pad, $name, $type, $enabled, $focusable, $offscreen))
    if ($index.ContainsKey($name)) {
        $index[$name] += ,@($enabled, $focusable)
    }
    else {
        $index[$name] = @(, @($enabled, $focusable))
    }

    $child = $walker.GetFirstChild($element)
    while ($child) {
        Walk $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

Walk $window 0

$text = $lines -join [Environment]::NewLine
Write-Output $text
if ($Out.Length -gt 0) {
    $directory = Split-Path -Parent $Out
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    Set-Content -Path $Out -Value $text -Encoding utf8
}

$named = $Name.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 }
$live = @()
$inert = @()
$missing = @()
foreach ($candidate in $named) {
    if (-not $index.ContainsKey($candidate)) { $missing += $candidate; continue }
    $states = $index[$candidate]
    $anyLive = $false
    $anyInert = $false
    foreach ($state in $states) {
        if ($state[0] -and $state[1]) { $anyLive = $true }
        if (-not $state[0]) { $anyInert = $true }
    }
    if ($anyLive) { $live += $candidate }
    elseif ($anyInert) { $inert += $candidate }
}

Write-Output ""
Write-Output ("chrome live (enabled + focusable): " + ($(if ($live.Count) { $live -join ', ' } else { 'none' })))
Write-Output ("chrome inert (disabled): " + ($(if ($inert.Count) { $inert -join ', ' } else { 'none' })))
Write-Output ("not in tree: " + ($(if ($missing.Count) { $missing -join ', ' } else { 'none' })))

if ($RequireInert -and $live.Count -gt 0) {
    Write-Error "Chrome is still reachable: $($live -join ', ')"
    exit 1
}
if ($RequireLive -and $live.Count -eq 0) {
    Write-Error "No chrome is reachable: $(($inert + $missing) -join ', ')"
    exit 1
}

Write-Output "probe ok"
