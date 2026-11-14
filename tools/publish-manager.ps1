# 관리센터 새 판 내기: 빌드 → Releases 'v<판>' 에 CoreDotSetup.exe 올리기 → catalog.json 의 manager 갱신
#   1) src\Program.cs 의 Version 을 올린다
#   2) powershell -ExecutionPolicy Bypass -File tools\publish-manager.ps1 -Notes "바뀐 점"
#   3) catalog.json 을 커밋 · 푸시 → 모든 PC 의 관리센터가 스스로 새 판으로 바뀐다
param([string]$Notes = '', [switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '빌드 실패' }
$ver = [regex]::Match([IO.File]::ReadAllText((Join-Path $Root 'src\Program.cs'), [Text.Encoding]::UTF8), 'Version = "([^"]+)"').Groups[1].Value
$tag = "v$ver"
$exe = Join-Path $Root 'dist\CoreDotSetup.exe'

gh release view $tag -R $Repo *> $null
if ($LASTEXITCODE -eq 0) {
    if (-not $Force) { throw "릴리스 $tag 가 이미 있습니다 — src\Program.cs 의 Version 을 올리세요 (같은 판을 덮어쓰려면 -Force)" }
    gh release upload $tag $exe -R $Repo --clobber
} else {
    if (-not $Notes) { $Notes = "코어닷 관리센터 $ver" }
    gh release create $tag $exe -R $Repo --title "코어닷 관리센터 $ver" --notes $Notes --latest
}
if ($LASTEXITCODE -ne 0) { throw '올리기 실패' }

$cat = Read-Catalog
$cat.manager.version = $ver
$cat.manager.url = "https://github.com/$Repo/releases/download/$tag/CoreDotSetup.exe"
$cat.manager.sha256 = Get-Sha256 $exe
$cat.manager.size = (Get-Item $exe).Length
Save-Catalog $cat
Write-Host "관리센터 $ver 를 올렸습니다 — 이제 catalog.json 을 커밋 · 푸시하세요"
Write-Host "새 PC 설치용 주소: https://github.com/$Repo/releases/latest/download/CoreDotSetup.exe"
