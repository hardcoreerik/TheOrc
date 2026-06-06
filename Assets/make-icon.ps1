Add-Type -AssemblyName System.Drawing

$png  = Join-Path $PSScriptRoot "icon.png"
$ico  = Join-Path $PSScriptRoot "icon.ico"

if (-not (Test-Path $png)) { Write-Error "icon.png not found"; exit 1 }

$sizes   = @(256, 128, 64, 48, 32, 16)
$entries = [System.Collections.Generic.List[hashtable]]::new()

foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $src = [System.Drawing.Image]::FromFile($png)
    $g.DrawImage($src, 0, 0, $sz, $sz)
    $g.Dispose(); $src.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add(@{ Size = $sz; Data = $ms.ToArray() })
    $bmp.Dispose(); $ms.Dispose()
    Write-Host "  Rendered ${sz}x${sz}"
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

# ICO header
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$entries.Count) # image count

# Directory: 16 bytes per entry
$dataOffset = [int](6 + 16 * $entries.Count)
foreach ($e in $entries) {
    # ICO format: 256 is encoded as 0
    $dim = if ($e.Size -eq 256) { [byte]0 } else { [byte]($e.Size) }
    $writer.Write($dim)               # width
    $writer.Write($dim)               # height
    $writer.Write([byte]0)            # colour count (0 = truecolour)
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # colour planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$e.Data.Length)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $e.Data.Length
}

# Image data
foreach ($e in $entries) { $writer.Write($e.Data) }

$writer.Flush()
[System.IO.File]::WriteAllBytes($ico, $stream.ToArray())
$stream.Dispose()

$kb = [math]::Round((Get-Item $ico).Length / 1KB, 1)
Write-Host "Created: $ico ($kb KB)"
