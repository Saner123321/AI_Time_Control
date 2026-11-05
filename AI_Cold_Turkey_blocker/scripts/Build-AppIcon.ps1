#Requires -Version 5.1
[CmdletBinding()] param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSizes=@(16,24,32,48,64,128,256);$taskFrames=@()
foreach($taskSize in $taskSizes){
 $taskBitmap=New-Object Drawing.Bitmap($taskSize,$taskSize)
 $taskGraphics=[Drawing.Graphics]::FromImage($taskBitmap)
 $taskPath=New-Object Drawing.Drawing2D.GraphicsPath
 $taskFill=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(25,125,131))
 $taskPen=New-Object Drawing.Pen([Drawing.Color]::White,2.3)
 $taskStream=New-Object IO.MemoryStream
 try{
  $taskGraphics.Clear([Drawing.Color]::Transparent);$taskGraphics.SmoothingMode='AntiAlias';$taskGraphics.ScaleTransform(($taskSize/32.0),($taskSize/32.0))
  $taskPath.AddArc(1,1,8,8,180,90);$taskPath.AddArc(23,1,8,8,270,90);$taskPath.AddArc(23,23,8,8,0,90);$taskPath.AddArc(1,23,8,8,90,90);$taskPath.CloseFigure()
  $taskGraphics.FillPath($taskFill,$taskPath);$taskPen.StartCap='Round';$taskPen.EndCap='Round'
  $taskGraphics.DrawEllipse($taskPen,7,7,18,18);$taskGraphics.DrawLine($taskPen,16,10,16,16);$taskGraphics.DrawLine($taskPen,16,16,21,19)
  $taskBitmap.Save($taskStream,[Drawing.Imaging.ImageFormat]::Png);$taskFrames+=,@($taskStream.ToArray())
  if($taskSize -eq 256){$taskBitmap.Save((Join-Path $taskRoot 'assets\focus-preview.png'),[Drawing.Imaging.ImageFormat]::Png)}
 }finally{$taskStream.Dispose();$taskPen.Dispose();$taskFill.Dispose();$taskPath.Dispose();$taskGraphics.Dispose();$taskBitmap.Dispose()}
}
$taskOutput=New-Object IO.MemoryStream;$taskWriter=New-Object IO.BinaryWriter($taskOutput)
try{
 $taskWriter.Write([uint16]0);$taskWriter.Write([uint16]1);$taskWriter.Write([uint16]$taskSizes.Count);$taskOffset=6+16*$taskSizes.Count
 for($taskIndex=0;$taskIndex -lt $taskSizes.Count;$taskIndex++){
  $taskDimension=if($taskSizes[$taskIndex] -eq 256){0}else{$taskSizes[$taskIndex]}
  $taskWriter.Write([byte]$taskDimension);$taskWriter.Write([byte]$taskDimension);$taskWriter.Write([byte]0);$taskWriter.Write([byte]0);$taskWriter.Write([uint16]1);$taskWriter.Write([uint16]32)
  $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length);$taskWriter.Write([uint32]$taskOffset);$taskOffset+=$taskFrames[$taskIndex].Length
 }
 foreach($taskFrame in $taskFrames){$taskWriter.Write([byte[]]$taskFrame)}
 [IO.File]::WriteAllBytes((Join-Path $taskRoot 'assets\focus.ico'),$taskOutput.ToArray())
}finally{$taskWriter.Dispose();$taskOutput.Dispose()}
Write-Host 'Created seven icon sizes from the existing teal clock design.'
