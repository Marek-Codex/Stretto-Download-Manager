# Captures a window by process name to a PNG, for checking UI changes without a human
# having to look. Falls back to the full primary screen if the window cannot be measured.
param(
    [string]$ProcessName = 'Stretto.App',
    [string]$Out = "$env:TEMP\stretto-shot.png",
    [int]$DelaySeconds = 4
)

Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

Start-Sleep -Seconds $DelaySeconds

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

if (-not $proc) { Write-Error "No window found for $ProcessName"; exit 1 }

[void][Win]::ShowWindow($proc.MainWindowHandle, 9)   # SW_RESTORE
[void][Win]::SetForegroundWindow($proc.MainWindowHandle)

# Park the pointer away from the window so hover states do not leak into the capture
# and look like selection.
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(5, 5)
Start-Sleep -Milliseconds 900

# DWM's extended frame bounds, not GetWindowRect: the latter includes the invisible
# resize border and leaves a transparent margin around the capture.
$rect = New-Object Win+RECT
$ok = [Win]::DwmGetWindowAttribute($proc.MainWindowHandle, 9, [ref]$rect, 16)

if ($ok -eq 0 -and $rect.R -gt $rect.L) {
    $x = $rect.L; $y = $rect.T
    $w = $rect.R - $rect.L; $h = $rect.B - $rect.T
} else {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $x = $screen.X; $y = $screen.Y; $w = $screen.Width; $h = $screen.Height
}

$bitmap = New-Object System.Drawing.Bitmap $w, $h
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($x, $y, 0, 0, $bitmap.Size)
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()

Write-Output "$Out ($w x $h)"
