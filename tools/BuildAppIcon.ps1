[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $PSScriptRoot "..\assets\mark.png" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $PSScriptRoot "..\assets\AreaRec.ico" }
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $SourcePath))
$canvas = New-Object System.Drawing.Bitmap(256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
$graphics = [System.Drawing.Graphics]::FromImage($canvas)

try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 15, 15, 18))
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $destinationRectangle = New-Object System.Drawing.Rectangle(28, 28, 200, 200)
    $graphics.DrawImage($source, $destinationRectangle)
    $graphics.Flush()

    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $iconHandle = $canvas.GetHicon()
    try {
        $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
        try {
            $stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
            try { $icon.Save($stream) } finally { $stream.Dispose() }
        } finally {
            $icon.Dispose()
        }
    } finally {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class AreaRecNativeMethods {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@
        [AreaRecNativeMethods]::DestroyIcon($iconHandle) | Out-Null
    }
} finally {
    $graphics.Dispose()
    $canvas.Dispose()
    $source.Dispose()
}

Write-Host "Generated $OutputPath"
