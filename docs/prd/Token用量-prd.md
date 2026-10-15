# Token 用量 PRD

- 文件版本：2.2
- 文件狀態：已實作
- 現行系統版本：1.0.36
- 首次實作版本：0.4.80（帳本與頁面）、1.0.31（升級）
- 最後核對日期：2026/10/15

> 本文件原名 `AI用量分析-prd.md`（1.0.30 前頁面名為「AI 用量分析」），1.0.31 改名並全文改寫成現況。每次呼叫實際送出的內容與回應見 [AI 對話紀錄 PRD](AI對話紀錄-prd.md)。

## 一、目標與範圍

讓管理者看得到「AI 花了多少錢、花在哪裡、誰用的」，並能查單筆、匯出與清理。系統在 0.4.80 之前**完全沒有記錄任何用量**，所以本能力分成兩半：先在每一個會付費的 AI 呼叫寫進**用量帳本**（`AiUsageLog`），再由「Token 用量」頁（`/token-usage`）呈現與管理。1.0.31 參考 NET10-Blazor-Starter 的 /token-usage，把頁面從「本月概況＋最近明細」升級成完整的分析與管理頁。

- **範圍**：
  - 用量帳本：一次 API 呼叫一列；失敗與取消也記；寫入當下算好估算金額並存單價與匯率快照；1.0.29 起每列帶 `CallId`，對應同一次呼叫的 AI 對話紀錄。
  - 模型單價設定（`LlmSettings:Pricing`，以 deployment 名稱為鍵）。
  - 美金→台幣換算：背景服務定期抓匯率（`ExchangeRateBackgroundService`），帳本存「呼叫當下」的匯率。
  - Token 用量頁：上限列、本月概況（四張卡片＋本月累計 vs 上月同期曲線）、篩選、篩選範圍合計、六個頁籤（圖表／每日／依使用者／依功能／依模型／明細）、單筆明細窗（可跳到 AI 對話紀錄）、CSV／PDF 匯出、單筆刪除、清除此日之前、全部清除。
  - 保留期限：`LogRetentionSettings:AiUsageLogDays`（預設 0＝永久保留）。
  - 一次性回填腳本 `scripts/Backfill-AiUsageCost.mjs`（0.4.88 人工執行過，不是自動路徑）。
- **非範圍**：
  - **在本頁設定上限**。每月用量上限（`AiQuotaSettings`）在設定檔設定，本頁只顯示；擋呼叫的是 `AiCallGateway`，見「四、內部系統運作」的「用量上限」。
  - 0.4.80 之前的用量（事後補不回來，帳本不回填）。
  - 快取輸入 token 的差別計價（`CachedInputTokens` 有存、明細窗看得到，但不參與計價；快取命中率上升時本頁會**高估**）。
  - 編輯帳本、REST API、團隊過濾（見「五、權限與安全」）。
  - Starter 的「只匯出目前頁籤」：本專案只提供一個 PDF 按鈕，固定輸出全部段落。

計費呼叫點（`AiUsageFeature`，數值上線後永不重編）：

| 功能別 | 值 | 計費單位 | 呼叫端 | 一次動作寫幾列 |
|---|---|---|---|---|
| 會議紀錄生成（最終整理，reduce） | 0 | token | `MeetingDraftJobRunner` | 1 |
| 逐字稿分段摘要（map） | 1 | token | `MeetingDraftJobRunner` | N（逐字稿切段數） |
| 語音轉錄 | 2 | 音訊秒數 | `TranscriptionJobRunner` | N（音檔每 15 分鐘一段） |
| AI 問答 | 3 | token | `AiChatService` | 提問與「修改後重新產生」各 1 |
| 待辦事項擷取 | 4 | token | `TodoExtractionService` | 1 |
| AI 日誌分析（0.4.121） | 5 | token | `DiagnosticAiService`（監控與診斷 → 日誌檢視） | 1（不帶會議／專案，「對象」為「—」） |
| AI 例外分析（0.4.122） | 6 | token | `DiagnosticAiService`（監控與診斷 → 系統例外紀錄） | 1（不帶會議／專案，「對象」為「—」） |
| AI 連線測試（1.0.35） | 7 | token | `AiConnectionTestService`（系統健康監控 →「測試 AI 連線」，管理者手動按） | 1（一則極短提問） |
| AI 連線測試（轉錄）（1.0.35） | 8 | 音訊 | `AiConnectionTestService` | 1（1 秒靜音） |

1.0.29 起呼叫端一律經 `AiCallGateway`，由 gateway 寫帳（見「四、內部系統運作」）。

