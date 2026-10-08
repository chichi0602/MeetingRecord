# AI 用量分析 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.80
- 最後核對日期：2026/10/08

## 一、目標與範圍

讓管理者看得到「AI 花了多少錢、花在哪裡、誰用的」。系統在 0.4.80 之前**完全沒有記錄任何用量**，所以本能力分成兩半：先在每一個會付費的 AI 呼叫點寫進**用量帳本**（`AiUsageLog`），再提供 `/ai-usage` 分析頁呈現。

- 範圍：
  - 用量帳本：一次 API 呼叫一列、永久保留；失敗與取消也記；寫入當下算好估算金額並存單價與匯率快照。
  - 模型單價設定（`LlmSettings:Pricing`，以 deployment 名稱為鍵）。
  - 美金→台幣換算：背景服務定期抓匯率（`ExchangeRateBackgroundService`），帳本存「呼叫當下」的匯率。
  - 分析頁：四張概況卡片、本月累計 vs 上月同期累計曲線、依功能別／模型／使用者分佈、最近呼叫明細（真的分頁），功能下拉連動整頁。
  - 一次性回填腳本 `scripts/Backfill-AiUsageCost.mjs`（0.4.88 人工執行過，不是自動路徑）。
- 非範圍：
  - **上限、配額、擋呼叫**——本頁純呈現，不擋任何 AI 呼叫（[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md) 只推翻了「不做計量」，上限與配額仍不做）。
  - 0.4.80 之前的用量（事後補不回來，帳本不回填）。
  - 快取輸入 token 的差別計價（`CachedInputTokens` 有存，但不參與計價；快取命中率上升時本頁會**高估**）。
  - 匯出、REST API、團隊過濾（見「五、權限與安全」）。

計費呼叫點（`AiUsageFeature`，數值上線後永不重編）：

| 功能別 | 值 | 計費單位 | 寫帳位置 | 一次動作寫幾列 |
|---|---|---|---|---|
| 會議紀錄生成（最終整理，reduce） | 0 | token | `MeetingDraftJobRunner` | 1 |
| 逐字稿分段摘要（map） | 1 | token | `MeetingDraftJobRunner` | N（逐字稿切段數） |
| 語音轉錄 | 2 | 音訊秒數 | `TranscriptionJobRunner` | N（音檔每 15 分鐘一段） |
| AI 問答 | 3 | token | `AiChatService` | 提問與「修改後重新產生」各 1 |
| 待辦事項擷取 | 4 | token | `TodoExtractionService` | 1 |

⚠️ 「一次使用者動作 = 一次呼叫」是錯的，帳本記的是**實際呼叫**。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/ai-usage`（`Components/Pages/AiUsages/AiUsagePage.razor`，掛 `MainLayout`，內容在 `AiUsageView`） |
| REST API | 無 |
| 選單路徑 | 系統管理（`Menu.json` id=3）→ AI 用量分析（id=33，icon `monetization_on`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[33] = MagicObjectHelper.角色_AI用量分析` |
| 權限鍵 | 頁面鍵「AI 用量分析」（`MagicObjectHelper.角色_AI用量分析`，**值與 `Menu.json` 的 name 一字不差、尾端不加空白**）；單一開關、沒有動作鍵（`RolePermissionService.GetRoleListPermissionAllName` 中為單元素群組）；管理員短路 |
| 主要使用者 | 管理者。預設角色目錄（`RolePresets`）中只有「管理者」含此鍵，一般使用者／檢視者／主管都沒有 |

## 三、畫面與欄位

由上而下：

1. **工具列**：左側「重新整理」；右側「功能」下拉（全部功能＋五個功能別，`AiUsageFeatureText.Describe` 的文字）。
2. **說明列**：「統計起始 yyyy/MM/dd，此日之前的呼叫沒有留下用量資料。」帳本空白時改為「尚未記錄到任何 AI 呼叫…不回填先前的用量」。啟用換算且已有匯率時附「匯率 1 USD = 31.8636 TWD」（小數四位）。
   - ⚠️ 統計起始日**刻意不套功能篩選**：它講的是帳本從哪天開始記，套了的話選「待辦事項擷取」會顯示較晚的日期，像是之前的紀錄遺失。
