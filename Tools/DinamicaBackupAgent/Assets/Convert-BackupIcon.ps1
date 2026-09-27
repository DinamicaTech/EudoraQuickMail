param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
try {
 foreach ($size in $sizes) {
  $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $stream = [System.IO.MemoryStream]::new()
  try {
   $graphics.Clear([System.Drawing.Color]::Transparent)
   $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $graphics.DrawImage($sourceImage,0,0,$size,$size)
   $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
   $frames.Add($stream.ToArray())
  } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
 }
 $output = [System.IO.File]::Open($Destination,[System.IO.FileMode]::CreateNew)
 $writer = [System.IO.BinaryWriter]::new($output)
 try {
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i=0; $i -lt $sizes.Count; $i++) {
   $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
   $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
   $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
   $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
   $offset += $frames[$i].Length
  }
  foreach ($frame in $frames) { $writer.Write($frame) }
 } finally { $writer.Dispose(); $output.Dispose() }
} finally { $sourceImage.Dispose() }