⚠️ 「一次使用者動作 = 一次呼叫」是錯的，帳本記的是**實際呼叫**。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/token-usage`，別名 `/ai-usage`（1.0.30 前的網址與書籤照樣能用）（`Components/Pages/AiManagement/TokenUsagePage.razor`，掛 `MainLayout`，內容在 `TokenUsageView`） |
| REST API | 無 |
| 選單路徑 | 系統管理（`Menu.json` id=3）→ AI 管理（id=62，`psychology`，1.0.30 起）→ Token 用量（id=33，icon `toll`）。同組另有「AI 對話紀錄」（id=63）。0.4.120～1.0.29 位於「監控與診斷」（id=36）底下、名稱「AI 用量分析」、icon `monetization_on` |
| 選單過濾 | 列在 `SidebarMenuService.AdminOnlyMenuIds`（非管理者看不到）；分組 62 列在 `GroupOnlyMenuIds`。`MenuPermissionMap` 沒有 id 33 |
| 權限鍵 | **無，只限管理者**（1.0.30 起）。頁面以 `CheckIsAdmin()` 守門，不在權限目錄、不能勾給角色。0.4.80～1.0.29 的頁面鍵「AI 用量分析」改為 `MagicObjectHelper.已退役權限鍵_AI用量分析`，由 `RbacBackfillService` 在啟動時刪除權限列、角色對應與勾選。`角色_Token用量` 只是選單名稱常數，不是權限鍵 |
| 對外連結 | 明細窗的「查看 AI 對話紀錄」導向 `/ai-call-logs?callId=…`（1.0.31） |
| 主要使用者 | 管理者 |

## 三、畫面與欄位

由上而下：

1. **上限列**（1.0.31）：標題「AI 用量上限（本月）」，**固定看本月，不受篩選影響**。
   - **全系統每月**：有設上限時顯示「NT$ 已用 ／ 上限」（整數千分位）；沒設時顯示「不限（本月已用 NT$ N）」。
   - **每人每月**：顯示「NT$ 上限」或「不限」。有設上限時再列「本月用最多」的前 5 人與各自已用金額（沒有名稱時顯示「#Id」）。
   - **狀態膠囊**：達 80% 顯示警示色「已達 80%」，達上限顯示錯誤色「已達上限」；全系統與每位使用者各自判斷。
   - 尾註：「設定在 `AiQuotaSettings`（0＝不限）；未設定單價的呼叫以 0 元計。」
   - 讀不到上限資料時只記錯誤、整列顯示「不限」，不擋整頁。
2. **說明列**：
   - 「統計起始 yyyy/MM/dd（目前保留的最早一筆），此日之前的呼叫沒有留下用量資料或已被清除。」帳本空白時改為「尚未記錄到任何 AI 呼叫…不回填先前的用量」。
   - 啟用換算且已有匯率時附「匯率 1 USD = 31.8636 TWD」（小數四位）。
   - 保留期限：`AiUsageLogDays` 大於 0 時寫「用量紀錄保留 N 天，每天自動清除」；0 時寫「用量紀錄永久保留…需要時可在下方手動清除」。
   - ⚠️ 統計起始日**不套任何篩選**：它講的是帳本目前從哪天開始，套了的話會像是之前的紀錄遺失。
3. **警示條**（條件顯示，都講「本月」）：
   - 「本月有 N 次呼叫的模型未設定單價，金額**未**納入統計」——本月成功但 `EstimatedCost` 為 null 的筆數，提示到 `LlmSettings:Pricing` 補 deployment 名稱。
   - 「本月有 N 次呼叫沒有當時的匯率，金額**未**納入台幣統計」——有金額但沒匯率的筆數（僅在啟用換算時計算）。
4. **本月概況（全部功能）**：**固定全部功能、本月 1 日至今**，不跟著篩選（1.0.31 拿掉原本的功能下拉）。
   - 本月估算金額＋「與上月同期相比 ±N%」；上月同期為 0 時顯示「上月同期無資料」；增加時黃色。
   - 本月 token「輸入 / 輸出」（上千縮寫 K／M）。
   - 本月轉錄時長。
   - 本月呼叫次數；副標「全部成功」或「含 N 次失敗或取消」（紅色）。
   - **本月累計估算金額曲線**（`LineChart`，兩條線「本月」「上月同期」）：X 軸本月 1 日到今天，每點是到當天為止的累計，**最後一點等於第一張卡片的金額**。標題帶單位：有匯率說明時為「（單位：NT$ 分）」，否則為「（單位：美分）」。⚠️ 啟用換算但目前沒有任何匯率時，曲線數值仍是依各列匯率換算的台幣分，標題卻顯示「美分」（標題依 `ExchangeRateNote` 判斷，而非 `CostView.Convert`）。
5. **篩選分析**（1.0.31）：
   - 條件：開始日期、結束日期（含當天）、功能（不限＋七個功能別）、使用者、模型、結果（成功／失敗／已取消）。
     - 日期預設本月 1 日到今天。
     - 使用者和模型的下拉只列帳本裡出現過的值；使用者顯示最近一次的名稱，依名稱排序。
   - **按「查詢」才會套用**。只改條件不按查詢時，翻頁與匯出仍用畫面上那組條件，數字才對得上。開始日期晚於結束日期時提示「開始日期不可晚於結束日期。」且不查詢。
   - 工具列：查詢、重新整理（連同下拉選項一起重讀）、匯出 CSV、匯出 PDF。範圍內沒有任何呼叫時兩個匯出鈕停用。
   - 清除列：日期＋「清除此日之前」、「全部清除」，兩者都是紅色按鈕（見「四」的「刪除與清除」）。
6. **查詢區間列**：「查詢區間：yyyy/MM/dd～yyyy/MM/dd；功能：…、使用者：…」，沒選的條件不列，沒有起訖時寫「不限」。
7. **篩選範圍合計卡**（四張）：
   - 呼叫次數；副標「全部成功」或「含 N 次失敗或取消」（紅色）。
   - token「輸入 / 輸出」。
   - 轉錄時長。
   - 估算金額；有算不出金額的列時副標「有 N 筆未計入（沒單價或沒匯率）」（黃色），否則「依呼叫當下的單價與匯率」。
8. **頁籤**（預設「圖表」，標籤帶筆數）：
   - **圖表**：「依功能」圓餅（中央顯示篩選範圍總額）、「依模型」長條、「依使用者」長條。各取前 8 名，其餘併成「其他」；整組都算不出金額時標成「未設定單價或無匯率」而不是 0 元。
   - **每日（N）**：新到舊，每列有日期「MM/dd (週)」、金額長條（以範圍內最高的一天為滿格）、金額、輸入／輸出 token、轉錄時長、次數；沒有資料的日子淡色顯示。
     - 起訖都有且區間不超過 92 天（`MaxFilledDays`）時補上沒有資料的日子。
     - 超過 92 天或沒有起訖時只列有資料的日子，下方註明。
   - **依使用者（N）／依功能（N）／依模型（N）**：共用元件 `TokenUsageGroupTable`，不分頁。
     - 欄位：使用者／功能／模型、次數、失敗、輸入 token、輸出 token、轉錄時長、估算金額、未計入（大於 0 時以警示色顯示，滑過說明「沒有單價或沒有匯率，金額未計入」）。
     - 依金額由大到小，同額再依次數、名稱；排序在服務層的記憶體內完成。
     - 沒有使用者的列歸到「（未記錄）」，沒有模型名稱的歸到「（未知）」。
   - **明細（N）**：遠端分頁（預設每頁 `MagicObjectHelper.PageSize` 筆，伺服端夾在 1～200），套用篩選。
     - 欄位：時間（yyyy-MM-dd HH:mm:ss，可排序，預設新到舊）、功能、結果膠囊（失敗錯誤色、取消警示色，有錯誤訊息時滑過顯示）、使用者（沒有時顯示「（系統）」）、對象（「會議 #id」／「專案 #id」／「—」）、模型、用量（token「輸入 / 輸出」或音訊時長，推估的時長加「（推估）」；沒回報用量顯示「—」）、估算金額、操作（查看、刪除）。
     - 停在最後一頁時刪掉該頁唯一一筆，頁碼自動回到最後一頁。
9. **頁尾說明**：金額是依單價在呼叫當下換算的**估算**，改設定不影響既有紀錄；有匯率說明時另加一句台幣以**呼叫當下**匯率換算（來源 open.er-api.com），取不到匯率的列不換算、顯示「—」且不計入總額，**上限列則以目前匯率補算沒有匯率的列，兩邊可能有些微差距**；失敗與取消沒有用量資料但供應商仍可能計費，所以本頁金額是**下限**。
10. **明細窗「用量明細」**（1.0.31，`form-modal-standard`，不能點遮罩關閉）：
    - 頂端：找得到同一個 `CallId` 的 AI 對話紀錄時顯示「查看 AI 對話紀錄」鈕，導向 `/ai-call-logs?callId=…`；找不到時改顯示原因：
      - 沒有 `CallId`：「1.0.29 之前的紀錄沒有對話可看。」
      - 有 `CallId` 但查不到：「對話紀錄已清除（超過保留天數或手動刪除），或當時停用了記錄。」
    - 欄位：時間、功能、結果（含錯誤訊息）、使用者（含 Id）、對象、供應商／模型、輸入 token、輸出 token、快取 token、音訊時長（秒，推估時加註）、輸入單價（每百萬 token）、輸出單價（每百萬 token）、音訊單價（每分鐘）、估算金額（原幣）、當時匯率「1 USD = N TWD」、估算金額（換算後）、呼叫識別碼。沒有值一律「—」。
    - 尾註：「單價與匯率都是呼叫當下的快照，之後改設定不影響這一筆。」
11. **確認框**（全部紅色、不能點遮罩關閉）：

    | 動作 | 標題 | 確定鈕 |
    |---|---|---|
    | 明細列「刪除」 | 刪除用量紀錄 | 刪除 |
    | 清除此日之前 | 清除 yyyy-MM-dd 之前的用量紀錄 | 清除 |
    | 全部清除 | 清空全部用量紀錄 | 全部清除 |

    - 刪除的範圍含本月時（單筆在本月、或清除日期晚於本月 1 日）加一句「其中含本月的紀錄，本月 AI 用量上限的已用額度會跟著變少。」全部清除寫「統計起始日會重新計算…本月 AI 用量上限的已用額度會歸零。」
    - 三種都說明：AI 對話紀錄不會一起刪除；會議時數不受影響（儀表板讀的是 `Meeting.MediaDurationSeconds`）。

金額顯示：算不出來一律「—」。換算後（`AiUsageMetrics.FormatConverted`）台幣印「NT$」、其他幣別印代碼，≥ 1 用千分位兩位小數，小於 1 保留最多四位小數，不會被四捨五入成 NT$ 0.00（一次 AI 問答約 NT$0.0014）；停用換算時（`AiUsageMetrics.FormatAmount`）印定價幣別代碼，小於 1 保留最多六位小數。

## 四、內部系統運作

### 寫入：帳本

- 1.0.29 起所有呼叫端一律經 `AiCallGateway`（`GenerateAsync`／`TranscribeAsync`），由 gateway 在呼叫結束後呼叫 `AiUsageRecorder.RecordAsync(AiUsageEntry)`，並以同一個 `CallId` 寫 AI 對話紀錄（`AiCallLog`）；成功、失敗、取消都記，兩邊一對一。呼叫端把使用者／會議／專案放進 `AiCallContext` 傳給 gateway。provider 只回用量（`TextGenerationResult`／`AiTokenUsage`），不寫帳。1.0.28 以前是各呼叫端自己呼叫 `RecordAsync`。
- `RecordAsync` **永不拋例外**（寫不進去只記 log，不能讓使用者的工作失敗），一律以 `CancellationToken.None` 存檔（取消的呼叫照樣花了錢）。
- 金額由 `AiUsagePricing.EstimateTokenCost`／`EstimateAudioCost` 依 `Model` 查 `LlmSettings.Pricing` 算出；連同單價（`InputPricePerMillion`、`OutputPricePerMillion`、`AudioPricePerMinute`）、`Currency`、`ExchangeRate`、`ConvertedCurrency` 一起寫入當下的**快照**——明細窗顯示的就是這些快照。
- 匯率只讀 `ExchangeRateCache.Current`（同步欄位）；匯率的 `BaseCurrency` 與 `LlmSettings.Currency` 不同時整組留空。
- 使用者歸屬：`AiUsageAttribution.Resolve(CurrentUser)`，`Id <= 0` 視為無人。背景工作（生成、轉錄）的觸發者在**入列那一刻**由 `MeetingJobRequest` 帶進佇列，recorder 刻意不注入 `CurrentUserService`（背景 scope 會解析成空白使用者且不報錯）。
- 串流用量：`AzureOpenAiTextGenerationProvider` 送 `stream_options.include_usage = true`，每個片段**先取 usage（`ExtractUsage`）再檢查 delta**——帶用量的那一片 `choices` 是空陣列。轉錄時長由 FFmpeg 以 `-loglevel info -nostats` 輸出、`MediaDurationParser` 解析 `Duration:`，取不到時以位元組回推並標 `IsAudioDurationEstimated`。

### 用量上限（1.0.29；1.0.31 起本頁顯示）

- `AiQuotaService` 以本帳本計算：本月各列 `EstimatedCost` 依**該列自己的匯率**換算台幣後加總（**有金額但沒有匯率的列用目前匯率補算**；未設定單價的列算 0 元；讀不到帳本時放行並記錯誤）。全系統每月與每人每月兩種（`AiQuotaSettings:GlobalMonthlyTwd`／`PerUserMonthlyTwd`，0＝不限制），以 `IOptionsMonitor` 讀取。
- 達上限時 `AiCallGateway` 寫稽核 `Ai.QuotaBlocked`、拋 `AiQuotaExceededException`，該次呼叫不送出、**不記帳**。即時功能每次呼叫前檢查；轉錄與會議紀錄生成在工作開始時檢查一次。
- 本頁上限列呼叫 `IAiQuotaService.GetStatusAsync(null)`（取全系統那一筆）與 `GetTopUsersAsync(5)`（僅在設了每人上限時）。⚠️ 上限列與頁面其他金額的規則差一點：上限列對沒有匯率的列以目前匯率補算（寧可多算），頁面其他地方則不換算、計入「未計入」，所以兩邊可能有些微差距，頁尾有說明。
- 因為上限要讀到上個月初，`AiUsageLogDays` 要啟用時至少 62 天。設定細節見 [日誌與設定檔說明](../operations/日誌與設定檔說明.md) §4.3.0.1.2。

### 讀取：Token 用量頁

- `TokenUsageView` →（注入）`AiUsageAnalysisService`、`IAiQuotaService`、`AiCallLogService`：
  - `GetSummaryAsync()`：本月概況。撈上月同期起的投影（`UsageFact`），金額一律撈進記憶體加總。期間由 `AiUsageMetrics.BuildMonthToDateRanges` 算，曲線由 `AiUsageMetrics.BuildMonthToDateCumulative` 算。方法仍保留 `AiUsageFeature? feature` 參數（0.4.91 的功能連動），頁面 1.0.31 起不再傳。
  - `GetReportAsync(AiUsageFilter)`（1.0.31）：一次讀出範圍內的投影，在記憶體算合計、每日、三種分組（`AiUsageGroupRow`）與圖表切片。
  - `GetRecentCallsAsync(AiUsageQuery)`：明細分頁。`AiUsageQuery(Filter, CurrentPage, PageSize, SortAscending)`；排序 `OccurredAt` 後一律再以 `Id` 排（同一秒會寫進十幾列，不加會翻頁重複或漏列）；`Skip`／`Take` **無條件套用**。`AiUsageRow` 帶 `CallId`。
  - `GetDetailAsync(id)`、`GetForExportAsync(filter, maxRows = 10,000)`（新到舊）、`GetFilterOptionsAsync()`（使用者以 `(UserId, UserName)` 分組取最近一次名稱、模型取不重複值）。
  - `DeleteAsync(id)`、`PurgeBeforeAsync(before)`（1.0.29，自動保留期限共用）、`ClearAllAsync()`：都用 `ExecuteDelete`，不經變更追蹤。
  - 明細窗另以 `AiCallLogService.FindIdByCallIdAsync(callId)` 判斷有沒有對應的 AI 對話紀錄。
- **篩選**：`AiUsageFilter(From, ToExclusive, Feature, UserId, Model, Outcome)`。頁面把結束日轉成「隔天零時」當上界，結束日當天才不會被切掉。私有的 `ApplyFilter` **只比對整數、字串與日期欄位**，金額欄位不進 SQL。頁面另存一份 `appliedFilter`，按查詢才更新。
- 「要不要換算、怎麼加總、怎麼格式化」集中在私有的 `CostView`，所有顯示點（卡片、曲線、分佈、合計、每日、分組、明細、明細窗）共用；單列換算走 `AiUsageExchange.ToTargetCurrency`（用該列自己的匯率），沒有匯率的列不換算、計入「未計入」。「未計入」＝顯示金額算不出來，且是成功呼叫或有原幣金額（失敗且沒金額的本來就沒用量，不算）。
- 每日以 `DateOnly.FromDateTime(OccurredAt)` 直接取日期，**不套 `ToLocalTime()`**（SQLite 讀回 `Kind` 為 Unspecified，套了會整批位移 8 小時）。
- 圖表元件共用 `Components/Commons/Charts/`（`StatCard`、`LineChart`、`PieChart`、`BarChart`）；`ChartSlice.Value` 以「分」負責幾何（`AiUsageMetrics.ToChartCents`），文字另由 `Display` 給。
- 查詢、重新整理、刪除與清除 → `ReloadAsync` 重算整頁（概況、上限、範圍統計、明細目前頁）；翻頁或排序 → 只重載明細。

### 匯出

- **CSV**：`GetForExportAsync(appliedFilter)` 取目前條件下的每一筆，新到舊，最多 10,000 筆（到達上限時提示「只匯出最新 10,000 筆，請縮小查詢條件。」）。由 `CsvExport` 輸出（UTF-8 含 BOM、中和公式注入），檔名 `MeetingRecord.Web-token-usage-yyyyMMdd-HHmmss.csv`。
  - 欄位依序（`TokenUsageView.CsvColumns`）：時間、功能、結果、使用者、對象、供應商、模型、輸入token、輸出token、快取token、音訊秒數、時長推估、估算金額、幣別、匯率、換算金額、換算幣別、錯誤訊息、呼叫識別碼。
  - 數字一律 InvariantCulture，金額不加千分位，試算表才算得動。
- **PDF**：靜態類別 `Business/Services/Export/TokenUsageReportExporter`（純函式，不碰 IO）組 HTML，交給 `IPdfRenderer`（「HTML → 無頭瀏覽器」管線）輸出 A4 橫式，中文字型交給瀏覽器處理。
  - 內容依序：表頭（系統版本、匯出者、查詢區間、篩選條件、匯出時間）、合計（含未定價與無匯率的註記）、每日（CSS 長條）、依使用者、依功能、依模型、明細。
  - 明細最多 200 筆（`MaxDetailRows`；頁面多讀一筆用來判斷），超過時註明「完整資料請用 CSV 匯出」。
  - 所有字串都經過 HTML 跳脫（使用者名稱、模型名稱、錯誤訊息都是外部來源）。
  - 檔名 `Token用量報表_yyyyMMdd-HHmmss.pdf`（`BuildFileName`）。

### 刪除與清除

- 單筆刪除、清除此日之前（不含當天）、全部清除；都先跳紅色確認框（見「三」第 11 項），方法內再檢查一次 `CheckIsAdmin()`。
- **AI 對話紀錄不連動**；會議時數不受影響（1.0.29 起儀表板讀 `Meeting.MediaDurationSeconds`）。
- ⚠️ 本月的紀錄是用量上限的計算來源：刪掉本月的紀錄，本月已用額度會跟著變少；全部清除會歸零。
- **自動保留期限**：`LogRetentionSettings:AiUsageLogDays`（預設 0＝永久保留；啟用時至少 62 天，設 1～61 啟動即失敗）。`AiLogRetentionBackgroundService` 啟動時跑一次、之後每天呼叫 `PurgeBeforeAsync`，有刪到才記稽核 `AiUsage.AutoPurge`。

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
| `LlmSettings` | `Pricing.<deployment>` | `gpt-4o-mini`、`gpt-4o-transcribe`、`gpt-4o-mini-transcribe`、`gpt-transcribe`、`gpt-6.1-sol`（1.0.36）、`gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna` | `InputPerMillionTokens`／`OutputPerMillionTokens` 或 `AudioPerMinute`；字典不分大小寫；負數啟動擋下 |
| `ExchangeRateSettings` | `Enabled` | `true` | 總開關；`false` 時整頁退回定價幣別 |
| `ExchangeRateSettings` | `SourceUrl` | `https://open.er-api.com/v6/latest/USD` | 必須是 http(s) 絕對網址 |
| `ExchangeRateSettings` | `TargetCurrency` | `TWD` | 必須是三碼英文字母 |
| `ExchangeRateSettings` | `RefreshIntervalHours` | `24` | `int`，1～168 |
| `ExchangeRateSettings` | `FallbackRate` | `32.0` | 類別預設 null（沒有保底）；有填須 > 0 |
| `LogRetentionSettings` | `AiUsageLogDays` | `0` | 帳本保留天數；0＝永久，啟用時至少 62（1.0.29） |
| `AiQuotaSettings` | `GlobalMonthlyTwd`／`PerUserMonthlyTwd` | `0`／`0` | 每月用量上限（新台幣），0＝不限制；改設定檔即生效（1.0.29） |

