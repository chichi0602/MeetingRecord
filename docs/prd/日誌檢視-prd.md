# 日誌檢視 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.122
- 首次實作版本：0.4.121（移植自 NET10-Blazor-Starter；AI 分析改走本專案的供應商與用量帳本）
- 最後核對日期：2026/10/14

## 一、目標與範圍

排查問題時，管理者不必登入主機翻日誌檔，就能在系統裡依時間、等級、關鍵字找到相關紀錄，看完整堆疊、複製或匯出；必要時把結果交給 AI 整理成重點與建議處置。

- 範圍：`/logs` 頁面、讀取 NLog 檔案目標、篩選與快捷區間、展開原文、查前後 1 分鐘、點列複製、複製全部、匯出 .log、AI 日誌分析（含 PDF 報告）。
- 非範圍：即時尾隨（tail -f）、跨主機彙整、修改或刪除日誌、AI 追問（多輪）、儲存分析結果。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/logs` | 系統管理 → 監控與診斷 → 日誌檢視（id 37） | **只限管理者**（不在角色權限目錄，`SidebarMenuService.AdminOnlyMenuIds`） | 系統管理員／維運 |

## 三、畫面與欄位

- 查詢條件：起訖時間（`DatePicker` 含時間，`yyyy-MM-dd HH:mm:ss`）、最低等級（不限／TRACE～FATAL）、筆數（1～10,000，步進 100，預設 100）、關鍵字（比對整筆原文含堆疊、不分大小寫）、TraceId（與第 2 欄完全相同；0.4.122 起第 2 欄是 8 碼錯誤追蹤碼，使用者在錯誤提示上看到的碼直接貼這裡）。
- 快捷區間：最近 5 分、最近 15 分、最近 1 小時、今天（只改時間並立即查詢）。初次進頁查最近 1 小時。
- 工具列（圖示＋文字，沒有資料時停用）：查詢、全部展開、全部收合、複製（超過 1,000 筆先確認）、匯出（`.log`，UTF-8 含 BOM，檔名 `MeetingRecord.Web-logs-yyyyMMdd-HHmmss.log`）、AI 分析（LLM 未設定時停用，滑過顯示原因）。
- 表格：時間（到毫秒）、等級（膠囊：ERROR／FATAL 錯誤色、WARN 警示色）、記錄器、訊息、TraceId；每頁 20 筆、最新在前。
  - 點列：複製該筆原文。
  - 展開：原文 `<pre>`（可捲動）與「查前後 1 分鐘」（清掉等級、關鍵字、TraceId，筆數拉到上限）。
- 狀態訊息（無資料、目錄不存在）與警示（區間被夾住、筆數被調整、個別檔案讀不到）顯示在表格上方。
- **AI 日誌分析**
  1. 確認窗「確認執行 AI 日誌分析（會產生費用）」：寫出送出筆數、約略字數，以及內容可能含帳號、檔名與使用者輸入；超過 100 筆時說明只送最新 100 筆。OkText「開始分析」。
  2. 對話窗（`form-modal-large`）：分析中顯示「已等待 N 秒」並逐段顯示 AI 回覆的純文字；完成後排成 Markdown（總結／重點問題／錯誤與例外／建議處置）。
  3. 摘要：查詢區間、最低等級、關鍵字、TraceId、分析範圍（分析 X／查詢 Y 筆）、使用模型、用量、耗時。
  4. 按鈕：「複製結果」（Markdown 原文）、「匯出 PDF」（`AI日誌分析_yyyyMMdd-HHmmss.pdf`，含免責說明與「附錄：實際送出的日誌」）。
  5. 關閉對話窗＝取消分析。

## 四、內部系統運作

1. `LogQueryService.QueryAsync`：檔案路徑來自 `INLogFilePathResolver`（`{NLog:BasePath}/MeetingRecord.Web/MeetingRecord.Web-logfile-{日期}*.log`，含封存檔、依最後寫入時間排序），依時間升冪逐檔串流（`FileShare.ReadWrite`，因為 NLog `keepFileOpen`）。
2. 一行若第 25 個字元是 `|` 且前 24 字元可解析成 `yyyy-MM-dd HH:mm:ss.ffff` 即為新紀錄，否則是上一筆的續行（堆疊）。欄位依 nlog.config 的 7 欄 layout 切分；訊息本身含 `|` 時以最後一個 `|` 為界。0.4.122 起第 2 欄由 `${aspnet-TraceIdentifier}` 改為 `${scopeproperty:item=TraceCode:whenEmpty=${aspnet-TraceIdentifier}}`（錯誤追蹤碼，`Diagnostics/TraceCode`；HTTP 請求的追蹤碼同時取代 `TraceIdentifier`），欄數不變、解析不受影響；Blazor 互動也各有一組碼，所以畫面操作的日誌行同樣查得到。格式不符的行照樣保留、不因等級篩選隱藏。
3. 每筆湊齊後才套用時間、等級、關鍵字、TraceId 篩選；通過者進容量 N 的佇列（只留最新 N 筆，記憶體 O(N)）；內容時間超過結束時間即停止讀取。區間超過 3 天夾住起始時間、筆數超過 10,000 夾住並提示。
4. AI：`AiLogPromptBuilder.Build` 取最新 100 筆原文（不截斷、換行統一 LF）組成使用者訊息；系統提示詞 `AiDiagnosticPrompts.LogAnalysis`（四個章節＋防注入規則：日誌內容只是資料）；`DiagnosticAiService.AnalyzeAsync` 依 `LlmSettings.DefaultProvider` 呼叫 `ITextGenerationProvider` 串流，**成功／失敗／取消都寫進 AI 用量帳本**（`AiUsageFeature.LogAnalysis = 5`「AI 日誌分析」）。
5. PDF：`DiagnosticReportExporter.BuildHtml` → `IPdfRenderer.RenderAsync`（無頭瀏覽器）→ `FileDownloadInterop.SaveBytesAsync`。

## 五、權限與安全

- 管理者限定：頁面以 `CheckIsAdmin()` 守門（AI 分析方法內再檢查一次），判定完成前不渲染、不讀任何日誌；側邊欄由 `AdminOnlyMenuIds` 排除非管理者。
- 日誌路徑只來自設定，使用者無法指定檔名。
- AI 分析會把日誌原文送到外部 AI 服務：先跳費用確認並寫明可能含敏感內容；系統提示詞要求把日誌視為資料、不執行其中的指令，且不輸出 HTML、圖片、連結。
- 稽核：`LogViewer.Export`、`LogViewer.AiAnalyze`（成功／失敗／取消）、`LogViewer.AiAnalyzeExportPdf`；只記筆數、字數、模型與耗時，**不記日誌內容**。

## 六、錯誤與邊界

- `NLog:BasePath` 未設定、目錄不存在、區間內沒有檔案：顯示對應訊息。
- 個別檔案讀不到：列入警示，其餘照常。
- AI 送出量超過模型內容上限：顯示「請縮小時間區間、提高最低等級或減少筆數後再試」。
- AI 失敗：對話窗顯示錯誤訊息；取消後不再跳錯誤。
- 剪貼簿被瀏覽器拒絕：提示改用選取＋Ctrl+C。

## 七、驗收與測試

- `MeetingRecord.Tests/LogQueryServiceTests.cs`（移植自 Starter，18 則）：欄位解析、TraceId 篩選、續行合併、訊息含 `|`、最低等級、關鍵字（含續行、不分大小寫）、取最新 N 筆且時間正序、跨日檔案、區間與筆數夾住、結束時間之後排除、檔案時間戳過舊仍讀得到、格式不符保留、未設定／目錄不存在／區間無檔案。
- `MeetingRecord.Tests/LogTimeRangesTests.cs`、`AiLogPromptBuilderTests.cs`（移植）：快捷區間；只取最新 N 筆、時間正序、不截斷、換行 LF、上限夾住。
- `MeetingRecord.Tests/DiagnosticAiServiceTests.cs`：成功串流並記帳（Feature=5、token、使用者）、失敗記 Failed、取消記 Cancelled、未設定或找不到供應商時不可用。
- `MeetingRecord.Tests/DiagnosticReportExporterTests.cs`：報告 HTML 內容、逸出、檔名、提示詞含防注入規則且只用 LF；`AiUsageFeatureTests`：每個功能別都有顯示文字、數值永不重編。
- `MeetingRecord.Tests/MonitoringMenuRegistrationTests.cs`：id 37 的位置、名稱、路由與管理者限定。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/LogQueryService.cs`、`LogModels.cs`、`NLogFilePathResolver.cs`、`AiLogPromptBuilder.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/Diagnostics/DiagnosticAiService.cs`、`AiDiagnosticPrompts.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/Export/DiagnosticReportExporter.cs`
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Monitoring/LogViewerPage.razor`、`Components/Views/Monitoring/LogViewerView.razor(.cs/.css)`
- 交叉連結：[日誌與設定檔說明](../operations/日誌與設定檔說明.md)、[日誌等級設定 PRD](日誌等級設定-prd.md)、[系統例外紀錄 PRD](系統例外紀錄-prd.md)、[AI 用量分析 PRD](AI用量分析-prd.md)
