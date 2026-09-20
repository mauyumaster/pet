# Build pet.ico from assets/pet_icon.jpg (PNG frames 16/32/48/256). Run from the pet dir.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$srcPath = Join-Path (Get-Location) 'assets\pet_icon.jpg'
$outPath = Join-Path (Get-Location) 'pet.ico'

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$sizes = 256, 48, 32, 16
$frames = @()

$tmp = Join-Path $env:TEMP ('petico_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
        $g.DrawImage($src, $rect, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $png = Join-Path $tmp "$s.png"
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += @{ Size = $s; Path = $png }
    }

    $count = $frames.Count
    $dataOffset = 6 + 16 * $count
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]1); $w.Write([byte]0)
    $w.Write([System.UInt16]$count)
    $offset = $dataOffset
    $blobs = @()
    foreach ($f in $frames) {
        $bytes = [System.IO.File]::ReadAllBytes($f.Path)
        $bw = [byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size }))
        $w.Write($bw); $w.Write($bw); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([System.UInt16]1); $w.Write([System.UInt16]32)
        $w.Write([System.UInt32]$bytes.Length)
        $w.Write([System.UInt32]$offset)
        $offset += $bytes.Length
        $blobs += $bytes
    }
    foreach ($b in $blobs) { $w.Write($b) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
    $w.Dispose(); $ms.Dispose()
    Write-Output ("wrote ico: {0} bytes, frames={1}" -f (Get-Item $outPath).Length, $count)
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}