各區段都 `ValidateDataAnnotations().ValidateOnStart()`（`Program.cs`）。設定細節見 [日誌與設定檔說明](../operations/日誌與設定檔說明.md) §4.3.0.1（保留期限與上限）、§4.3.3（單價與匯率）。

### 設計決策

| 議題 | 結論 |
|------|------|
| 一張表還是兩張 | **一張 `AiUsageLog`**，token 型與時長型共用，另一組留 null；**不另設單位欄位**，單位由 `AiUsageFeatureText.IsTokenBased` 唯一決定 |
| 外鍵 | **一個都沒有**。帳本是既成事實，不該因會議或使用者被刪而連帶刪除；`UserName` 存姓名快照 |
| 金額何時算 | **寫入當下算好存起來**，連單價一起存。歷史若被新單價重算，「本月 vs 上月」就失去意義 |
| 算不出金額 | **null，不是 0**。帳本存 null、明細「—」、分佈標「未設定單價或無匯率」、頂端警示條、合計卡與分組表的「未計入」，多道防線 |
| 失敗與取消 | **照記**，`Outcome` 分 Failed／Cancelled，token 與金額留 null、**絕不猜用量**；頁尾說明金額是下限 |
| 記帳的時機 | 在「已經付錢」之後、`continue` **之前**；且在呼叫端修改自己的實體**之前**——recorder 與 job runner 共用 scoped `BackendDBContext`，SaveChanges 會一併送出呼叫端未完成的變更 |
| SQLite 的 decimal | 存成 TEXT，`EstimatedCost`／`ExchangeRate` **不可在 SQL 端排序、比大小或 SUM**，只能 `IS NOT NULL` 篩選後撈進記憶體算；1.0.31 的分組排序同樣在記憶體做（測試防字典序陷阱）；篩選也只放整數、字串、日期欄位 |
| 只存匯率、不存台幣金額 | 台幣＝同一列 `EstimatedCost × ExchangeRate`。另存一份 `EstimatedCostTwd` 必然漂移，也換不到效能 |
| 匯率何時抓 | **只在背景軌道**。`ExchangeRateCache` 刻意不提供任何 async 方法，記帳路徑（使用者等待中）只讀一個欄位 |
| ⭐ 月比月用哪個幣別 | **定價幣別（美金）**。台幣的月比月＝用量變化×匯率變化，匯率一動就會被誤讀成花費增減。曲線用台幣顯示，所以曲線末點比例與卡片百分比可能差匯率的 1～2%，刻意如此 |
| 停用換算 | **整頁退回定價幣別**，不做「有匯率顯示台幣、沒有顯示美金」的混算 |
| 「上月同期」 | 不是上月整月；3/31 對上 2 月時夾到月底，上期為 0 回 null（「上月同期無資料」） |
| 曲線怎麼累加 | 先累加原始金額、最後才換成「分」（每筆先換分會把小額呼叫全捨成 0）；上月較短時延續月底值；只畫到今天 |
| 概況與上限列不跟篩選（1.0.31） | 「本月花多少、離上限多遠」是固定要看的狀態，跟著篩選走會讓人誤讀；篩選的結果另放在下方的合計卡與頁籤 |
| 按查詢才套用（1.0.31） | 頁面記住 `appliedFilter`。下拉一改就生效的話，翻頁與匯出會用到還沒查詢的條件，數字對不上畫面上的合計 |
| 每日補空白日（1.0.31） | 區間不超過 92 天才補，才看得出「哪幾天沒用」；更長或沒有起訖時只列有資料的日子，免得一年列出 365 列空白 |
| PDF 管線（1.0.31） | 沿用本專案「HTML → 無頭瀏覽器」（`IPdfRenderer`），不用 Starter 的 MigraDoc；中文字型交給瀏覽器，不必嵌字型檔。只提供一個按鈕、固定輸出全部段落 |
| 刪除不連動對話紀錄（1.0.31） | 帳本與 AI 對話紀錄各有自己的保留與清除，刪帳本不刪對話紀錄（反之亦然）；明細窗找不到對話時說明原因 |
| 查看明細不記稽核 | 帳本沒有內容（只有用量與金額），與 AI 對話紀錄的 `AiCallLog.View` 不同 |
| 不做團隊過濾 | 成本帳本套部分過濾會讓「依使用者」加總對不上總額；這是刻意的全公司彙總頁，1.0.30 起只限管理者（之前靠權限鍵限定） |

