# 檢查 docs/ 下所有 .md（遞迴）都是 UTF-8 含 BOM、而且沒有亂碼（U+FFFD 替代字元）；有任何一份不合格就失敗。
# CI（.github/workflows/dotnet-ci.yml）每次都會跑。
#
# 用法（在哪個目錄執行都可以）：
#   pwsh scripts/Test-DocsEncoding.ps1
#   pwsh scripts/Test-DocsEncoding.ps1 -DocsPath 其他資料夾
param(
    [string]$DocsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "docs")
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path $DocsPath
$failed = $false

Get-ChildItem -Path $root -Filter "*.md" -File -Recurse | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $hasReplacementCharacter = $text.Contains([char]0xFFFD)

    if (-not $hasBom -or $hasReplacementCharacter) {
        $failed = $true
        Write-Host "FAILED $($_.FullName): BOM=$hasBom ReplacementCharacter=$hasReplacementCharacter"
    }
    else {
        Write-Host "OK $($_.FullName): BOM=$hasBom ReplacementCharacter=$hasReplacementCharacter"
    }
}

if ($failed) {
    throw "Documentation encoding check failed."
}
