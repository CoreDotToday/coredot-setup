# tools 공용 — catalog.json 읽기 · 쓰기 (사람이 읽기 좋게 2칸 들여쓰기)
$Repo = 'CoreDotToday/coredot-setup'
$Root = Split-Path $PSScriptRoot -Parent
$CatalogPath = Join-Path $Root 'catalog.json'

function Read-Catalog {
    [IO.File]::ReadAllText($CatalogPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
}

function Format-Json([string]$json) {
    $sb = New-Object Text.StringBuilder
    $indent = 0; $inStr = $false; $esc = $false
    $chars = $json.ToCharArray()
    for ($i = 0; $i -lt $chars.Length; $i++) {
        $c = $chars[$i]
        if ($inStr) {
            [void]$sb.Append($c)
            if ($esc) { $esc = $false } elseif ($c -eq [char]'\') { $esc = $true } elseif ($c -eq [char]'"') { $inStr = $false }
            continue
        }
        if ($c -eq [char]'"') { $inStr = $true; [void]$sb.Append($c) }
        elseif ($c -eq [char]'{' -or $c -eq [char]'[') {
            $next = if ($i + 1 -lt $chars.Length) { $chars[$i + 1] } else { [char]' ' }
            if (($c -eq [char]'{' -and $next -eq [char]'}') -or ($c -eq [char]'[' -and $next -eq [char]']')) { [void]$sb.Append($c).Append($next); $i++; continue }
            $indent++; [void]$sb.Append($c).Append("`n").Append('  ' * $indent)
        }
        elseif ($c -eq [char]'}' -or $c -eq [char]']') { $indent--; [void]$sb.Append("`n").Append('  ' * $indent).Append($c) }
        elseif ($c -eq [char]',') { [void]$sb.Append($c).Append("`n").Append('  ' * $indent) }
        elseif ($c -eq [char]':') { [void]$sb.Append(': ') }
        elseif (-not [char]::IsWhiteSpace($c)) { [void]$sb.Append($c) }
    }
    $sb.ToString() + "`n"
}

function Save-Catalog($cat) {
    $json = $cat | ConvertTo-Json -Depth 20 -Compress
    # PowerShell 5.1 이 ' < > & 를 \u 로 바꾼다 — 읽기 좋게 되돌림 (JSON 으로는 같은 값)
    $json = $json -replace '\\u0027', "'" -replace '\\u003c', '<' -replace '\\u003e', '>' -replace '\\u0026', '&'
    [IO.File]::WriteAllText($CatalogPath, (Format-Json $json), (New-Object Text.UTF8Encoding $false))
    Write-Host "catalog.json 저장"
}

function Get-Sha256($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# gh 가 stderr 에 쓰는 안내('release not found' 등)를 PowerShell 5.1 이 오류로 바꿔 멈추지 않게 — 종료 코드만 본다
function Test-Gh {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & gh @args 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $old }
}

function Test-Release($tag) { Test-Gh release view $tag -R $Repo }

function Ensure-Release($tag, $title, $notes, [switch]$NotLatest) {
    if (Test-Release $tag) { return }
    $a = @('release', 'create', $tag, '-R', $Repo, '--title', $title, '--notes', $notes)
    if ($NotLatest) { $a += '--latest=false' }
    & gh @a
    if ($LASTEXITCODE -ne 0) { throw "릴리스 $tag 를 만들지 못했습니다" }
}

function Get-AssetNames($tag) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $names = gh release view $tag -R $Repo --json assets -q '.assets[].name' 2>$null
        if ($LASTEXITCODE -ne 0) { return @() }
        return @($names)
    }
    finally { $ErrorActionPreference = $old }
}
