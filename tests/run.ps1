$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$test = Join-Path $dist 'LocalStoreTests.exe'
& $compiler /nologo /target:exe /main:PicoPaste.LocalStoreTests /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll "/out:$test" "$root\src\Program.cs" "$PSScriptRoot\LocalStoreTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Persistence tests failed' }
