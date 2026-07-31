param(
    [string]$PngPath = "winmeters.png",
    [string]$IcoPath = "winmeters.ico"
)

try {
    Add-Type -AssemblyName System.Drawing
} catch {
    Write-Error "Could not load System.Drawing. Ensure you are running on a Windows system that supports it."
    exit 1
}

if (-not (Test-Path $PngPath)) {
    Write-Error "Could not find file '$PngPath'."
    exit 1
}

$sourceImage = [System.Drawing.Image]::FromFile($PngPath)
$sizes = @(256, 48, 32, 16)
$imageData = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $bmp.MakeTransparent()

    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $g.DrawImage($sourceImage, 0, 0, $size, $size)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()

    $imageData += ,@{ Size = $size; Bytes = $bytes }
}

$sourceImage.Dispose()

# Create ICO file manually. The container is a 6-byte ICONDIR header followed by
# a 16-byte ICONDIRENTRY for each frame, then the raw PNG data for each frame.
$fs = New-Object System.IO.FileStream($IcoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR header
$bw.Write([uint16]0)                # Reserved
$bw.Write([uint16]1)                # Type (1 = ICO)
$bw.Write([uint16]$imageData.Count) # Number of images

# Header is 6 bytes; each directory entry is 16 bytes.
$offset = 6 + (16 * $imageData.Count)

# ICONDIRENTRY for each image
foreach ($img in $imageData) {
    # ICO format encodes 256 as 0 in the byte-sized width/height fields.
    $width  = if ($img.Size -eq 256) { [byte]0 } else { [byte]$img.Size }
    $height = if ($img.Size -eq 256) { [byte]0 } else { [byte]$img.Size }

    $bw.Write([byte]$width)
    $bw.Write([byte]$height)
    $bw.Write([byte]0)                    # ColorCount
    $bw.Write([byte]0)                    # Reserved
    $bw.Write([uint16]1)                  # Planes
    $bw.Write([uint16]32)                 # BitCount
    $bw.Write([uint32]$img.Bytes.Length)  # BytesInRes
    $bw.Write([uint32]$offset)            # ImageOffset

    $offset += $img.Bytes.Length
}

# Image data (PNG streams)
foreach ($img in $imageData) {
    $bw.Write($img.Bytes)
}

$bw.Close()
$fs.Dispose()

Write-Host "Successfully created $IcoPath with sizes: $($sizes -join ', ')"
