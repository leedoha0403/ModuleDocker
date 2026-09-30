# Generates src\Dora.Widget.Host\Assets\ModuleDock.ico (PNG-compressed frames) - same motif as the Host header icon.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Dora.Widget.Host\Assets\ModuleDock.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  function Round($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
  }
  $m = $s * 0.04
  $back = Round $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.24)
  $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.ColorTranslator]::FromHtml('#16383A')), ([System.Drawing.ColorTranslator]::FromHtml('#0B1D1F'))
  $g.FillPath($lg, $back)
  if ($s -ge 32) {
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 63, 221, 178)), ([Math]::Max(1, $s / 64))
    $g.DrawPath($pen, $back)
  }
  # "Dock": a screen outline on the left, a docked column of three module cards on the right.
  $mintColor = [System.Drawing.ColorTranslator]::FromHtml('#3FDDB2')
  $lw = [Math]::Max(1.2, $s * 0.05)
  $pen2 = New-Object System.Drawing.Pen $mintColor, $lw
  $screen = Round ($s * 0.17) ($s * 0.2) ($s * 0.27) ($s * 0.6) ($s * 0.06)
  $g.DrawPath($pen2, $screen)
  $alphas = 255, 170, 100
  $ch = $s * 0.16; $cg = $s * 0.06
  for ($i = 0; $i -lt 3; $i++) {
    $br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alphas[$i], $mintColor))
    $card = Round ($s * 0.54) ($s * 0.2 + $i * ($ch + $cg)) ($s * 0.29) $ch ($s * 0.05)
    $g.FillPath($br, $card)
  }
  $g.Dispose(); $bmp
}

$png = foreach ($s in $sizes) {
  $b = New-Frame $s; $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  , @($s, $ms.ToArray())
}
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$png.Count)
$offset = 6 + 16 * $png.Count
foreach ($f in $png) {
  $s = $f[0]; $data = $f[1]
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset); $offset += $data.Length
}
foreach ($f in $png) { $bw.Write([byte[]]$f[1]) }
$bw.Close(); $fs.Close()
"wrote $out"
