# Build pet.ico from a character-on-pure-black JPG: key out near-black to transparent,
# crop to the character bounding box, then emit multi-size transparent ICO frames.
# No colored background, no rounded-rect tile -- just the character silhouette.
# ASCII-only (PowerShell 5 reads scripts under OEM codepage unless saved with BOM).
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$baseDir = $PSScriptRoot
if (-not $baseDir) { $baseDir = (Get-Location).Path }
$srcPath = Join-Path $baseDir 'assets\pet_icon_standalone.jpg'
$outPath = Join-Path $baseDir 'pet.ico'
$prevPath = Join-Path $baseDir '_icon_preview.png'

$t0 = 26          # max-channel below t0 => alpha 0
$t1 = 70          # max-channel at/above t1 => alpha 255 (soft ramp between)

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
# work at a manageable resolution for the GetPixel loop
$workW = 1024
$workH = 1024
$work = New-Object System.Drawing.Bitmap($workW, $workH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($work)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($src, 0, 0, $workW, $workH)
$g.Dispose()
$src.Dispose()

# Key black->transparent with a soft ramp and mild edge decontamination.
function AlphaFor([double]$m) {
    if ($m -le $t0) { return 0 }
    if ($m -ge $t1) { return 255 }
    return [int](255.0 * ($m - $t0) / ($t1 - $t0))
}
$minA = $workW; $maxA = 0; $minB = $workH; $maxB = 0
For($y=0; $y -lt $workH; $y++){
  for($x=0; $x -lt $workW; $x++){
    $c = $work.GetPixel($x,$y)
    $m = [double]([Math]::Max([Math]::Max($c.R,$c.G),$c.B))
    $a = AlphaFor $m
    if ($a -gt 0) {
      # edge decontamination: pull semi-transparent fringe toward full color to reduce dark rim
      if ($a -lt 255) {
        $lift = 255.0 / $a
        $nr = [int]([Math]::Min(255.0, $c.R*$lift))
        $ng = [int]([Math]::Min(255.0, $c.G*$lift))
        $nb = [int]([Math]::Min(255.0, $c.B*$lift))
        $work.SetPixel($x,$y,[System.Drawing.Color]::FromArgb($a,$nr,$ng,$nb))
      } else {
        $work.SetPixel($x,$y,[System.Drawing.Color]::FromArgb(255,$c.R,$c.G,$c.B))
      }
      if ($x -lt $minA){$minA=$x}; if ($x -gt $maxA){$maxA=$x}
      if ($y -lt $minB){$minB=$y}; if ($y -gt $maxB){$maxB=$y}
    } else {
      $work.SetPixel($x,$y,[System.Drawing.Color]::FromArgb(0,0,0,0))
    }
  }
}
if ($maxA -le $minA -or $maxB -le $minB) { throw "no opaque content found" }
$pad = 4
$bx = [Math]::Max(0, $minA - $pad); $by = [Math]::Max(0, $minB - $pad)
$bw = [Math]::Min($workW - $bx, $maxA - $minA + 1 + 2*$pad)
$bh = [Math]::Min($workH - $by, $maxB - $minB + 1 + 2*$pad)
Write-Output ("character bbox: x=$bx y=$by w=$bw h=$bh")

$card = New-Object System.Drawing.Bitmap($bw, $bh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gg = [System.Drawing.Graphics]::FromImage($card)
$gg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$gg.DrawImage($work, (New-Object System.Drawing.Rectangle(0,0,$bw,$bh)),
              (New-Object System.Drawing.Rectangle($bx,$by,$bw,$bh)),
              [System.Drawing.GraphicsUnit]::Pixel)
$gg.Dispose()
$work.Dispose()

$sizes = 256, 48, 32, 16
$frames = @()
$tmp = Join-Path $env:TEMP ('petico_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g2 = [System.Drawing.Graphics]::FromImage($bmp)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g2.Clear([System.Drawing.Color]::Transparent)
        # fit inside square preserving aspect ratio, centered (no horizontal/vertical stretch)
        $cw = [double]$card.Width
        $ch = [double]$card.Height
        $scale = [double]$s / ([Math]::Max($cw, $ch))
        $dw = [int]($cw * $scale)
        $dh = [int]($ch * $scale)
        $dx = [int](($s - $dw) / 2)
        $dy = [int](($s - $dh) / 2)
        $g2.DrawImage($card, $dx, $dy, $dw, $dh)
        $g2.Dispose()
        $png = Join-Path $tmp "$s.png"
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += @{ Size = $s; Path = $png }
    }
    $card.Dispose()

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
        $bw2 = [byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size }))
        $w.Write($bw2); $w.Write($bw2); $w.Write([byte]0); $w.Write([byte]0)
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

    Copy-Item (Join-Path $tmp '256.png') $prevPath -Force
    Write-Output ("wrote ico: {0} bytes, frames={1}" -f (Get-Item $outPath).Length, $count)
    Write-Output ("preview: {0}" -f $prevPath)
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}