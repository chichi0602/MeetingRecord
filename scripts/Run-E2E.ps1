# 執行端對端（E2E）測試並產生基準測試報告（1.0.61，WASM 遷移前置工作 M0）。
#
# 流程：publish MeetingRecord.Web → 跑單元／服務／API 測試（L1、L2）→ 跑 Playwright 端對端測試（L3）
#       → 以 New-TestReport.ps1 彙整兩份 .trx 成 docs/testing/ 下的 Markdown 報告。
#
# 需求：本機已安裝 Microsoft Edge（Playwright 以 msedge channel 驅動，不另外下載瀏覽器）。
# 網站以 E2E 環境啟動：假 AI（不會打到付費 API）、臨時資料夾、獨立資料庫，跑完自動刪除。
#
# 用法：
#   pwsh scripts/Run-E2E.ps1                     # 全部跑完並產生報告
#   pwsh scripts/Run-E2E.ps1 -Filter "LGN"       # 只跑情境編號含 LGN 的端對端測試，不產生報告
#   pwsh scripts/Run-E2E.ps1 -SkipUnitTests      # 只跑端對端測試
#   pwsh scripts/Run-E2E.ps1 -Headed             # 開出瀏覽器視窗看它操作
#   pwsh scripts/Run-E2E.ps1 -Milestone M1       # 遷移分支的里程碑報告（1.1.3），檔名「…-M1-里程碑測試報告-版本.md」
#
# L5（1.1.50）：發佈後統計 WASM 下載量與修剪警告（results/l5-build.json），端對端 L5-* 情境量載入時間、API p95、伺服器記憶體，
# 一併寫進報告第五節。
param(
    [string]$Filter = "",
    [switch]$SkipUnitTests,
    [switch]$Headed,
    [switch]$NoReport,
    [string]$Milestone = "M0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionRoot = Join-Path $repoRoot "src/MeetingRecord"
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MeetingRecordE2ERun-" + (Get-Date -Format "yyyyMMddHHmmss"))
$publishPath = Join-Path $runRoot "publish"
$resultsPath = Join-Path $runRoot "results"
New-Item -ItemType Directory -Force -Path $publishPath, $resultsPath | Out-Null

Write-Host "== 發佈 MeetingRecord.Web 到 $publishPath"
$publishOutput = dotnet publish (Join-Path $solutionRoot "MeetingRecord.Web/MeetingRecord.Web.csproj") -c Release -o $publishPath --nologo -v q 2>&1
$publishExit = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExit -ne 0) { throw "dotnet publish 失敗。" }

# L5（1.1.50）：WASM 下載量（_framework 的 Brotli 檔；ICU 語系檔只會下載其中一個，以最大的那個計）與修剪警告數。
$framework = Join-Path $publishPath "wwwroot/_framework"
$brotli = @(Get-ChildItem $framework -Filter "*.br" -ErrorAction SilentlyContinue)
$icu = @($brotli | Where-Object Name -like "icudt*")
$totalBytes = ($brotli | Measure-Object Length -Sum).Sum
$oneLocaleBytes = $totalBytes - ($icu | Measure-Object Length -Sum).Sum + ($icu | Measure-Object Length -Maximum).Maximum
$trimWarnings = @($publishOutput | Select-String -Pattern "warning IL\d{4}" | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique).Count
$l5BuildJson = Join-Path $resultsPath "l5-build.json"
[ordered]@{
    downloadTotalMb     = [math]::Round($totalBytes / 1MB, 2)
    downloadOneLocaleMb = [math]::Round($oneLocaleBytes / 1MB, 2)
    trimWarnings        = $trimWarnings
} | ConvertTo-Json | Set-Content -Encoding utf8 $l5BuildJson
Write-Host ("== L5：WASM 下載量（Brotli）全部 {0:N2} MB、只下載一個語系檔 {1:N2} MB；修剪警告 {2} 則" -f ($totalBytes / 1MB), ($oneLocaleBytes / 1MB), $trimWarnings)

$unitTrx = $null
if (-not $SkipUnitTests -and [string]::IsNullOrEmpty($Filter)) {
    Write-Host "== L1／L2：單元、服務與 API 測試"
    dotnet test (Join-Path $solutionRoot "MeetingRecord.Tests/MeetingRecord.Tests.csproj") -c Release --nologo `
        --logger "trx;LogFileName=unit.trx" --results-directory $resultsPath
    $unitExit = $LASTEXITCODE
    $unitTrx = Join-Path $resultsPath "unit.trx"
}

Write-Host "== L3：端對端測試（Playwright／Edge）"
$env:MEETINGRECORD_E2E = "1"
$env:MEETINGRECORD_E2E_APP = $publishPath
if ($Headed) { $env:E2E_HEADED = "1" }
$e2eArgs = @((Join-Path $solutionRoot "MeetingRecord.E2ETests/MeetingRecord.E2ETests.csproj"), "-c", "Release", "--nologo",
    "--logger", "trx;LogFileName=e2e.trx", "--results-directory", $resultsPath)
if (-not [string]::IsNullOrEmpty($Filter)) {
    $e2eArgs += @("--filter", "Scenario~$Filter")
}
try {
    dotnet test @e2eArgs
    $e2eExit = $LASTEXITCODE
}
finally {
    Remove-Item Env:MEETINGRECORD_E2E, Env:MEETINGRECORD_E2E_APP, Env:E2E_HEADED -ErrorAction SilentlyContinue
}
$e2eTrx = Join-Path $resultsPath "e2e.trx"

if (-not $NoReport -and [string]::IsNullOrEmpty($Filter)) {
    Write-Host "== 產生測試報告"
    & (Join-Path $PSScriptRoot "New-TestReport.ps1") -UnitTrx $unitTrx -E2ETrx $e2eTrx -Milestone $Milestone -L5Json $l5BuildJson
}

Write-Host "測試結果（.trx）：$resultsPath"
if (($unitExit -and $unitExit -ne 0) -or $e2eExit -ne 0) {
    throw "有測試失敗，詳見上方輸出與 $resultsPath。"
}