## 五、權限與安全

- **只限管理者**：
  - 頁面進入：`AuthenticationStateHelper.Check` 通過後以 `CheckIsAdmin()` 守門（1.0.30 起；之前是 `CheckAccessPage(角色_AI用量分析)`），判定前不渲染、不讀任何資料，非管理者顯示「你沒有權限存取此頁面」。
  - 刪除、清除在方法內再檢查一次 `CheckIsAdmin()`。
  - 側邊欄以 `AdminOnlyMenuIds` 排除非管理者。舊資料庫殘留的「AI 用量分析」勾選不再有作用，並在啟動時由 `RbacBackfillService` 清掉。沒有動作級權限，也沒有 API。
- ⚠️ **本頁看得到全公司的用量與人名，不經 `ProjectAccessService`、不套團隊過濾**——與儀表板同屬「全公司彙總」的刻意例外。1.0.29 以前可以把「AI 用量分析」勾給非管理者角色，那個人就會看到全部團隊的用量（[上線前全面審查](../changelog/2026-10-02-上線前全面審查與修正.md)「四、未修，請判斷」列過這點）；1.0.30 改為只限管理者，這個疑慮已不存在。
- 帳本只存 `ErrorMessage`（截斷 500 字），不存提示詞、逐字稿或回答內容；要看內容得到 AI 對話紀錄（那邊查看會留稽核）。
- **稽核**（`targetType` 為 `AiUsageLog`，只記編號、筆數與條件）：
  - `AiUsage.Delete`（目標為該列編號，記功能與呼叫識別碼）、`AiUsage.Purge`（日期與筆數）、`AiUsage.ClearAll`（筆數）。
  - `AiUsage.Export`（CSV，筆數與區間）、`AiUsage.ExportPdf`（筆數、區間與檔案大小）。
  - `AiUsage.AutoPurge`（1.0.29，背景自動清除）。
  - 查看明細不記。
