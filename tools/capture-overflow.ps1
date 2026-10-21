Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$chevronX = $screen.Right - 520 + 250
$chevronY = $screen.Bottom - 23

[Mouse]::Click($chevronX, $chevronY)
Start-Sleep -Milliseconds 900

$w = 700
$h = 420
$x = $screen.Right - $w
$y = $screen.Bottom - 56 - $h
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$g.Dispose()
$out = 'C:\Users\YIXIN\Documents\deepseek-harness\default-workspace\_shots\overflow.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# Close the flyout again so the desktop is left as it was.
[Mouse]::Click($screen.Left + 300, $screen.Top + 300)
Write-Host "saved $out"
