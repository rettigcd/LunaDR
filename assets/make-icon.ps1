param([string]$Assets)
Add-Type -AssemblyName System.Drawing

function Resize-Png([System.Drawing.Image]$src, [int]$size) {
	$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)
	$g.DrawImage($src, 0, 0, $size, $size)
	$g.Dispose()
	return $bmp
}

function Png-Bytes([System.Drawing.Bitmap]$bmp) {
	$ms = New-Object System.IO.MemoryStream
	$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
	return ,$ms.ToArray()
}

# The icon: several sizes, each stored as a PNG inside the .ico file (supported since Windows Vista).
$icon = [System.Drawing.Image]::FromFile((Join-Path $Assets "lunadr-icon.png"))
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { $b = Resize-Png $icon $s; ,(Png-Bytes $b) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
	$s = $sizes[$i]
	$w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
	$w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
	$w.Write([byte]0); $w.Write([byte]0)
	$w.Write([uint16]1); $w.Write([uint16]32)
	$w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
	$offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $Assets "lunadr.ico"), $out.ToArray())
$icon.Dispose()

# The picture shown in the app: 256 px is plenty for a header image.
$logo = [System.Drawing.Image]::FromFile((Join-Path $Assets "lunadr-logo.png"))
$small = Resize-Png $logo 256
$small.Save((Join-Path $Assets "lunadr-logo-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose(); $logo.Dispose()
"done"