- 匯率來源免金鑰，沒有任何機密進入設定或日誌。
- PDF 的所有字串經過 HTML 跳脫；CSV 由 `CsvExport` 中和公式注入。

## 六、錯誤與邊界

- **換了 deployment 沒補單價**：新呼叫的金額是 null，頂端警示條提示本月筆數，合計卡與分組表計入「未計入」；[系統健康監控](系統健康監控-prd.md) 的「AI 供應商設定」也會因「正在用的模型沒有設定單價」亮黃燈。單價只對之後的新呼叫生效，不會追溯。
- **抓不到匯率**：依序退回本行程上次成功的值 → 帳本最後一筆已知匯率（啟動時）→ `FallbackRate` → null（該筆不換算，「—」且不計入總額）。健康度「匯率」項（`SystemHealthChecks.EvaluateExchangeRate`）在使用帳本舊匯率或保底值、或逾期（更新間隔＋1 小時）未更新時黃燈，啟用換算卻完全沒有匯率時紅燈；停用換算時綠燈。
- **改了 `Currency` 卻沒換匯率來源**：`ExchangeRateResponseParser` 的 `base_code` 驗證與 `AiUsageRecorder` 的幣別比對都會讓匯率留空，畫面顯示「—」而不是錯幾 % 的金額；冷啟動種子也不會拿舊幣別的匯率。
- **設定錯誤**：負單價、`SourceUrl` 非 http(s) 絕對網址、`TargetCurrency` 不是三碼字母（含尾隨空白）、`FallbackRate <= 0`、`RefreshIntervalHours` 不在 1～168、`AiUsageLogDays` 為 1～61 → 啟動失敗。
- **帳本寫入失敗**：記 `LogError`，不影響生成／轉錄／問答本身。
- **背景工作取不到觸發者**：`UserId`／`UserName` 為 null，分佈與分組歸到「（未記錄）」，明細顯示「（系統）」，也不會出現在使用者下拉。
- **舊資料**：0.4.80 前沒有任何紀錄；0.4.88 前的紀錄沒有匯率（實際資料庫已以回填腳本補過一次）；1.0.29 前的紀錄沒有 `CallId`，明細窗不提供「查看 AI 對話紀錄」。
- **對應的對話紀錄不在**（超過 `AiCallLogSettings:RetentionDays`、手動刪除或當時停用記錄）：明細窗顯示原因，不顯示按鈕。
- **開始日期晚於結束日期**：提示並不查詢。**清除此日之前沒選日期**：提示「請先選擇日期。」
- **刪除時該列已不存在**：提示「這筆紀錄已不存在。」並重新載入；**查看時該列已被刪**：提示「找不到這筆用量紀錄，可能已被刪除。」並關閉明細窗。
- **CSV 超過 10,000 筆**：只匯出最新 10,000 筆並提示縮小條件；**PDF 明細超過 200 筆**：只列前 200 筆並註明改用 CSV。
- **上限列讀取失敗**：只記錯誤，上限列顯示「不限」，頁面其他部分照常。
- **載入失敗**：`ReloadAsync` 的例外記 log 並提示「載入 Token 用量失敗：…」，畫面保留上一份資料；載入中重複觸發會被早退擋下（避免同一個 circuit 的 DbContext 被並行使用）。
- **刪掉本月紀錄**：本月上限已用額度跟著變少，確認框已提醒。

