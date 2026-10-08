param([string]$Name = 'benchmark')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$outputDir = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/main:KeyCascade.PerformanceBench',('/out:' + (Join-Path $outputDir ($Name + '.exe'))),('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')),('/resource:' + (Join-Path $taskRoot 'src\Settings.xaml') + ',Settings.xaml'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkDir $reference) }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$compilerArgs += Join-Path $PSScriptRoot 'PerformanceBench.cs'
& (Join-Path $frameworkDir 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Benchmark compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'KeyCascade.exe.config') -Destination (Join-Path $outputDir ($Name + '.exe.config')) -Force
