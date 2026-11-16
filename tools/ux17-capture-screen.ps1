# UX-17 evidence: captures the whole primary screen in physical pixels, for
# surfaces that live in their own top-level window (the Windows 11 Snap Layouts
# flyout is drawn by the shell, so PrintWindow on the app cannot see it).
# Usage: powershell -NoProfile -File tools/ux17-capture-screen.ps1 -Out shot.png
param([Parameter(Mandatory = $true)][string]$Out)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class Ux17Screen {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public static int[] Bounds() {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        return new[] { GetSystemMetrics(0), GetSystemMetrics(1) };
    }
}
"@

Add-Type -AssemblyName System.Drawing

$size = [Ux17Screen]::Bounds()
$bitmap = New-Object System.Drawing.Bitmap($size[0], $size[1])
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($size[0], $size[1])))
$graphics.Dispose()

$directory = Split-Path -Parent $Out
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "saved $Out ($($size[0])x$($size[1]))"
