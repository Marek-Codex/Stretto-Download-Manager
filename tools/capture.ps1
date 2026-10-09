# Captures one window by title, using PrintWindow.
#
# The screen-region capture in screenshot.ps1 grabs whatever happens to be on top of the
# window, which is wrong whenever the target is not foreground -- and risks capturing
# unrelated windows. PrintWindow asks the window to render itself into a bitmap instead, so
# what comes back is that window and nothing else, occluded or not.
#
#   .\tools\capture.ps1 -TitleLike "New download" -Out shot.png

param(
    [Parameter(Mandatory = $true)][string]$TitleLike,
    [string]$Out = "$env:TEMP\stretto-window.png",
    [string]$ProcessName = 'Stretto.App'
)

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public static IntPtr Find(string titlePart, uint wantPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            if (!IsWindowVisible(h)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (wantPid != 0 && pid != wantPid) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$pids = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if (-not $pids) { Write-Error "$ProcessName is not running."; exit 1 }

$handle = [IntPtr]::Zero
foreach ($procId in $pids) {
    $handle = [WinCap]::Find($TitleLike, [uint32]$procId)
    if ($handle -ne [IntPtr]::Zero) { break }
}

if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No visible window of $ProcessName with a title containing '$TitleLike'."
    exit 2
}

# The DWM rect excludes the invisible resize border, so the image matches what is seen.
$rect = New-Object WinCap+RECT
[void][WinCap]::DwmGetWindowAttribute($handle, 9, [ref]$rect, 16)

$width = $rect.R - $rect.L
$height = $rect.B - $rect.T
if ($width -le 0 -or $height -le 0) { Write-Error 'Window has no size.'; exit 3 }

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()

# 2 = PW_RENDERFULLCONTENT, needed for hardware-composited content such as WinUI.
[void][WinCap]::PrintWindow($handle, $dc, 2)

$graphics.ReleaseHdc($dc)
$graphics.Dispose()
$bitmap.Save($Out)
$bitmap.Dispose()

"$Out ($width x $height)"