3. **警示條**（條件顯示）：
   - 「有 N 次呼叫的模型未設定單價，金額**未**納入統計」——本月成功但 `EstimatedCost` 為 null 的筆數，提示到 `LlmSettings:Pricing` 補 deployment 名稱。
   - 「有 N 次呼叫沒有當時的匯率…金額**未**納入台幣統計」——有金額但沒匯率的筆數（僅在啟用換算時計算）。
4. **概況卡片**（本月 1 日至今，跟著功能下拉）：
   - 本月估算金額＋「與上月同期相比 ±N%」；上月同期為 0 時顯示「上月同期無資料」；增加時黃色。
   - 本月 token「輸入 / 輸出」（上千縮寫 K／M）；選「語音轉錄」時顯示「—」，副標「語音轉錄以音訊時長計費」。
   - 本月轉錄時長；選 token 型功能時顯示「—」，副標「這個功能以 token 計費」。
   - 本月呼叫次數；副標「全部成功」或「含 N 次失敗或取消」（紅色）。
5. **本月累計估算金額曲線**（`LineChart`，兩條線「本月」「上月同期」）：X 軸本月 1 日到今天，每點是到當天為止的累計，**最後一點等於第一張卡片的金額**。標題帶單位：有匯率說明（啟用換算且目前取得匯率）時為「（單位：NT$ 分）」，否則為「（單位：美分）」。⚠️ 啟用換算但目前沒有任何匯率時，曲線數值仍是依各列匯率換算的台幣分，標題卻顯示「美分」（標題依 `ExchangeRateNote` 判斷，而非 `CostView.Convert`）。
6. **分佈**：「本月依功能別」圓餅（中央顯示總額；**選了特定功能時不顯示**，只剩一片 100% 沒有資訊）、「本月依模型」長條、「本月依使用者」長條。各取前 8 名，其餘併成「其他」；整組都算不出金額時標成「未設定單價或無匯率」而不是 0 元。
7. **最近呼叫明細**：標題列右側「最近 7／30／90 天」下拉（預設 30，**只影響明細表**）。欄位：時間（MM/dd HH:mm:ss）、功能、模型、使用者、對象（「會議 #id」／「專案 #id」／「—」）、用量（token「輸入 / 輸出」或音訊時長，推估的時長加「（推估）」；沒回報用量顯示「—」）、估算金額、狀態（失敗／已取消以紅字顯示，滑過看錯誤訊息）。每頁 20 筆、伺服端分頁（每頁上限夾在 1～200）。
8. **頁尾說明**：金額是依單價在呼叫當下換算的**估算**；有匯率說明時另加一句台幣以**呼叫當下**匯率換算（來源 open.er-api.com）；失敗與取消沒有用量資料但供應商仍可能計費，所以本頁金額是**下限**。

金額顯示：算不出來一律「—」。換算後（`AiUsageMetrics.FormatConverted`）台幣印「NT$」、其他幣別印代碼，≥ 1 用千分位兩位小數，小於 1 保留最多四位小數，不會被四捨五入成 NT$ 0.00（一次 AI 問答約 NT$0.0014）；停用換算時（`AiUsageMetrics.FormatAmount`）印定價幣別代碼，小於 1 保留最多六位小數。

## 四、內部系統運作

### 寫入：帳本