## 七、驗收與測試

測試位於 `src/MeetingRecord/MeetingRecord.Tests/`：

- `AiUsageAnalysisServiceTests`：
  - 既有：分頁真的切頁且跨頁不重不漏（`GetRecentCalls_ShouldActuallyPaginate`、`GetRecentCalls_ShouldReturnEveryRowExactlyOnceAcrossPages`）、功能篩選、記憶體加總、未設定單價與失敗分開計數、統計起始日、分佈標示「未設定單價」、功能篩選連動卡片／分佈／警示（`Summary_WithFeature_*`）、⭐ `Summary_WithFeature_ShouldKeepTheLedgerStartDate`、不適用卡片顯示「—」、⭐ `Summary_CumulativeLastPoint_ShouldMatchTheMonthCard`、每列用自己的匯率換算、⭐ `Summary_MonthOverMonth_ShouldUseSourceCurrencyNotConverted`、`Summary_WhenConversionDisabled_ShouldFallBackToSourceCurrency`、明細金額換算與無匯率顯示「—」。
  - 1.0.31 新增 11 則：結束日含當天（`Filter_ShouldIncludeTheWholeEndDay_AndExcludeTheNextDay`）、功能／使用者／模型／結果篩選、⭐ 分組依金額數值排序（`Report_ShouldSumAndSortGroupsByNumericCost`，防 TEXT 字典序陷阱）、每列各自匯率換算與未計入筆數、每日 92 天內補齊空白日、區間更長或沒有起訖時只列有資料的日子、明細升冪排序並帶出 CallId、明細快照、篩選選項取最近一次的名稱、匯出套用篩選與筆數上限、刪除／清除此日之前／全部清除。
- `TokenUsageReportExporterTests`（1.0.31，6 則）：段落齊全、HTML 跳脫、明細截斷與加註、沒有資料時不拋例外、檔名；`View_CsvColumns_ShouldKeepOrderAndCarryTheCallId`（CSV 欄位順序與內容）。
- `AiUsageMetricsTests`：上月同期範圍（月底夾住、閏年、跨年）、變化率上期為 0 回 null、累計曲線（點數＝今天幾號、末點＝月總額、單調不減、⭐ 3/31 對 2 月延續月底值、⭐ 先累加再換分）、`ToChartCents` 夾住負數與溢位、各格式化函式。
- `AiUsagePricingTests`：輸入輸出分開計價、沒單價／半套單價／沒回報用量回 null、0 token 回 0 而非 null、音訊按分鐘比例、單價查表不分大小寫、負單價驗證（含沒有轉錄供應商時）。
- `AiUsageRecorderTests`：⭐ `RecordAsync_WithoutExchangeRate_ShouldStillWriteTheRow`、匯率快照、幣別不符時留空；1.0.29 起存入 `CallId`、寫入失敗時移出追蹤。
- `AiCallGatewayTests`、`AiQuotaServiceTests`（1.0.29）：CallId 一致、被上限擋下不呼叫不記帳；上限依每列匯率、缺匯率用目前匯率、每人、排行。
- `AiUsageExchangeTests`（含 `ExchangeRateSettingsTests` 類別）：換算、不提早四捨五入、0 元維持 0、無匯率回 null；設定預設值合法、停用時跳過其他檢查、各項非法值被擋。
- `ExchangeRateResponseParserTests`：實際回應樣本、幣別大小寫、⭐ `Parse_BaseCurrencyMismatch_ShouldReturnNull`、⭐ `Parse_NonPositiveRate_ShouldReturnNull`、亂碼與非預期格式不拋例外。
- `AzureOpenAiUsageParsingTests`、`AzureOpenAiStreamingTests`：從最後一片讀用量、中間片段的 `"usage": null`、快取 token；樣本為實際錄下的回應。
- `MediaDurationParserTests`：FFmpeg `Duration:` 解析。
- `MeetingDraftJobRunnerTests`：分段摘要回空白仍記帳、記帳不會提早提交草稿。
- `AiManagementMenuRegistrationTests`（1.0.30 由 `AiUsageRegistrationTests` 改名重寫）：`Menu.json` 的「AI 管理」節點、名稱與網址、只限管理者、不在權限目錄。`RbacBackfillServiceTests`：退役鍵「AI 用量分析」在啟動時清除。
- `MigrationSnapshotTests.ModelSnapshot_ShouldMatchCurrentModel`：模型變更必須產生 migration。
- `SystemHealthChecksTests`：AI 設定缺單價、匯率各種狀態、`ExchangeRateCache.MarkFailed` 不動目前匯率。

