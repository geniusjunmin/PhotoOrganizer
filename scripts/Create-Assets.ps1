param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($size in @(44, 50, 150)) {
    $image = New-Object Drawing.Bitmap($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::FromArgb(0, 109, 177))
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, ([Math]::Max(2, $size / 24)))
    $graphics.DrawRectangle($pen, [single]($size * .18), [single]($size * .2), [single]($size * .64), [single]($size * .6))
    $points = [Drawing.PointF[]]@(
        (New-Object Drawing.PointF([single]($size * .2), [single]($size * .73))),
        (New-Object Drawing.PointF([single]($size * .42), [single]($size * .43))),
        (New-Object Drawing.PointF([single]($size * .56), [single]($size * .59))),
        (New-Object Drawing.PointF([single]($size * .68), [single]($size * .49))),
        (New-Object Drawing.PointF([single]($size * .8), [single]($size * .7)))
    )
    $graphics.DrawLines($pen, $points)
    $brush = New-Object Drawing.SolidBrush([Drawing.Color]::White)
    $graphics.FillEllipse($brush, [single]($size * .63), [single]($size * .28), [single]($size * .09), [single]($size * .09))
    $image.Save((Join-Path $Destination "Logo$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    $brush.Dispose()
    $pen.Dispose()
    $graphics.Dispose()
    $image.Dispose()
}