- 呼叫端在每次 AI 呼叫結束後呼叫 `AiUsageRecorder.RecordAsync(AiUsageEntry)`；provider 只回用量（`TextGenerationResult`／`AiTokenUsage`），**寫帳是呼叫端的事**——只有呼叫端知道使用者／會議／專案，而失敗是以例外表達、`catch` 只能寫在呼叫端。
- `RecordAsync` **永不拋例外**（寫不進去只記 log，不能讓使用者的工作失敗），一律以 `CancellationToken.None` 存檔（取消的呼叫照樣花了錢）。
- 金額由 `AiUsagePricing.EstimateTokenCost`／`EstimateAudioCost` 依 `Model` 查 `LlmSettings.Pricing` 算出；連同單價、`Currency`、`ExchangeRate`、`ConvertedCurrency` 一起寫入當下的快照。
- 匯率只讀 `ExchangeRateCache.Current`（同步欄位）；匯率的 `BaseCurrency` 與 `LlmSettings.Currency` 不同時整組留空。
- 使用者歸屬：`AiUsageAttribution.Resolve(CurrentUser)`，`Id <= 0` 視為無人。背景工作（生成、轉錄）的觸發者在**入列那一刻**由 `MeetingJobRequest` 帶進佇列，recorder 刻意不注入 `CurrentUserService`（背景 scope 會解析成空白使用者且不報錯）。
- 串流用量：`AzureOpenAiTextGenerationProvider` 送 `stream_options.include_usage = true`，每個片段**先取 usage（`ExtractUsage`）再檢查 delta**——帶用量的那一片 `choices` 是空陣列。轉錄時長由 FFmpeg 以 `-loglevel info -nostats` 輸出、`MediaDurationParser` 解析 `Duration:`，取不到時以位元組回推並標 `IsAudioDurationEstimated`。

### 讀取：分析頁

- `AiUsageView` →（注入）`AiUsageAnalysisService`：
  - `GetSummaryAsync(AiUsageFeature? feature)`：撈上月同期起的投影（`UsageFact`），功能篩選在 SQL 端（int 列舉欄位）；金額一律撈進記憶體加總。期間由 `AiUsageMetrics.BuildMonthToDateRanges` 算，曲線由 `AiUsageMetrics.BuildMonthToDateCumulative` 算。
  - `GetRecentCallsAsync(AiUsageQuery)`：`OrderByDescending(OccurredAt).ThenByDescending(Id)`，`Skip`／`Take` **無條件套用**。
- 「要不要換算、怎麼加總、怎麼格式化」集中在私有的 `CostView`，五個顯示點（卡片、曲線、三張分佈、明細）共用；單列換算走 `AiUsageExchange.ToTargetCurrency`（用該列自己的匯率）。
- 圖表元件共用 `Components/Commons/Charts/`（`StatCard`、`LineChart`、`PieChart`、`BarChart`）；`ChartSlice.Value` 以「分」負責幾何（`AiUsageMetrics.ToChartCents`），文字另由 `Display` 給。
- 切換功能 → `ReloadAsync` 重算整頁；換期間或翻頁 → 只重載明細表。

### 匯率背景服務

- `ExchangeRateBackgroundService`（`AddExchangeRateServices` 註冊；`ExchangeRateCache` 為 Singleton、`ExchangeRateFetcher` 為 Scoped、具名 HttpClient 逾時 15 秒）：
  1. `Enabled = false` → 直接結束，不進迴圈。
  2. 冷啟動種子：帳本中 `ExchangeRate != null` 依 `OccurredAt` 最後一筆（幣別相符才用，來源標 `Ledger`）；沒有就用 `FallbackRate`（來源標 `Fallback`）；都沒有就維持 null。
  3. 進迴圈前**先抓一次**，成功後等 `RefreshIntervalHours`，失敗 10 分鐘後重試；失敗只 `MarkFailed`，不清掉目前匯率。
- `ExchangeRateFetcher.FetchAsync` 打 `SourceUrl`，由 `ExchangeRateResponseParser.Parse` 解析並交叉驗證 `base_code`；任何可預期失敗回 null。

### 設定

