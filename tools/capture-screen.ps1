# Captures the entire primary screen as a PNG (for popup surfaces that live in
# their own HWND, which PrintWindow cannot see).
param([Parameter(Mandatory = $true)][string]$Out)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$graphics.Dispose()

$directory = Split-Path -Parent $Out
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "saved $Out (${bounds.Width}x${bounds.Height})"
