# Build pet.ico from a JPG that has a rounded-rect icon card on a near-white field.
# Steps: detect the card bounding box, crop, then for each target size downscale and
# apply a rounded-rect alpha mask so the white border becomes transparent.
# Run from the pet dir. ASCII-only (PowerShell 5 reads this under OEM codepage).
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$baseDir = $PSScriptRoot
if (-not $baseDir) { $baseDir = (Get-Location).Path }
$srcPath = Join-Path $baseDir 'assets\pet_icon_q.jpg'
$outPath = Join-Path $baseDir 'pet.ico'
$prevPath = Join-Path $baseDir '_icon_preview.png'

function IsNearWhite([System.Drawing.Color]$c, [int]$t) {
    return ($c.R -ge $t) -and ($c.G -ge $t) -and ($c.B -ge $t)
}

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$W = $src.Width; $H = $src.Height
$t = 245            # near-white threshold for the border field
$margin = [int]($W * 0.55)  # scan only outer 55% to find the card edge

# Detect card bounding box by scanning inward from each edge until a non-white pixel.
$minX = -1; $minY = -1; $maxX = -1; $maxY = -1
$step = 4  # subsample rows/cols for speed

# top edge
for ($y = 0; $y -lt $margin; $y += $step) {
    for ($x = $margin; $x -lt $W; $x += $step) {
        if (-not (IsNearWhite $src.GetPixel($x, $y) $t)) { $minY = $y; break }
    }
    if ($minY -ge 0) { break }
}
# bottom edge
for ($y = $H - 1 - (($H - 1) % $step); $y -gt $H - 1 - $margin; $y -= $step) {
    for ($x = $margin; $x -lt $W; $x += $step) {
        if (-not (IsNearWhite $src.GetPixel($x, $y) $t)) { $maxY = $y; break }
    }
    if ($maxY -ge 0) { break }
}
# left edge
for ($x = 0; $x -lt $margin; $x += $step) {
    for ($y = $margin; $y -lt $H; $y += $step) {
        if (-not (IsNearWhite $src.GetPixel($x, $y) $t)) { $minX = $x; break }
    }
    if ($minX -ge 0) { break }
}
# right edge
for ($x = $W - 1 - (($W - 1) % $step); $x -gt $W - 1 - $margin; $x -= $step) {
    for ($y = $margin; $y -lt $H; $y += $step) {
        if (-not (IsNearWhite $src.GetPixel($x, $y) $t)) { $maxX = $x; break }
    }
    if ($maxX -ge 0) { break }
}

if ($minX -lt 0 -or $minY -lt 0 -or $maxX -lt 0 -or $maxY -lt 0) {
    throw "Could not auto-detect card boundary in $srcPath"
}
# pad crop a touch inward so border wedge is fully covered by the later rounded mask
$pad = 2
$cx = $minX + $pad
$cy = $minY + $pad
$cw = ($maxX - $minX + 1) - 2 * $pad
$ch = ($maxY - $minY + 1) - 2 * $pad
Write-Output ("card crop: x=$cx y=$cy w=$cw h=$ch (orig ${W}x${H})")

$card = New-Object System.Drawing.Bitmap($cw, $ch, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($card)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)),
    (New-Object System.Drawing.Rectangle($cx, $cy, $cw, $ch)),
    [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$src.Dispose()

# Rounded-rect alpha mask on a square of side $size, radius ~22% side.
function ApplyRoundedMask([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    if ($bmp.Height -ne $size) { throw 'mask expects square frame' }
    $r = [int]([double]$size * 0.22)
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $px = $bmp.GetPixel($x, $y)
            if ($px.A -eq 0) { continue }
            # inside the rounded rectangle?
            $rx = $x; $ry = $y
            $cx0 = $r; $cy0 = $r; $cx1 = $size - 1 - $r; $cy1 = $size - 1 - $r
            if ($rx -lt $cx0) { $rx = $cx0 }
            elseif ($rx -gt $cx1) { $rx = $cx1 }
            if ($ry -lt $cy0) { $ry = $cy0 }
            elseif ($ry -gt $cy1) { $ry = $cy1 }
            $dx = $x - $rx; $dy = $y - $ry
            $dist2 = $dx * $dx + $dy * $dy
            $inside = ($dist2 -le ($r * $r))
            if (-not $inside) {
                $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
            }
        }
    }
    return $bmp
}

# Build frames
$sizes = 256, 48, 32, 16
$frames = @()
$tmp = Join-Path $env:TEMP ('petico_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gg = [System.Drawing.Graphics]::FromImage($bmp)
        $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $gg.Clear([System.Drawing.Color]::Transparent)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
        $gg.DrawImage($card, $rect)
        $gg.Dispose()
        ApplyRoundedMask $bmp | Out-Null
        $png = Join-Path $tmp "$s.png"
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += @{ Size = $s; Path = $png }
    }
    $card.Dispose()

    # package ICO
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

    # preview = 256 frame reuse (extract from png we saved)
    $prev256 = Join-Path $tmp '256.png'
    Copy-Item $prev256 $prevPath -Force
    Write-Output ("wrote ico: {0} bytes, frames={1}" -f (Get-Item $outPath).Length, $count)
    Write-Output ("preview: {0}" -f $prevPath)
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}