| 區段 | 鍵 | 現行值 | 說明 |
|---|---|---|---|
| `LlmSettings` | `Currency` | `USD` | 定價幣別 |
| `LlmSettings` | `Pricing.<deployment>` | `gpt-4o-mini`、`gpt-4o-transcribe`、`gpt-4o-mini-transcribe`、`gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna` | `InputPerMillionTokens`／`OutputPerMillionTokens` 或 `AudioPerMinute`；字典不分大小寫；負數啟動擋下 |
| `ExchangeRateSettings` | `Enabled` | `true` | 總開關；`false` 時整頁退回定價幣別 |
| `ExchangeRateSettings` | `SourceUrl` | `https://open.er-api.com/v6/latest/USD` | 必須是 http(s) 絕對網址 |
| `ExchangeRateSettings` | `TargetCurrency` | `TWD` | 必須是三碼英文字母 |
| `ExchangeRateSettings` | `RefreshIntervalHours` | `24` | `int`，1～168 |
| `ExchangeRateSettings` | `FallbackRate` | `32.0` | 類別預設 null（沒有保底）；有填須 > 0 |

兩個區段都 `ValidateDataAnnotations().ValidateOnStart()`（`Program.cs`）。設定細節見 [日誌與設定檔說明 §4.3.3](../operations/日誌與設定檔說明.md)。

### 設計決策

| 議題 | 結論 |
|------|------|
| 一張表還是兩張 | **一張 `AiUsageLog`**，token 型與時長型共用，另一組留 null；**不另設單位欄位**，單位由 `AiUsageFeatureText.IsTokenBased` 唯一決定 |
| 外鍵 | **一個都沒有**。帳本是既成事實，不該因會議或使用者被刪而連帶刪除；`UserName` 存姓名快照 |
| 金額何時算 | **寫入當下算好存起來**，連單價一起存。歷史若被新單價重算，「本月 vs 上月」就失去意義 |
| 算不出金額 | **null，不是 0**。帳本存 null、明細「—」、分佈標「未設定單價或無匯率」、頂端警示條，四道防線 |
| 失敗與取消 | **照記**，`Outcome` 分 Failed／Cancelled，token 與金額留 null、**絕不猜用量**；頁尾說明金額是下限 |
| 記帳的時機 | 在「已經付錢」之後、`continue` **之前**；且在呼叫端修改自己的實體**之前**——recorder 與 job runner 共用 scoped `BackendDBContext`，SaveChanges 會一併送出呼叫端未完成的變更 |
| SQLite 的 decimal | 存成 TEXT，`EstimatedCost`／`ExchangeRate` **不可在 SQL 端排序、比大小或 SUM**，只能 `IS NOT NULL` 篩選後撈進記憶體算 |
| 只存匯率、不存台幣金額 | 台幣＝同一列 `EstimatedCost × ExchangeRate`。另存一份 `EstimatedCostTwd` 必然漂移，也換不到效能 |
| 匯率何時抓 | **只在背景軌道**。`ExchangeRateCache` 刻意不提供任何 async 方法，記帳路徑（使用者等待中）只讀一個欄位 |
| ⭐ 月比月用哪個幣別 | **定價幣別（美金）**。台幣的月比月＝用量變化×匯率變化，匯率一動就會被誤讀成花費增減。曲線用台幣顯示，所以曲線末點比例與卡片百分比可能差匯率的 1～2%，刻意如此 |
| 停用換算 | **整頁退回定價幣別**，不做「有匯率顯示台幣、沒有顯示美金」的混算 |
| 「上月同期」 | 不是上月整月；3/31 對上 2 月時夾到月底，上期為 0 回 null（「上月同期無資料」） |
| 曲線怎麼累加 | 先累加原始金額、最後才換成「分」（每筆先換分會把小額呼叫全捨成 0）；上月較短時延續月底值；只畫到今天 |
| 選了特定功能 | 卡片、曲線、模型／使用者分佈、警示筆數都只算該功能；功能別圓餅隱藏；不適用的單位卡顯示「—」 |
| 期間下拉位置 | 放明細表標題列。曲線改成本月累計後，「最近 N 天」只剩明細表在用，放右上角會變成「選了上面沒反應」的下拉 |
| 不做團隊過濾 | 成本帳本套部分過濾會讓「依使用者分佈」加總對不上總額；這是刻意的全公司彙總頁，靠權限限定給管理者 |

## 五、權限與安全

