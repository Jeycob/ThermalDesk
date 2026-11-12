$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path $PSScriptRoot '../src/ThermalDesk.App/Resources/ThermalDesk.ico'
New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
$images = @()
foreach ($size in 16,24,32,48,64,128,256) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.Color]::Transparent)
    $g.ScaleTransform($size/256.0,$size/256.0)
    $dark = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#102332'))
    $cyan = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#31D5E9'),19)
    $cyan.StartCap = $cyan.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $gold = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#F5B840'),13)
    $gold.StartCap = $gold.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $white = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#EFFAFF'))
    try {
        $g.FillEllipse($dark,4,4,248,248)
        $g.DrawArc($cyan,26,26,204,204,130,215)
        $g.DrawArc($gold,26,26,204,204,353,55)
        $g.DrawLine($cyan,128,65,128,157)
        $g.FillEllipse($white,99,143,58,58)
        $g.DrawLine($gold,160,92,179,92)
        $g.DrawLine($gold,160,123,173,123)
        $stream = [IO.MemoryStream]::new()
        try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $images += ,$stream.ToArray() } finally { $stream.Dispose() }
    } finally { $dark.Dispose(); $cyan.Dispose(); $gold.Dispose(); $white.Dispose(); $g.Dispose(); $bitmap.Dispose() }
}
$file = [IO.File]::Create([IO.Path]::GetFullPath($target))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    $index = 0
    foreach ($size in 16,24,32,48,64,128,256) {
        $dimension = if($size -eq 256){0}else{$size}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length; $index++
    }
    foreach($png in $images){$writer.Write([byte[]]$png)}
} finally { $writer.Dispose(); $file.Dispose() }
Write-Host "Created $target"
