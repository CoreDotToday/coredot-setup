# 이 PC 에 깔린 드라이버를 drivers\<id>\ 로 꺼낸다 (catalog.json 의 driver 항목 · inf 기준).
# 드라이버를 새 판으로 바꿀 때: 그 PC 에 제조사 드라이버를 새로 깐 뒤 이것을 돌리고 publish-packages.ps1 -Upload.
#   powershell -ExecutionPolicy Bypass -File tools\export-drivers.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$store = Join-Path $env:WINDIR 'System32\DriverStore\FileRepository'
foreach ($it in (Read-Catalog).items | Where-Object { $_.kind -eq 'driver' }) {
    $pkg = Get-ChildItem $store -Directory -Filter ($it.inf + '_amd64_*') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $pkg) { Write-Warning "$($it.id): 이 PC 에 $($it.inf) 가 없습니다 — 건너뜀"; continue }
    $dst = Join-Path $Root $it.source
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Copy-Item $pkg.FullName $dst -Recurse
    Write-Host ("{0,-14} <- {1}" -f $it.id, $pkg.Name)
}