- 頁面進入：`AuthenticationStateHelper.Check` 通過後以 `CheckAccessPage(MagicObjectHelper.角色_AI用量分析)` 守門，不通過顯示「你沒有權限存取此頁面」；管理員短路。沒有動作級權限，也沒有 API。
- ⚠️ **本頁看得到全公司的用量與人名，不經 `ProjectAccessService`、不套團隊過濾**——與儀表板同屬「全公司彙總」的刻意例外。把「AI 用量分析」勾給非管理者角色，那個人就會看到全部團隊的用量（`RolePermissionService` 註解與 [上線前全面審查](../changelog/2026-10-02-上線前全面審查與修正.md)「四、未修，請判斷」都列了這點，待產品確認）。
- 帳本只存 `ErrorMessage`（截斷 500 字），不存提示詞、逐字稿或回答內容。
- 匯率來源免金鑰，沒有任何機密進入設定或日誌。

## 六、錯誤與邊界

- **換了 deployment 沒補單價**：新呼叫的金額是 null，頂端警示條提示筆數；[系統健康度](系統健康監控-prd.md) 的「AI 供應商設定」也會因「正在用的模型沒有設定單價」亮黃燈。單價只對之後的新呼叫生效，不會追溯。
- **抓不到匯率**：依序退回本行程上次成功的值 → 帳本最後一筆已知匯率（啟動時）→ `FallbackRate` → null（該筆不換算，「—」且不計入總額）。健康度「匯率」項（`SystemHealthChecks.EvaluateExchangeRate`）在使用帳本舊匯率或保底值、或逾期（更新間隔＋1 小時）未更新時黃燈，啟用換算卻完全沒有匯率時紅燈；停用換算時綠燈。
- **改了 `Currency` 卻沒換匯率來源**：`ExchangeRateResponseParser` 的 `base_code` 驗證與 `AiUsageRecorder` 的幣別比對都會讓匯率留空，畫面顯示「—」而不是錯幾 % 的金額；冷啟動種子也不會拿舊幣別的匯率。
- **設定錯誤**：負單價、`SourceUrl` 非 http(s) 絕對網址、`TargetCurrency` 不是三碼字母（含尾隨空白）、`FallbackRate <= 0`、`RefreshIntervalHours` 不在 1～168 → 啟動失敗。
- **帳本寫入失敗**：記 `LogError`，不影響生成／轉錄／問答本身。
- **背景工作取不到觸發者**：`UserId`／`UserName` 為 null，分佈圖歸到「（未記錄）」。
- **舊資料**：0.4.80 前沒有任何紀錄；0.4.88 前的紀錄沒有匯率（實際資料庫已以回填腳本補過一次）。
- **載入失敗**：`ReloadAsync`／明細重載的例外只記 log，畫面保留上一份資料；載入中重複觸發會被早退擋下。

## 七、驗收與測試

測試位於 `src/MeetingRecord/MeetingRecord.Tests/`：