**人工驗收**（見 [上架前人工測試清單](../guides/上架前人工測試清單.md) 第 14 節）：

- 上限列在沒設上限、設了全系統上限、設了每人上限時各自正確，膠囊在 80% 與達上限時出現。
- 篩選改了不按查詢不生效；按查詢後合計卡、各頁籤、明細一致。
- 明細窗的「查看 AI 對話紀錄」跳到同一筆；舊紀錄或對話已清除時顯示原因。
- CSV 用 Excel 開啟中文正常；PDF 中文正常、A4 橫式。
- 三種刪除確認框文字正確（含本月額度提醒），完成後 `AuditLog` 有對應的 `AiUsage.*`。

操作說明見 [系統使用說明](../guides/系統使用說明.md) 的「想知道到底花了多少？」與 FAQ；欄位細節見 [畫面與欄位字典](../guides/畫面與欄位字典.md) §3.3。

## 八、相關程式與文件

### 程式

- 頁面與檢視：`src/MeetingRecord/MeetingRecord.Web/Components/Pages/AiManagement/TokenUsagePage.razor`、`src/MeetingRecord/MeetingRecord.Web/Components/Views/AiManagement/TokenUsageView.razor(.cs/.css)`（`OnInitializedAsync` 管理者檢查、`ReloadAsync`、`LoadQuotaAsync`、`LoadRowsAsync`、`OnSearchAsync`、`OnViewAsync`、`OnExportCsvAsync`、`OnExportPdfAsync`、`OnDeleteAsync`、`OnPurgeAsync`、`OnClearAllAsync`、`CsvColumns`）、`TokenUsageGroupTable.razor(.css)`（1.0.31，三個分組頁籤共用）
- 讀取服務：`src/MeetingRecord/MeetingRecord.Business/Services/AiUsage/AiUsageAnalysisService.cs`（`GetSummaryAsync`、`GetReportAsync`、`GetRecentCallsAsync`、`GetDetailAsync`、`GetForExportAsync`、`GetFilterOptionsAsync`、`DeleteAsync`、`PurgeBeforeAsync`、`ClearAllAsync`、`ApplyFilter`、`BuildGroups`、`BuildDaily`、`CostView`）、`AiUsageModels.cs`（`AiUsageFilter`、`AiUsageQuery`、`AiUsageRow`、`AiUsageReport`、`AiUsageGroupRow`、`AiUsageDailyRow`、`AiUsageFilterOptions`、`AiUsageDetail`）
- PDF 報表：`src/MeetingRecord/MeetingRecord.Business/Services/Export/TokenUsageReportExporter.cs`（1.0.31，`BuildHtml`、`BuildFileName`、`MaxDetailRows`）；CSV：`src/MeetingRecord/MeetingRecord.Web/Export/CsvExport`
- 用量上限：`src/MeetingRecord/MeetingRecord.Business/Services/AiUsage/AiQuotaService.cs`（`GetStatusAsync`、`GetTopUsersAsync`）
- 純函式：`AiUsageMetrics`（`BuildMonthToDateRanges`、`BuildMonthToDateCumulative`、`CalculateChangeRate`、`ToChartCents`、`FormatConverted`）、`AiUsagePricing`、`AiUsageExchange`（同目錄）
- 寫入：`AiCallGateway.cs`、`AiUsageRecorder.cs`（`AiUsageRecorder.RecordAsync`、`AiUsageEntry`、`AiTokenUsage`、`AiUsageAttribution.Resolve`）；呼叫端 `MeetingDraftJobRunner`、`TranscriptionJobRunner`、`AiChatService`、`TodoExtractionService`、`DiagnosticAiService`（`src/MeetingRecord/MeetingRecord.Business/Services/Diagnostics/`）
- 保留期限：`src/MeetingRecord/MeetingRecord.Web/BackgroundServices/AiLogRetentionBackgroundService.cs`
- 匯率：`ExchangeRate.cs`（`ExchangeRateSnapshot`、`ExchangeRateSource`、`ExchangeRateCache`）、`ExchangeRateFetcher.cs`、`ExchangeRateResponseParser.cs`；`src/MeetingRecord/MeetingRecord.Web/BackgroundServices/ExchangeRateBackgroundService.cs`（`ExecuteAsync`、`SeedFromLedgerAsync`、`SeedFromFallback`）
- 用量取得：`src/MeetingRecord/MeetingRecord.Business/Services/TextGeneration/AzureOpenAiTextGenerationProvider.cs`（`ExtractUsage`）、`ITextGenerationProvider.GenerateAsync`
- 資料：`src/MeetingRecord/MeetingRecord.AccessDatas/Models/AiUsageLog.cs`；`src/MeetingRecord/MeetingRecord.Share/Enums/AiUsageFeature.cs`（`AiUsageFeatureText.Describe`／`IsTokenBased`）、`AiUsageOutcome.cs`
- 設定：`src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs`（`Pricing`、`LlmPricingSettings`、`Validate`）、`ExchangeRateSettings.cs`（`Validate`）、`LogRetentionSettings.cs`、`AiQuotaSettings.cs`、`src/MeetingRecord/MeetingRecord.Web/appsettings.json`
- 註冊：`src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`（`AddExchangeRateServices`）、`Program.cs`（`AddOptions<ExchangeRateSettings>`）
- 權限與選單：`src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`角色_Token用量`、`已退役權限鍵_AI用量分析`）、`src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacBackfillService.cs`（清除退役鍵）、`src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs`（`GetRoleListPermissionAllName`，1.0.30 起不含本頁）、`src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`AdminOnlyMenuIds`、`GroupOnlyMenuIds`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 62、33）
- 健康檢查：`src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthChecks.cs`（`EvaluateAiProvider`、`EvaluateExchangeRate`）
- 一次性腳本：`scripts/Backfill-AiUsageCost.mjs`（Node 內建 `node:sqlite`；只補空欄位、動手前備份含 `-wal`／`-shm`；⚠️ 回填用執行當天匯率，是使用者要求下的例外，自動路徑一律不回填）

### 交叉連結

