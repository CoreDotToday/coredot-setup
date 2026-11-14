# 드라이버 · 프로그램 설치 파일을 묶어 Releases('packages')에 올리고 catalog.json 을 채운다.
#   powershell -ExecutionPolicy Bypass -File tools\publish-packages.ps1            # 묶기 + catalog.json 채우기 (올리지는 않음)
#   powershell -ExecutionPolicy Bypass -File tools\publish-packages.ps1 -Upload    # + GitHub 에 올리기 (gh 로그인 필요)
# 그다음 catalog.json 을 커밋 · 푸시하면 모든 PC 의 관리센터가 3시간 안에 알림을 띄운다.
#
# 원본 자리 (git 에는 안 올라감):
#   drivers\<id>\   드라이버 폴더 (INF 포함)   — 드라이버가 깔린 PC 에서 tools\export-drivers.ps1 로 꺼낼 수 있음
#   extras\<파일>   설치 프로그램 exe            — catalog.json 의 "source" 가 가리키는 파일
param([switch]$Upload)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

$tag = 'packages'
$pkgDir = Join-Path $Root 'packages'
New-Item -ItemType Directory -Force $pkgDir | Out-Null
$base = "https://github.com/$Repo/releases/download/$tag"

if ($Upload) {
    Ensure-Release $tag '설치 파일 (드라이버 · 프로그램)' "코어닷 관리센터가 받아 가는 설치 파일들입니다. catalog.json 이 이 파일들을 가리킵니다 — 지우지 마세요." -NotLatest
    $have = Get-AssetNames $tag
}

$cat = Read-Catalog
foreach ($it in $cat.items) {
    $src = Join-Path $Root $it.source
    if (-not (Test-Path $src)) { Write-Warning "$($it.id): 원본이 없습니다 ($($it.source)) — 건너뜀"; continue }

    if ($it.kind -eq 'driver') {
        $inf = Join-Path $src $it.inf
        if (-not (Test-Path $inf)) { throw "$($it.id): $($it.inf) 가 $src 에 없습니다" }
        $m = [regex]::Match((Get-Content $inf -Raw), 'DriverVer\s*=\s*[^,\r\n]*,\s*([0-9][0-9.]*)', 'IgnoreCase')
        if (-not $m.Success) { throw "$($it.id): INF 에서 DriverVer 를 찾지 못했습니다" }
        $ver = $m.Groups[1].Value
        $file = "$($it.id)-$ver.zip"
        $path = Join-Path $pkgDir $file
        if (-not (Test-Path $path)) {
            Write-Host "묶는 중: $file"
            [IO.Compression.ZipFile]::CreateFromDirectory($src, $path, [IO.Compression.CompressionLevel]::Optimal, $false)
        }
    }
    else {
        $ver = (Get-Item $src).VersionInfo.ProductVersion
        if (-not $ver) { throw "$($it.id): exe 에 판 번호가 없습니다 — catalog.json 의 version 을 직접 적고 파일 이름에 넣으세요" }
        $ver = $ver.Trim()
        $file = "$($it.id)-$ver" + [IO.Path]::GetExtension($src)
        $path = Join-Path $pkgDir $file
        if (-not (Test-Path $path)) { Copy-Item $src $path }
    }

    $it.version = $ver
    $it.url = "$base/$file"
    $it.sha256 = Get-Sha256 $path
    $it.size = (Get-Item $path).Length
    Write-Host ("{0,-14} {1,-14} {2,8:N1} MB  {3}" -f $it.id, $ver, ($it.size / 1MB), $file)

    if ($Upload -and ($have -notcontains $file)) {
        Write-Host "  올리는 중…"
        gh release upload $tag $path -R $Repo
        if ($LASTEXITCODE -ne 0) { throw "$file 올리기 실패" }
    }
}
Save-Catalog $cat
if (-not $Upload) { Write-Host "올리지 않았습니다 — 확인 후 -Upload 로 다시 실행하세요" }
else { Write-Host "다 올렸습니다 — 이제 catalog.json 을 커밋 · 푸시하세요" }
