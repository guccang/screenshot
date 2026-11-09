$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
if (!(Test-Path $compiler)) { throw '需要安装 .NET Framework 4.x' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /codepage:65001 /win32icon:Screenshot.ico /optimize+ /out:Screenshot.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll Screenshot.cs
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
} finally { Pop-Location }

