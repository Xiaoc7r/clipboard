$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'C# compiler not found. Install .NET Framework 4.8 Developer Pack.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:"$root\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll /out:"$dist\PicoPaste.exe" "$root\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Host "Build complete: $dist\PicoPaste.exe"
