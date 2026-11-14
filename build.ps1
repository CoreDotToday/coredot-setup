# 코어닷 관리센터 빌드 → dist\CoreDotSetup.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1
# 필요한 것: 윈도우 기본 .NET Framework 4 (csc.exe) — 따로 설치할 것 없음
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe 를 찾지 못했습니다 (.NET Framework 4 필요)' }
New-Item -ItemType Directory -Force $dist | Out-Null

$out = Join-Path $dist 'CoreDotSetup.exe'
$refs = 'System.dll', 'System.Core.dll', 'Microsoft.CSharp.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Web.Extensions.dll', 'System.Management.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll' |
        ForEach-Object { "/r:$_" }
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
$cargs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
           "/win32manifest:$(Join-Path $src 'app.manifest')", "/out:$out") + $refs + $files
& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw '컴파일 실패' }
$ver = [regex]::Match((Get-Content (Join-Path $src 'Program.cs') -Raw -Encoding UTF8), 'Version = "([^"]+)"').Groups[1].Value
Write-Host ("완료: {0}  ({1:N0} KB, 관리센터 {2})" -f $out, ((Get-Item $out).Length / 1KB), $ver)
