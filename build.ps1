param([string]$Destination = 'release')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework 4.8 is required.' }
$outputDir = if ([System.IO.Path]::IsPathRooted($Destination)) { [System.IO.Path]::GetFullPath($Destination) } else { Join-Path $taskRoot $Destination }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$taskBitmap = New-Object System.Drawing.Bitmap(64,64)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskGraphics.Clear([System.Drawing.Color]::FromArgb(15,25,36))
$taskColors = @('#51DCCA','#F273BF','#55C8F3','#ECA5FF')
for ($taskIndex = 0; $taskIndex -lt 4; $taskIndex++) {
    $taskBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($taskColors[$taskIndex]))
    $taskGraphics.FillRectangle($taskBrush, (9 + $taskIndex * 12), (10 + $taskIndex * 5), 8, (32 - $taskIndex * 5))
    $taskGraphics.FillRectangle($taskBrush, (9 + $taskIndex * 12), 47, 8, 8)
    $taskBrush.Dispose()
}
$taskIconHandle = $taskBitmap.GetHicon()
$taskIcon = [System.Drawing.Icon]::FromHandle($taskIconHandle)
$taskIconStream = [System.IO.File]::Create((Join-Path $outputDir 'KeyCascade.ico'))
$taskIcon.Save($taskIconStream)
$taskIconStream.Dispose()
$taskIcon.Dispose()
Add-Type -TypeDefinition 'public static class KeyCascadeIconNative { [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(System.IntPtr handle); }'
[KeyCascadeIconNative]::DestroyIcon($taskIconHandle) | Out-Null
$taskGraphics.Dispose()
$taskBitmap.Dispose()

$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + (Join-Path $outputDir 'KeyCascade.exe')),('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')),('/win32icon:' + (Join-Path $outputDir 'KeyCascade.ico')),('/resource:' + (Join-Path $taskRoot 'src\Settings.xaml') + ',Settings.xaml'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkDir $reference) }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'KeyCascade.exe.config') -Destination $outputDir -Force
if (Test-Path -LiteralPath (Join-Path $taskRoot 'README.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $outputDir 'README.md') -Force }
if (Test-Path -LiteralPath (Join-Path $taskRoot 'docs\performance.md')) {
    $taskDocsDir = Join-Path $outputDir 'docs'
    New-Item -ItemType Directory -Path $taskDocsDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\performance.md') -Destination $taskDocsDir -Force
}
Write-Output ('Built: ' + (Join-Path $outputDir 'KeyCascade.exe'))
