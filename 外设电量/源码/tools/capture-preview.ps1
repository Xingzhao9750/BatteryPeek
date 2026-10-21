param(
    [string]$Path = 'C:\Users\YIXIN\Documents\deepseek-harness\default-workspace\BatteryPeek\dist\BatteryPeek.exe',
    [string]$Out = 'C:\Users\YIXIN\Documents\deepseek-harness\default-workspace\_shots\popup.png',
    [int]$WaitMs = 2200
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Force -Path $outDir }

$proc = Start-Process -FilePath $Path -ArgumentList '--preview' -PassThru
Start-Sleep -Milliseconds $WaitMs

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Start-Sleep -Milliseconds 100
if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "saved $Out ($($bounds.Width)x$($bounds.Height))"