- `AiUsageAnalysisServiceTests`：分頁真的切頁且跨頁不重不漏（`GetRecentCalls_ShouldActuallyPaginate`、`GetRecentCalls_ShouldReturnEveryRowExactlyOnceAcrossPages`）、功能篩選、記憶體加總、未設定單價與失敗分開計數、統計起始日、分佈標示「未設定單價」、功能篩選連動卡片／分佈／警示（`Summary_WithFeature_*`）、⭐ `Summary_WithFeature_ShouldKeepTheLedgerStartDate`、不適用卡片顯示「—」、⭐ `Summary_CumulativeLastPoint_ShouldMatchTheMonthCard`、每列用自己的匯率換算、⭐ `Summary_MonthOverMonth_ShouldUseSourceCurrencyNotConverted`、`Summary_WhenConversionDisabled_ShouldFallBackToSourceCurrency`、明細金額換算與無匯率顯示「—」。
- `AiUsageMetricsTests`：上月同期範圍（月底夾住、閏年、跨年）、變化率上期為 0 回 null、累計曲線（點數＝今天幾號、末點＝月總額、單調不減、⭐ 3/31 對 2 月延續月底值、⭐ 先累加再換分）、`ToChartCents` 夾住負數與溢位、各格式化函式。
- `AiUsagePricingTests`：輸入輸出分開計價、沒單價／半套單價／沒回報用量回 null、0 token 回 0 而非 null、音訊按分鐘比例、單價查表不分大小寫、負單價驗證（含沒有轉錄供應商時）。
- `AiUsageRecorderTests`：⭐ `RecordAsync_WithoutExchangeRate_ShouldStillWriteTheRow`、匯率快照、幣別不符時留空。
- `AiUsageExchangeTests`（含 `ExchangeRateSettingsTests` 類別）：換算、不提早四捨五入、0 元維持 0、無匯率回 null；設定預設值合法、停用時跳過其他檢查、各項非法值被擋。
- `ExchangeRateResponseParserTests`：實際回應樣本、幣別大小寫、⭐ `Parse_BaseCurrencyMismatch_ShouldReturnNull`、⭐ `Parse_NonPositiveRate_ShouldReturnNull`、亂碼與非預期格式不拋例外。
- `AzureOpenAiUsageParsingTests`、`AzureOpenAiStreamingTests`：從最後一片讀用量、中間片段的 `"usage": null`、快取 token；樣本為實際錄下的回應。
- `MediaDurationParserTests`：FFmpeg `Duration:` 解析。
- `MeetingDraftJobRunnerTests`：分段摘要回空白仍記帳、記帳不會提早提交草稿。
- `AiUsageRegistrationTests`：權限目錄含此頁且為單一開關、`Menu.json` 有此節點且位於「系統管理」之下。
- `MigrationSnapshotTests.ModelSnapshot_ShouldMatchCurrentModel`：模型變更必須產生 migration。
- `SystemHealthChecksTests`：AI 設定缺單價、匯率各種狀態、`ExchangeRateCache.MarkFailed` 不動目前匯率。

人工驗收：見 [上架前人工測試清單](../guides/上架前人工測試清單.md)；操作說明見 [系統使用說明](../guides/系統使用說明.md) 的 AI 用量分析段落與 FAQ。

## 八、相關程式與文件

### 版本沿革

- **0.4.80**：建立用量帳本 `AiUsageLog`（migration `AddAiUsageLog`）與 `/ai-usage` 頁；推翻「不做 token 計量」的書面決策（上限與配額仍不做）；佇列改帶觸發者（`MeetingJobRequest`）。
- **0.4.88**：補上 `gpt-5.6-sol` 等單價；金額以呼叫當下匯率換算台幣（migration `AddAiUsageExchangeRate`、`ExchangeRateSettings`、`ExchangeRateBackgroundService`）；以 `scripts/Backfill-AiUsageCost.mjs` 一次性回填既有 18 筆。
- **0.4.91**：功能下拉連動整頁；曲線由「文字生成 vs 語音轉錄每日金額」改為「本月累計 vs 上月同期累計」；期間下拉移到明細表旁。
- **0.4.93**：`ExchangeRateSnapshot.Source`（Live／Ledger／Fallback）與 `ExchangeRateCache.LastFailureAt`，供系統健康度判斷匯率狀態。
- **0.4.97 起**：儀表板的會議時數取自本帳本的轉錄秒數；0.4.99 起啟動時以帳本中最早的轉錄紀錄回推舊會議的上傳者（0.4.99 為 `ProjectAccessBackfillService`，現為 `TeamConversionService.BackfillMeetingCreatorsAsync`）。

### 程式

