# make-icon.ps1 — Generate a multi-resolution .ico from icon.png
# Run from the Assets\ folder: .\make-icon.ps1
#
# Requires one of:
#   Option A) ImageMagick  — https://imagemagick.org  (magick in PATH)
#   Option B) Pure .NET    — no dependencies, slower

param(
    [string]$SourcePng = "$PSScriptRoot\icon.png",
    [string]$OutIco    = "$PSScriptRoot\icon.ico"
)

if (-not (Test-Path $SourcePng)) {
    Write-Error "Source PNG not found: $SourcePng"
    exit 1
}

# ── Option A: ImageMagick (best quality) ──────────────────────────────────────
if (Get-Command magick -ErrorAction SilentlyContinue) {
    Write-Host "Using ImageMagick..."
    magick $SourcePng `
        -define icon:auto-resize=256,128,64,48,32,16 `
        $OutIco
    Write-Host "✅ Created $OutIco (ImageMagick)"
    exit 0
}

# ── Option B: Pure .NET System.Drawing ────────────────────────────────────────
Write-Host "ImageMagick not found — using .NET System.Drawing..."

Add-Type -AssemblyName System.Drawing

function New-IcoFile {
    param([string]$Png, [string]$Ico)

    $sizes   = @(256, 128, 64, 48, 32, 16)
    $entries = @()

    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $src = [System.Drawing.Image]::FromFile($Png)
        $g.DrawImage($src, 0, 0, $sz, $sz)
        $g.Dispose()
        $src.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += @{ Size = $sz; Data = $ms.ToArray() }
        $bmp.Dispose()
        $ms.Dispose()
    }

    # Build ICO file structure
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    # Header: reserved=0, type=1 (icon), count
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)

    # Directory entries come after the header (6 bytes) + all directory entries (16 bytes each)
    $dirSize  = 6 + 16 * $entries.Count
    $offset   = $dirSize

    foreach ($e in $entries) {
        $sz = [byte]($e.Size -eq 256 ? 0 : $e.Size)  # 256 stored as 0 in ICO format
        $writer.Write($sz)          # width
        $writer.Write($sz)          # height
        $writer.Write([byte]0)      # colour count (0=no palette)
        $writer.Write([byte]0)      # reserved
        $writer.Write([uint16]1)    # colour planes
        $writer.Write([uint16]32)   # bits per pixel
        $writer.Write([uint32]$e.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $e.Data.Length
    }

    foreach ($e in $entries) {
        $writer.Write($e.Data)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Ico, $stream.ToArray())
    $stream.Dispose()
}

New-IcoFile -Png $SourcePng -Ico $OutIco
Write-Host "✅ Created $OutIco (.NET fallback)"
