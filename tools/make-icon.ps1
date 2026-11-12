$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/MiControl/Assets'
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
function Rounded([single]$x,[single]$y,[single]$width,[single]$height,[single]$radius) {
    $path=New-Object Drawing.Drawing2D.GraphicsPath
    $d=2*$radius
    $path.AddArc($x,$y,$d,$d,180,90);$path.AddArc($x+$width-$d,$y,$d,$d,270,90)
    $path.AddArc($x+$width-$d,$y+$height-$d,$d,$d,0,90);$path.AddArc($x,$y+$height-$d,$d,$d,90,90);$path.CloseFigure()
    return ,$path
}
$frames=New-Object 'System.Collections.Generic.List[byte[]]'
$sizes=16,24,32,48,64,128,256
foreach($size in $sizes) {
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode='AntiAlias';$graphics.PixelOffsetMode='HighQuality';$graphics.ScaleTransform($size/256.0,$size/256.0)
    $shape=Rounded 12 12 232 232 52
    $gradient=New-Object Drawing.Drawing2D.LinearGradientBrush([Drawing.Point]::new(40,18),[Drawing.Point]::new(215,240),[Drawing.Color]::FromArgb(50,179,255),[Drawing.Color]::FromArgb(36,77,218))
    $graphics.FillPath($gradient,$shape)
    $rail=New-Object Drawing.Pen([Drawing.Color]::FromArgb(145,255,255,255),9)
    $rail.StartCap='Round';$rail.EndCap='Round'
    foreach($x in 72,128,184) { $graphics.DrawLine($rail,[single]$x,[single]61,[single]$x,[single]195) }
    foreach($point in @(@(72,101),@(128,160),@(184,119))) {
        $graphics.FillEllipse([Drawing.Brushes]::White,[single]($point[0]-15),[single]($point[1]-15),[single]30,[single]30)
    }
    if($size -eq 256){$bitmap.Save((Join-Path $assetRoot 'MiControl-preview.png'),[Drawing.Imaging.ImageFormat]::Png)}
    $stream=New-Object IO.MemoryStream;$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$frames.Add($stream.ToArray())
    $stream.Dispose();$rail.Dispose();$gradient.Dispose();$shape.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$file=[IO.File]::Create((Join-Path $assetRoot 'MiControl.ico'));$writer=New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
    $offset=6+16*$frames.Count
    for($i=0;$i -lt $frames.Count;$i++) {
        $dimension=[byte]($sizes[$i] % 256);$writer.Write($dimension);$writer.Write($dimension);$writer.Write([uint16]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset);$offset+=$frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write($frame)}
} finally {$writer.Dispose();$file.Dispose()}

# Notification-area icons use a monochrome Fluent-style glyph, with no colored tile.
foreach($theme in 'Dark','Light') {
    $traySizes=16,20,24,32,40,48,64
    $trayFrames=New-Object 'System.Collections.Generic.List[byte[]]'
    $color=if($theme -eq 'Dark'){[Drawing.Color]::FromArgb(245,245,245)}else{[Drawing.Color]::FromArgb(32,32,32)}
    foreach($size in $traySizes) {
        $bitmap=New-Object Drawing.Bitmap($size,$size);$graphics=[Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode='AntiAlias';$graphics.PixelOffsetMode='HighQuality';$graphics.ScaleTransform($size/16.0,$size/16.0)
        $pen=New-Object Drawing.Pen($color,1.15);$pen.StartCap='Round';$pen.EndCap='Round';$brush=New-Object Drawing.SolidBrush($color)
        foreach($x in 4,8,12){$graphics.DrawLine($pen,[single]$x,[single]2.5,[single]$x,[single]13.5)}
        foreach($point in @(@(4,6),@(8,10),@(12,7))) {$graphics.FillEllipse($brush,[single]($point[0]-1.75),[single]($point[1]-1.75),[single]3.5,[single]3.5)}
        if($size -eq 64){$bitmap.Save((Join-Path $assetRoot "Tray$theme-preview.png"),[Drawing.Imaging.ImageFormat]::Png)}
        $stream=New-Object IO.MemoryStream;$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$trayFrames.Add($stream.ToArray())
        $stream.Dispose();$brush.Dispose();$pen.Dispose();$graphics.Dispose();$bitmap.Dispose()
    }
    $file=[IO.File]::Create((Join-Path $assetRoot "Tray$theme.ico"));$writer=New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$trayFrames.Count);$offset=6+16*$trayFrames.Count
        for($i=0;$i -lt $trayFrames.Count;$i++) {
            $writer.Write([byte]$traySizes[$i]);$writer.Write([byte]$traySizes[$i]);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
            $writer.Write([uint32]$trayFrames[$i].Length);$writer.Write([uint32]$offset);$offset+=$trayFrames[$i].Length
        }
        foreach($frame in $trayFrames){$writer.Write($frame)}
    } finally {$writer.Dispose();$file.Dispose()}
}