- 頁面與檢視：`src/MeetingRecord/MeetingRecord.Web/Components/Pages/AiUsages/AiUsagePage.razor`、`src/MeetingRecord/MeetingRecord.Web/Components/Views/AiUsages/AiUsageView.razor`（`.razor.cs`：`OnInitializedAsync` 權限檢查、`ReloadAsync`、`LoadRowsAsync`、`OnFeatureChangedAsync`、`OnTrendDaysChangedAsync`）
- 讀取服務：`src/MeetingRecord/MeetingRecord.Business/Services/AiUsage/AiUsageAnalysisService.cs`（`GetSummaryAsync`、`GetRecentCallsAsync`、`BuildCards`、`BuildCumulativeCost`、`BuildSlices`、`CostView`）、`AiUsageModels.cs`
- 純函式：`AiUsageMetrics`（`BuildMonthToDateRanges`、`BuildMonthToDateCumulative`、`CalculateChangeRate`、`ToChartCents`、`FormatConverted`）、`AiUsagePricing`、`AiUsageExchange`（同目錄）
- 寫入：`AiUsageRecorder.cs`（`AiUsageRecorder.RecordAsync`、`AiUsageEntry`、`AiTokenUsage`、`AiUsageAttribution.Resolve`）；呼叫點 `MeetingDraftJobRunner`、`TranscriptionJobRunner`、`AiChatService`、`TodoExtractionService`
- 匯率：`ExchangeRate.cs`（`ExchangeRateSnapshot`、`ExchangeRateSource`、`ExchangeRateCache`）、`ExchangeRateFetcher.cs`、`ExchangeRateResponseParser.cs`；`src/MeetingRecord/MeetingRecord.Web/BackgroundServices/ExchangeRateBackgroundService.cs`（`ExecuteAsync`、`SeedFromLedgerAsync`、`SeedFromFallback`）
- 用量取得：`src/MeetingRecord/MeetingRecord.Business/Services/TextGeneration/AzureOpenAiTextGenerationProvider.cs`（`ExtractUsage`）、`ITextGenerationProvider.GenerateAsync`
- 資料：`src/MeetingRecord/MeetingRecord.AccessDatas/Models/AiUsageLog.cs`；`src/MeetingRecord/MeetingRecord.Share/Enums/AiUsageFeature.cs`（`AiUsageFeatureText.Describe`／`IsTokenBased`）、`AiUsageOutcome.cs`
- 設定：`src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs`（`Pricing`、`LlmPricingSettings`、`Validate`）、`ExchangeRateSettings.cs`（`Validate`）、`src/MeetingRecord/MeetingRecord.Web/appsettings.json`（`LlmSettings:Pricing`、`ExchangeRateSettings`）
- 註冊：`src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`（`AddExchangeRateServices`）、`Program.cs`（`AddOptions<ExchangeRateSettings>`）
- 權限與選單：`src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`角色_AI用量分析`）、`src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs`（`GetRoleListPermissionAllName`）、`src/MeetingRecord/MeetingRecord.Business/Helpers/RolePresets.cs`、`src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 33）
- 健康檢查：`src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthChecks.cs`（`EvaluateAiProvider`、`EvaluateExchangeRate`）
- 一次性腳本：`scripts/Backfill-AiUsageCost.mjs`（Node 內建 `node:sqlite`；只補空欄位、動手前備份含 `-wal`／`-shm`；⚠️ 回填用執行當天匯率，是使用者要求下的例外，自動路徑一律不回填）

### 交叉連結

- [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)（token 計量的決策沿革）、[系統健康監控 PRD](系統健康監控-prd.md)、[儀表板 PRD](儀表板-prd.md)、[AI 問答 PRD](AI問答-prd.md)
- [日誌與設定檔說明](../operations/日誌與設定檔說明.md)（`LlmSettings`、`ExchangeRateSettings`）、[資料模型與資料庫](../architecture/資料模型與資料庫.md)（`AiUsageLog`）、[系統健康監控（機制）](../features/系統健康監控.md)
- changelog：[AI 用量分析](../changelog/2026-09-17-AI用量分析.md)、[AI 用量台幣換算](../changelog/2026-09-18-AI用量台幣換算.md)、[AI 用量功能篩選連動與累計曲線](../changelog/2026-09-22-AI用量功能篩選連動與累計曲線.md)
- 決策來源：[MeetingInk 對標項目決策清單](../planning/08-MeetingInk-對標項目決策清單.md)（D11 用量帳本）

> 返回 [PRD 主控台](README.md)
