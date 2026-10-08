# Generates src/AIUsageBar/Assets/app.ico (16/24/32/48/64/256 px) from a 16x16 dot design.
# Run once; the .ico is committed. Usage: pwsh tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "src/AIUsageBar/Assets/app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# 16x16 design: '.' transparent, 'k' frame, 'c' Claude, 'x' Codex, 'g' Grok, 'e' empty gauge cell
$design = @(
    "................",
    ".kkkkkkkkkkkkkk.",
    ".kkkkkkkkkkkkkk.",
    ".kkkkkkkkkkeekk.",
    ".kkkkkkxxkkeekk.",
    ".kkkkkkxxkkeekk.",
    ".kkcckkxxkkeekk.",
    ".kkcckkxxkkggkk.",
    ".kkcckkxxkkggkk.",
    ".kkcckkxxkkggkk.",
    ".kkcckkxxkkggkk.",
    ".kkcckkxxkkggkk.",
    ".kkcckkxxkkggkk.",
    ".kkkkkkkkkkkkkk.",
    ".kkkkkkkkkkkkkk.",
    "................"
)
$colors = @{
    'k' = [Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x1C)
    'c' = [Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57)
    'x' = [Drawing.Color]::FromArgb(255, 0x10, 0xA3, 0x7F)
    'g' = [Drawing.Color]::FromArgb(255, 0xE8, 0xE8, 0xE8)
    'e' = [Drawing.Color]::FromArgb(255, 0x3A, 0x3A, 0x3A)
}
$base = New-Object Drawing.Bitmap 16, 16, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt 16; $y++) {
    for ($x = 0; $x -lt 16; $x++) {
        $ch = $design[$y][$x]
        $base.SetPixel($x, $y, $(if ($colors.ContainsKey([string]$ch)) { $colors[[string]$ch] } else { [Drawing.Color]::Transparent }))
    }
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($base, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (Windows Vista+).
$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
"wrote $out"
