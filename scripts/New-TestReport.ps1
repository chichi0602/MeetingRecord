# 把 dotnet test 的 .trx 彙整成測試報告（1.0.61，格式見 docs/planning/09-Blazor-WASM-遷移評估與測試策略.md 第九節）。
#
# M0 產出「基準報告」：只有 Server 欄；WASM 遷移完成後再跑一次，把 WASM 的 e2e.trx 用 -WasmE2ETrx 帶進來就是並列的最終報告。
# 情境編號、標題、人物取自端對端測試原始碼上的 [Trait("Scenario"/"Title"/"Persona", ...)]。
#
# 遷移分支的里程碑報告（1.1.3）：-Milestone M1（M2…）只換標題與檔名（「M1 里程碑報告」「…-M1-里程碑測試報告-版本.md」），內容同一套。
#
# 用法（通常由 Run-E2E.ps1 呼叫）：
#   pwsh scripts/New-TestReport.ps1 -UnitTrx unit.trx -E2ETrx e2e.trx [-WasmE2ETrx wasm-e2e.trx] [-Milestone M1] [-L5Json l5-build.json] [-OutputPath 檔案]
#
# L5（1.1.50）：端對端 Scenario 以 L5- 開頭的情境自成一層；它們在測試輸出寫「L5METRIC|項目|門檻|實測|判定」，
# 與 -L5Json（Run-E2E.ps1 發佈後統計的下載量、修剪警告）一起組成第五節。
param(
    [string]$UnitTrx,
    [Parameter(Mandatory = $true)][string]$E2ETrx,
    [string]$WasmE2ETrx,
    [string]$Milestone = "M0",
    [string]$L5Json,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionRoot = Join-Path $repoRoot "src/MeetingRecord"

function Read-Trx([string]$path) {
    if ([string]::IsNullOrEmpty($path) -or -not (Test-Path $path)) { return @() }
    [xml]$xml = Get-Content -Raw -Encoding UTF8 $path
    $ns = @{ t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010" }
    Select-Xml -Xml $xml -XPath "//t:UnitTestResult" -Namespace $ns | ForEach-Object {
        $node = $_.Node
        $message = ""
        $err = $node.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        if ($err) { $message = ($err.InnerText -split "`n")[0].Trim() }
        $stdout = $node.SelectSingleNode("*[local-name()='Output']/*[local-name()='StdOut']")
        [pscustomobject]@{
            Name     = $node.testName
            Outcome  = $node.outcome
            Duration = $node.duration
            Message  = $message
            StdOut   = if ($stdout) { $stdout.InnerText } else { "" }
        }
    }
}

function Count-Outcomes($results) {
    [pscustomobject]@{
        Total   = @($results).Count
        Passed  = @($results | Where-Object Outcome -eq "Passed").Count
        Failed  = @($results | Where-Object Outcome -eq "Failed").Count
        Skipped = @($results | Where-Object { $_.Outcome -ne "Passed" -and $_.Outcome -ne "Failed" }).Count
    }
}

function Mark($outcome) {
    switch ($outcome) {
        "Passed" { "✅ 通過" }
        "Failed" { "❌ 失敗" }
        $null { "—" }
        default { "⏭️ 未執行" }
    }
}

# 端對端情境的中繼資料：掃原始碼上的 [Trait(...)]，對到「類別.方法」。
$scenarioMeta = @{}
Get-ChildItem (Join-Path $solutionRoot "MeetingRecord.E2ETests/Scenarios") -Filter "*.cs" -Recurse | ForEach-Object {
    $text = Get-Content -Raw -Encoding UTF8 $_.FullName
    $class = [regex]::Match($text, "class\s+(\w+)").Groups[1].Value
    $pattern = '(?s)((?:\[[^\]]*\]\s*)+)public\s+async\s+Task\s+(\w+)\s*\('
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $attrs = $m.Groups[1].Value
        $trait = { param($key) ([regex]::Match($attrs, "Trait\(""$key"",\s*""([^""]*)""\)")).Groups[1].Value }
        $scenarioMeta["$class.$($m.Groups[2].Value)"] = [pscustomobject]@{
            Scenario = & $trait "Scenario"
            Title    = & $trait "Title"
            Persona  = & $trait "Persona"
            Page     = & $trait "Page"
        }
    }
}

function Get-MethodKey([string]$testName) {
    $bare = ($testName -split "\(")[0]
    $parts = $bare.Split(".")
    "$($parts[-2]).$($parts[-1])"
}

function Get-Argument([string]$testName) {
    $m = [regex]::Match($testName, "\((.*)\)$")
    if ($m.Success) { $m.Groups[1].Value } else { "" }
}

$unit = @(Read-Trx $UnitTrx)
$e2e = @(Read-Trx $E2ETrx)
$wasm = @(Read-Trx $WasmE2ETrx)
$hasWasm = $wasm.Count -gt 0

$apiClasses = "ApiIntegrationTests|ApiAuthorizationConventionTests|GoLiveReviewApiTests|ApiResultTests|JwtTokenServiceTests"
$l2 = @($unit | Where-Object { $_.Name -match "\.($apiClasses)\." })
$l1 = @($unit | Where-Object { $_.Name -notmatch "\.($apiClasses)\." })

$version = ((Get-Content -Raw -Encoding UTF8 (Join-Path $solutionRoot "MeetingRecord.Web/appsettings.json") | ConvertFrom-Json).SystemSettings.SystemInformation.SystemVersion)
$versionNumber = ($version -split " ")[0]
$commit = (git -C $repoRoot rev-parse --short HEAD).Trim()
$dirty = if ((git -C $repoRoot status --porcelain)) { "（工作目錄有未提交的修改）" } else { "" }
$today = Get-Date -Format "yyyy/MM/dd"
$edgeVersion = try { (Get-Item "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe").VersionInfo.ProductVersion } catch { "未知" }
$dotnetVersion = (dotnet --version).Trim()

# 端對端結果再依情境編號分層：L4- 開頭是「即時與長時間」（1.0.66），L5- 是非功能（1.1.50），其餘是 L3 對照。
$scenarioOf = { param($r) $meta = $scenarioMeta[(Get-MethodKey $r.Name)]; if ($meta) { $meta.Scenario } else { "" } }
$l4 = @($e2e | Where-Object { (& $scenarioOf $_) -like "L4-*" })
$l5 = @($e2e | Where-Object { (& $scenarioOf $_) -like "L5-*" })
$l3 = @($e2e | Where-Object { (& $scenarioOf $_) -notlike "L4-*" -and (& $scenarioOf $_) -notlike "L5-*" })

$counts = [ordered]@{
    "L1 單元／服務" = Count-Outcomes $l1
    "L2 API 整合"  = Count-Outcomes $l2
    "L3 E2E 對照"  = Count-Outcomes $l3
    "L4 即時與長時間" = Count-Outcomes $l4
}
if ($l5.Count -gt 0) { $counts["L5 非功能"] = Count-Outcomes $l5 }
$failedTotal = ($counts.Values | Measure-Object -Property Failed -Sum).Sum
$lastLayer = if ($l5.Count -gt 0) { "L5" } else { "L4" }
$conclusion = if ($failedTotal -eq 0) { "✅ 通過（L1～$lastLayer 全部通過）" } else { "❌ 不通過（$failedTotal 則失敗）" }
$kind = if ($hasWasm) { "最終報告（Server 與 WASM 並列）" } elseif ($Milestone -eq "M0") { "M0 基準報告（只有 Server 版）" } else { "$Milestone 里程碑報告（Server 版＋已遷移的 WASM 頁）" }

$sb = [System.Text.StringBuilder]::new()
function Line([string]$text = "") { [void]$sb.AppendLine($text) }

Line "# Blazor WASM 遷移測試報告：$kind"
Line
Line "- 文件版本：1.0"
Line "- 文件狀態：已產出（由 scripts/New-TestReport.ps1 自動產生，第六、七節人工填寫）"
Line "- 現行系統版本：$versionNumber"
Line "- 首次實作版本：1.0.61"
Line "- 最後核對日期：$today"
Line
Line "> 測試策略與完成門檻見 [09-Blazor-WASM-遷移評估與測試策略](../planning/09-Blazor-WASM-遷移評估與測試策略.md) 第八、九節。"
Line
Line "## 1. 摘要"
Line
Line "| 項目 | 內容 |"
Line "|------|------|"
Line "| 結論 | $conclusion |"
Line "| Server 版本 | $version，commit ``$commit``$dirty |"
Line "| WASM 版本 | $(if ($hasWasm) { '見 WASM 端 .trx' } else { '—（尚未遷移）' }) |"
Line "| 測試日期 | $today |"
Line "| 執行環境 | Windows $([Environment]::OSVersion.Version)、.NET SDK $dotnetVersion、Microsoft Edge $edgeVersion（Playwright 無頭模式） |"
Line "| 網站與資料 | 以 publish 輸出、E2E 環境啟動（假 AI，不呼叫付費 API）；獨立 SQLite 與臨時資料夾，種子資料見 ``E2ESeeder`` |"
Line
Line "## 2. 各層結果"
Line
Line "| 層級 | 案例數 | 通過 | 失敗 | 略過 |"
Line "|------|--------|------|------|------|"
foreach ($key in $counts.Keys) {
    $c = $counts[$key]
    Line "| $key | $($c.Total) | $($c.Passed) | $($c.Failed) | $($c.Skipped) |"
}
if ($l5.Count -eq 0) { Line "| L5 非功能 | — | — | — | 本次未執行 |" }
Line
Line "## 3. E2E 對照矩陣"
Line
Line "| 情境 | 說明 | 頁面 | 人物 | Server | WASM | 備註 |"
Line "|------|------|------|------|--------|------|------|"
$wasmByName = @{}
foreach ($r in $wasm) { $wasmByName[(Get-MethodKey $r.Name) + "|" + (Get-Argument $r.Name)] = $r }
$rows = foreach ($r in $e2e) {
    $key = Get-MethodKey $r.Name
    $meta = $scenarioMeta[$key]
    $arg = Get-Argument $r.Name
    [pscustomobject]@{
        Scenario = if ($meta -and $meta.Scenario) { $meta.Scenario } else { $key }
        Title    = (@($meta.Title, $arg) | Where-Object { $_ }) -join "："
        Page     = $meta.Page
        Persona  = $meta.Persona
        Server   = Mark $r.Outcome
        Wasm     = if ($hasWasm) { Mark $wasmByName["$key|$arg"].Outcome } else { "—" }
        Note     = if ($r.Outcome -eq "Failed") { $r.Message -replace "\|", "／" } else { "" }
    }
}
foreach ($row in ($rows | Sort-Object Scenario, Title)) {
    Line "| $($row.Scenario) | $($row.Title) | $($row.Page) | $($row.Persona) | $($row.Server) | $($row.Wasm) | $($row.Note) |"
}
Line
Line "## 4. 授權矩陣（L2，API 端點）"
Line
Line "每支端點三件事：沒權杖 401；角色有該頁其他所有動作、唯獨缺這一個時 403（並留稽核）；角色只有這一個動作時成功。"
Line
Line "| 端點 | 未登入 401 | 缺動作權限 403 | 只有該動作成功 |"
Line "|------|-----------|----------------|----------------|"
$authz = @{}
foreach ($r in $l2 | Where-Object { $_.Name -match "GuardedEndpoint_" }) {
    $endpoint = [regex]::Match($r.Name, 'endpoint:\s*"([^"]+)"').Groups[1].Value
    if (-not $authz.ContainsKey($endpoint)) { $authz[$endpoint] = @{} }
    if ($r.Name -match "WithoutToken") { $authz[$endpoint]["401"] = $r.Outcome }
    elseif ($r.Name -match "MissingOnly") { $authz[$endpoint]["403"] = $r.Outcome }
    elseif ($r.Name -match "WithOnly") { $authz[$endpoint]["ok"] = $r.Outcome }
}
foreach ($endpoint in ($authz.Keys | Sort-Object)) {
    $e = $authz[$endpoint]
    Line "| $endpoint | $(Mark $e['401']) | $(Mark $e['403']) | $(Mark $e['ok']) |"
}
$convention = $l2 | Where-Object { $_.Name -match "ApiAuthorizationConventionTests" }
Line
Line "授權守門掃描（``ApiAuthorizationConventionTests``，每支端點都要有授權屬性）：$(if (@($convention | Where-Object Outcome -ne 'Passed').Count -eq 0 -and @($convention).Count -gt 0) { '✅ 通過' } else { '❌ 未通過或未執行' })"
Line
Line "## 5. 效能（L5）"
Line
$l5Metrics = @($l5 | ForEach-Object { $_.StdOut -split "`r?`n" } | Where-Object { $_ -like "L5METRIC|*" } | ForEach-Object { $p = $_.Split("|"); [pscustomobject]@{ Item = $p[1]; Threshold = $p[2]; Actual = $p[3]; Verdict = $p[4] } })
$build = if (-not [string]::IsNullOrEmpty($L5Json) -and (Test-Path $L5Json)) { Get-Content -Raw -Encoding UTF8 $L5Json | ConvertFrom-Json } else { $null }
if ($l5Metrics.Count -eq 0 -and $null -eq $build) {
    Line "本次未量測。門檻見 09 號文件 §8 L5：首次下載量（Brotli 後 ≤ 5 MB）、冷啟動（桌機 ≤ 4 秒、有快取 ≤ 1.5 秒）、API p95（≤ 300 ms）、伺服器記憶體、越權與 CSRF、修剪警告為 0。"
}
else {
    Line "門檻見 09 號文件 §8 L5。下載量與修剪警告由 ``Run-E2E.ps1`` 在發佈後統計；載入時間、API p95、記憶體由端對端 ``L5-*`` 情境量測（載入時間在頁面裡從導覽開始量到登入者名稱出現）；越權與 CSRF 看 L2。"
    Line
    Line "| 項目 | 門檻 | 實測 | 判定 |"
    Line "|------|------|------|------|"
    if ($build) {
        Line "| WASM 首次下載量（Brotli，只下載一個語系檔） | ≤ 5 MB | $($build.downloadOneLocaleMb) MB（全部 ``.br`` 合計 $($build.downloadTotalMb) MB） | $(if ($build.downloadOneLocaleMb -le 5) { '✅' } else { '❌' }) |"
        Line "| 發佈時的修剪警告 | 0 | $($build.trimWarnings) 則 | $(if ($build.trimWarnings -eq 0) { '✅' } else { '❌' }) |"
    }
    foreach ($m in $l5Metrics) {
        Line "| $($m.Item) | $($m.Threshold) | $($m.Actual) | $($m.Verdict) |"
    }
    $authzOk = @($unit | Where-Object { $_.Name -match "ApiAuthorizationConventionTests|GuardedEndpoint_" -and $_.Outcome -ne "Passed" }).Count -eq 0 -and @($unit | Where-Object { $_.Name -match "GuardedEndpoint_" }).Count -gt 0
    $csrf = @($unit | Where-Object { $_.Name -match "CookieWrite_WithoutClientHeader_ShouldBeRejectedAsCsrf" })
    $csrfOk = $csrf.Count -gt 0 -and @($csrf | Where-Object Outcome -ne "Passed").Count -eq 0
    Line "| 越權（跨使用者、無權限、非管理者打管理 API） | 一律擋下 | L2 授權矩陣與守門掃描 | $(if ($authzOk) { '✅' } else { '❌ 或未執行' }) |"
    Line "| CSRF（Cookie 寫入沒帶防偽標頭） | 擋下 | ``CookieApiTests`` | $(if ($csrfOk) { '✅' } else { '❌ 或未執行' }) |"
}
Line
Line "## 6. 已知且接受的差異"
Line
Line "| 差異 | 影響 | 接受理由 | 決定者／日期 |"
Line "|------|------|----------|--------------|"
Line "| （人工填寫） | | | |"
Line
Line "## 7. 人工驗收（L6）與簽核"
Line
Line "| 項目 | 結果 | 驗收者／日期 |"
Line "|------|------|--------------|"
foreach ($item in "AntDesign 樣式與覆寫", "元件樣式（scoped CSS）", "全站字型", "HoverTip 提示", "Material Icons 圖示", "側邊欄三層浮出選單", "對話窗尺寸", "圖層順序（z-index）") {
    Line "| $item | （人工填寫） | |"
}
Line
if ($failedTotal -gt 0) {
    Line "## 附錄：失敗的測試"
    Line
    foreach ($r in @($unit + $e2e) | Where-Object Outcome -eq "Failed") {
        Line "- ``$($r.Name)``：$($r.Message)"
    }
    Line
}

if ([string]::IsNullOrEmpty($OutputPath)) {
    $label = if ($hasWasm) { "WASM遷移最終測試報告" } elseif ($Milestone -eq "M0") { "M0-基準測試報告" } else { "$Milestone-里程碑測試報告" }
    $OutputPath = Join-Path $repoRoot ("docs/testing/" + (Get-Date -Format "yyyy-MM-dd") + "-$label-$versionNumber.md")
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
# docs 下的 Markdown 一律 UTF-8 含 BOM（scripts/Test-DocsEncoding.ps1 會檢查）。
[System.IO.File]::WriteAllText($OutputPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($true))
Write-Host "測試報告：$OutputPath"
