# UX-17 evidence helper: turns the Windows High Contrast policy on or off for
# the duration of a capture run and prints the system colours the app's
# HighContrast.xaml resolves its brushes from.
#
# Usage: powershell -NoProfile -File tools/ux17-highcontrast.ps1 -On
#        powershell -NoProfile -File tools/ux17-highcontrast.ps1 -Off
#        powershell -NoProfile -File tools/ux17-highcontrast.ps1 -State
#
# This is a real host accessibility setting, not an app flag: Windows applies the
# user's selected contrast theme, and the app's ThemeManager reacts to the
# SystemParameters change. Always finish with -Off.
param(
    [switch]$On,
    [switch]$Off,
    [switch]$State
)

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class HighContrast {
    [StructLayout(LayoutKind.Sequential)]
    public struct HIGHCONTRAST {
        public int cbSize;
        public int dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    public const int SPI_GETHIGHCONTRAST = 0x0042;
    public const int SPI_SETHIGHCONTRAST = 0x0043;
    public const int HCF_HIGHCONTRASTON = 0x00000001;
    public const int SPIF_UPDATEINIFILE = 0x01;
    public const int SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(int action, int param, ref HIGHCONTRAST value, int winIni);

    public static bool IsOn() {
        var hc = new HIGHCONTRAST();
        hc.cbSize = Marshal.SizeOf<HIGHCONTRAST>();
        SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0);
        return (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    public static bool Set(bool on) {
        var hc = new HIGHCONTRAST();
        hc.cbSize = Marshal.SizeOf<HIGHCONTRAST>();
        hc.dwFlags = on ? HCF_HIGHCONTRASTON : 0;
        hc.lpszDefaultScheme = IntPtr.Zero;
        return SystemParametersInfo(SPI_SETHIGHCONTRAST, hc.cbSize, ref hc, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }
}
"@

if ($On -and $Off) { Write-Error "Use -On, -Off or -State, not both."; exit 1 }
if (-not $On -and -not $Off -and -not $State) { $State = $true }

if ($On -or $Off) {
    $want = [bool]$On
    if (-not [HighContrast]::Set($want)) {
        Write-Error "SPI_SETHIGHCONTRAST failed (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
        exit 1
    }
    Start-Sleep -Milliseconds 800
}

function Format-Color($color) {
    $argb = $color.ToArgb()
    return ("#{0:X8}" -f $argb)
}

Write-Output ("high contrast: " + $(if ([HighContrast]::IsOn()) { "on" } else { "off" }))
Write-Output ("window      : " + (Format-Color ([System.Drawing.SystemColors]::Window)))
Write-Output ("window text : " + (Format-Color ([System.Drawing.SystemColors]::WindowText)))
Write-Output ("highlight   : " + (Format-Color ([System.Drawing.SystemColors]::Highlight)))
Write-Output ("highlight tx: " + (Format-Color ([System.Drawing.SystemColors]::HighlightText)))
Write-Output ("frame       : " + (Format-Color ([System.Drawing.SystemColors]::WindowFrame)))
Write-Output ("grey text   : " + (Format-Color ([System.Drawing.SystemColors]::GrayText)))
exit 0