- [AI 對話紀錄 PRD](AI對話紀錄-prd.md)（同一個 `CallId` 的送出內容與回應）、[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)（token 計量的決策沿革）、[系統健康監控 PRD](系統健康監控-prd.md)、[儀表板 PRD](儀表板-prd.md)、[AI 問答 PRD](AI問答-prd.md)
- [日誌與設定檔說明](../operations/日誌與設定檔說明.md)（`LlmSettings`、`ExchangeRateSettings`、`LogRetentionSettings`、`AiQuotaSettings`）、[資料模型與資料庫](../architecture/資料模型與資料庫.md)（`AiUsageLog`）、[系統健康監控（機制）](../features/系統健康監控.md)、[對話窗 UI 設計規範](../architecture/對話窗%20UI%20設計規範.md)
- changelog：[AI 用量分析](../changelog/2026-09-17-AI用量分析.md)、[AI 用量台幣換算](../changelog/2026-09-18-AI用量台幣換算.md)、[AI 用量功能篩選連動與累計曲線](../changelog/2026-09-22-AI用量功能篩選連動與累計曲線.md)、[AI 管理（一）基礎記錄管線與用量上限](../changelog/2026-10-15-AI管理基礎記錄管線與用量上限.md)、[AI 管理（二）AI 對話紀錄頁與選單](../changelog/2026-10-15-AI管理AI對話紀錄頁與選單.md)、[AI 管理（三）Token 用量升級](../changelog/2026-10-15-AI管理Token用量升級.md)
- 決策來源：[MeetingInk 對標項目決策清單](../planning/08-MeetingInk-對標項目決策清單.md)（D11 用量帳本）

## 九、版本沿革

- **0.4.80**：建立用量帳本 `AiUsageLog`（migration `AddAiUsageLog`）與 `/ai-usage` 頁；推翻「不做 token 計量」的書面決策（上限與配額仍不做）；佇列改帶觸發者（`MeetingJobRequest`）。
- **0.4.88**：補上 `gpt-5.6-sol` 等單價；金額以呼叫當下匯率換算台幣（migration `AddAiUsageExchangeRate`、`ExchangeRateSettings`、`ExchangeRateBackgroundService`）；以 `scripts/Backfill-AiUsageCost.mjs` 一次性回填既有 18 筆。
- **0.4.91**：功能下拉連動整頁；曲線由「文字生成 vs 語音轉錄每日金額」改為「本月累計 vs 上月同期累計」；期間下拉移到明細表旁。
- **0.4.93**：`ExchangeRateSnapshot.Source`（Live／Ledger／Fallback）與 `ExchangeRateCache.LastFailureAt`，供系統健康度判斷匯率狀態。
- **0.4.97 起**：儀表板的會議時數取自本帳本的轉錄秒數；0.4.99 起啟動時以帳本中最早的轉錄紀錄回推舊會議的上傳者（0.4.99 為 `ProjectAccessBackfillService`，現為 `TeamConversionService.BackfillMeetingCreatorsAsync`）。
- **0.4.120**：頁面移到「系統管理 → 監控與診斷」底下（仍以權限鍵「AI 用量分析」授權）。
- **0.4.121**：新增功能別 `AiUsageFeature.LogAnalysis = 5`（「AI 日誌分析」，token 計費），由 `DiagnosticAiService` 寫帳（成功／失敗／取消都記，不帶會議／專案）；功能下拉自動多一項。
- **1.0.35**：新增功能別 `ConnectionTest = 7`（「AI 連線測試」，token 計費）與 `TranscriptionConnectionTest = 8`（「AI 連線測試（轉錄）」，以音訊時長計費，`AiUsageFeatureText.IsTokenBased` 一併排除），由系統健康監控的「測試 AI 連線」觸發；功能下拉自動多兩項。
- **0.4.122**：新增功能別 `AiUsageFeature.ExceptionAnalysis = 6`（「AI 例外分析」，token 計費，系統例外紀錄的明細窗觸發），同樣由 `DiagnosticAiService` 寫帳；功能下拉自動多一項。
- **0.4.124**：補上 Azure 轉錄部署 `gpt-transcribe` 的單價（每分鐘 0.0045 美元，依 OpenAI 官方價目）；之前健康監控「AI 供應商設定」亮黃燈，轉錄金額記成「—」。本機帳本沒有這個模型的舊紀錄，未回填。`SystemHealthChecksTests.AiProvider_ShippedPricing_ShouldCoverTheDeploymentsInUse` 以出貨的單價表守住。
- **1.0.29**：所有呼叫端改經 `AiCallGateway` 寫帳；帳本新增 `CallId`（migration `AddAiCallLogAndMeetingDuration`），對應新的 AI 對話紀錄（`AiCallLog`，頁面在 1.0.30）；帳本可設自動保留期限 `LogRetentionSettings:AiUsageLogDays`（預設 0＝永久，啟用時至少 62 天，`AiLogRetentionBackgroundService` 每天清，稽核 `AiUsage.AutoPurge`）；新增每月用量上限 `AiQuotaSettings`，依本帳本計算。儀表板會議時數改讀 `Meeting.MediaDurationSeconds`，不再依賴本帳本。見 [AI 管理（一）](../changelog/2026-10-15-AI管理基礎記錄管線與用量上限.md)。
- **1.0.30**：頁面改名「Token 用量」，從「監控與診斷」搬到新分組「系統管理 → AI 管理」（id 62），網址改為 `/token-usage`（`/ai-usage` 保留為別名），icon 改 `toll`；改為只限管理者（`CheckIsAdmin()`＋`AdminOnlyMenuIds`），權限鍵「AI 用量分析」退役（`已退役權限鍵_AI用量分析`，`RbacBackfillService` 啟動時清除）。檔案搬到 `Pages/AiManagement/TokenUsagePage.razor`、`Views/AiManagement/TokenUsageView.*`。頁面功能不變。見 [AI 管理（二）](../changelog/2026-10-15-AI管理AI對話紀錄頁與選單.md)。
- **1.0.31**：頁面升級成完整的分析與管理頁：上限列（接上 1.0.29 的 `GetStatusAsync`／`GetTopUsersAsync`）、篩選（按查詢才套用）、篩選範圍合計卡、六個頁籤（新增元件 `TokenUsageGroupTable`）、明細窗（單價與匯率快照，可跳 AI 對話紀錄）、CSV／PDF 匯出（新增 `TokenUsageReportExporter`）、單筆刪除／清除此日之前／全部清除與 `AiUsage.*` 稽核。本月概況改為固定全部功能、本月，原本的功能下拉與「最近 N 天」下拉拿掉。服務新增 `AiUsageFilter`、`GetReportAsync` 等方法。PRD 由 `AI用量分析-prd.md` 改名為本檔並全文改寫。見 [AI 管理（三）](../changelog/2026-10-15-AI管理Token用量升級.md)。
- **1.0.36**：出貨的單價表加入 `gpt-6.1-sol`（輸入 $2、輸出 $10／每百萬 token，OpenAI 官方模型頁標準層）。部署改用它之後的呼叫才有金額；之前已記錄、金額為「—」的紀錄不回填。

> 返回 [PRD 主控台](README.md)
