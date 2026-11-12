# 系統例外紀錄 PRD

- 文件版本：1.8
- 文件狀態：已實作
- 現行系統版本：1.1.53
- 首次實作版本：0.4.122（移植自 NET10-Blazor-Starter；不含 Email 告警、AI 例外分析只做單次分析）
- 最後核對日期：2026/11/12

## 一、目標與範圍

「系統怪怪的」最常見的來源是程式各處 `catch` 後只記一行日誌、不再往上拋的例外——畫面看起來正常、日誌檔裡卻每天上百次。本功能把這些例外**自動收集成一張表**（相同的合併計次），讓管理者不必翻日誌檔就能看出「什麼錯、在哪一頁、誰遇到、幾次、最近一次何時」，並可交給 AI 分析根因。同時引入「錯誤追蹤碼」，讓 API 錯誤回應的 `TraceId`、日誌與例外紀錄可以互相對照。

- 範圍：收集管線（ILoggerProvider → 佇列 → 背景寫入）、瀏覽器錯誤回報、程序層級未處理例外、啟動失敗補登、自動保留期限、`/system-exceptions` 頁面、AI 例外分析（單次）、錯誤追蹤碼。
- 非範圍：Email／即時告警、AI 多輪追問、跨主機彙整、自動修復、Warning 等級（多半是已處理、降級可用的情況，收進來只是噪音）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-exceptions` | 系統管理 → 監控與診斷 → 系統例外紀錄（id 39） | **只限管理者**（不在角色權限目錄，`SidebarMenuService.AdminOnlyMenuIds`） | 系統管理員／維運 |
| 各頁的錯誤橫幅 | — | 所有使用者 | 看到錯誤提示並告知管理者 |

REST API（1.1.40，只限管理者）：`api/v1/exception-logs` 的 `GET meta`、`POST search`、`GET {id}/stack-trace`、`POST stack-traces`、`POST export/csv`、`DELETE {id}`、`POST purge`、`POST clear-all`、`POST {id}/ai-analysis`（串流）、`POST {id}/ai-analysis/pdf`，見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)。AI 分析送出的內容由伺服器依例外編號組（不含帳號），不收前端的值。頁面的全部動作走這組 API，稽核由伺服器寫。

## 三、畫面與欄位

- **篩選**：最後發生起訖（含時間）、來源（畫面／WebAPI／系統啟動／背景作業／系統／瀏覽器／未知）、使用者帳號（部分比對）、關鍵字（類型／訊息／頁面／操作，或完整的追蹤碼）。
- **工具列**（圖示＋文字）：查詢、重新整理、複製（目前查詢結果含完整堆疊，超過 100 筆先確認）、匯出（CSV，UTF-8 含 BOM）、清除 N 天未再發生（N＝保留天數）、全部清除。清除兩顆都是紅色確認。
- **表格**（遠端分頁，預設依最後發生新到舊；最後發生、次數可排序）：最後發生、次數（≥10 警示色、≥100 錯誤色）、類型（短名，滑過看全名）、訊息、頁面、使用者、操作（查看／刪除）。點任一列複製該筆（含堆疊）。
- **明細對話窗**：例外類型、訊息、來源、頁面、操作（日誌訊息樣板）、記錄器、使用者（帳號＋UserId）、累計次數、首次／最後發生、最後追蹤碼、完整堆疊；按鈕「複製」「AI 分析」。
- **AI 例外分析**：
  1. 確認窗「確認執行 AI 例外分析（會產生費用）」：寫明會送出類型、訊息、頁面、操作與完整堆疊（不含帳號）及字數。OkText「開始分析」。
  2. 對話窗逐段顯示回覆，完成後排成 Markdown：管理者摘要、根本原因、修正建議、排查步驟、需要確認的問題；附例外類型、累計次數、最後發生、模型、用量、耗時。
  3. 「複製結果」「匯出 PDF」（`AI例外分析_yyyyMMdd-HHmmss.pdf`，附錄＝實際送出的例外內容）。
  4. 關閉視窗即取消。LLM 未設定時按鈕停用，滑過顯示原因。
- **錯誤橫幅**：頁面元件出錯時，`WasmLayout` 的 `ErrorBoundary` 顯示純文字「這個頁面發生未預期的錯誤，請重新整理頁面；若持續發生請聯絡系統管理員。」，不顯示追蹤碼；外框與選單照常可用。

### 多選刪除（1.0.53）

- 勾選後「刪除所選」，連同堆疊檔刪除，每筆各寫一次 `ExceptionLog.Delete`。表頭只選這一頁。
- 因為點列會複製，勾選欄是自己做的一般欄位（外層擋掉點擊冒泡），點勾選框不會複製那一列。共通規則見[畫面與欄位字典 0.5](../guides/畫面與欄位字典.md)。

## 四、內部系統運作

1. **追蹤碼**（`TraceCode`）：每個 HTTP 請求一組 8 碼（Crockford Base32），取代 `HttpContext.TraceIdentifier`，所以 API 回應的 `TraceId`、日誌第 2 欄、例外紀錄的「最後追蹤碼」是同一組。
2. **收集**：`ExceptionLogProvider`（ILoggerProvider，與 NLog 並存）收下所有帶例外的 Error／Critical 日誌——服務層既有的 `catch { logger.LogError(ex, …) }` 不必改一行就涵蓋。略過取消、管線自己的記錄器、抑制中的區段；同一個例外實例只收一次（中介層記完重拋、框架又記一次時不會變兩筆）；「操作」取訊息**樣板**而非算好的字串。
3. **情境**（`ExceptionContextAccessor`，AsyncLocal）：HTTP 中介層設來源（`/api` 開頭為 WebAPI，其他為畫面）與路徑，驗證後補使用者與路由樣板；三個背景服務與清理服務設「背景作業」；啟動流程設「系統啟動」。
4. **寫入**：有界佇列（1,000 筆，滿了丟棄並計數）→ `ExceptionLogWriter`（背景服務，每筆自建 scope、全程抑制、關機時 5 秒內清空）→ `ExceptionLogService.RecordAsync`：簽章（SHA-256：類型＋訊息＋頁面＋操作樣板）相同就累加次數、更新最後發生與追蹤碼；否則新增一列並把 `ex.ToString()` 寫成堆疊檔 `ExceptionPath/yyyyMM/{簽章前 16 碼}.txt`。相異列達 5,000 後，新例外一律併入溢位列。
5. **其他來源**：`ProcessExceptionHooks`（未觀察的 Task 例外記為「系統」；程序層級未處理例外寫補登檔）；`BrowserErrorReporter`（瀏覽器的 `error`／`unhandledrejection`，1.1.47 起登入後由 `WasmLayout` 註冊、經 `POST api/v1/client-errors` 回報，來源記為「瀏覽器」；每個帳號每分鐘最多 `ClientErrorReporting:MaxPerCircuitPerMinute` 筆（預設 10），超過的丟棄並計數；忽略外掛腳本；註冊前發生的錯誤由 JS 暫存，註冊時送出）。
6. **啟動失敗補登**：`Program.cs` 的 catch（排除 `HostAbortedException`，`dotnet ef` 不會留假紀錄）先寫 `ExceptionPath/pending/*.json`；下次成功啟動、Migration 之後匯入並刪檔。
7. **保留期限**：`ExceptionLogRetentionBackgroundService` 啟動先跑一次、之後每 24 小時，刪除「最後發生」（本地時間）超過 `LogRetentionSettings:ExceptionLogDays` 天的紀錄與堆疊檔；0＝不清。
8. **AI**：`AiExceptionPromptBuilder` 組使用者訊息（只寫「有／無登入使用者」），系統提示詞 `AiDiagnosticPrompts.ExceptionAnalysis`，經 `DiagnosticAiService` 呼叫並記入 AI 用量（1.1.40 起觸發者經 `IAiCallerProvider` 取，經 API 呼叫也記得到人）（`AiUsageFeature.ExceptionAnalysis = 6`「AI 例外分析」，成功／失敗／取消都記）。1.0.29 起 `DiagnosticAiService` 經 `AiCallGateway`，同時以同一個 CallId 記進 AI 對話紀錄（完整送出內容與回應，關聯說明「例外紀錄 #N（例外類型）」）；送出前檢查 AI 用量上限。

## 五、權限與安全

- 管理者限定：頁面以 `PermissionView.IsAdmin` 守門（刪除、清除、AI 由 API 的 `[AdminOnly]` 擋），判定前不渲染；側邊欄由 `AdminOnlyMenuIds` 排除非管理者。API 整個 Controller `[AdminOnly]`（1.1.40）。
- 管線防遞迴：寫入器、`ExceptionLogService.RecordAsync`、堆疊檔與補登檔存放**不使用 ILogger**（只用 NLog InternalLogger），寫入期間全程抑制收集。
- 紀錄只存帳號與 UserId（首次遇到的人），不存姓名；AI 分析**不送帳號與 UserId**。CSV 有防公式注入。
- 稽核：`ExceptionLog.Delete／Purge／ClearAll／Export／AiAnalyze／AiAnalyzeExportPdf／AutoPurge`，只記編號、筆數、字數、模型與耗時，不記例外內容。
- 瀏覽器回報只在登入後註冊，伺服器端再截斷一次長度、只取路徑，匿名者無從灌資料。

## 六、錯誤與邊界

- 佇列滿：丟棄並計數（不阻塞使用者）；寫入失敗：計入管線監控，不重試。
- 堆疊檔寫入失敗：資料列照樣建立，明細窗顯示「堆疊檔案不存在」並提示改用日誌檢視以追蹤碼查。
- 兩筆同簽章同時新增：唯一索引擋下第二筆，改為累加。
- 刪除列一定同時刪堆疊檔（`ExceptionStackFileStore` 是唯一出入口），不留孤兒檔。
- AI 內容超過模型上限：顯示「這筆例外的內容（多半是堆疊）超過模型的內容上限」。
- 已達 AI 用量上限（1.0.29）：顯示「已達 AI 用量上限…」訊息，不送出、不記帳；記 Warning，**不會**再被收進系統例外紀錄。

## 七、驗收與測試

- 移植：`ExceptionSignatureTests`、`ExceptionLogServiceTests`（合併計次、只寫一次堆疊檔、溢位列、不拋例外、刪除／清空／清除連同檔案、篩選與排序）、`ExceptionLogProviderTests`（只收 Error 以上且有例外、樣板、情境、自身記錄器與抑制、佇列滿、同一實例只收一次）、`ExceptionLogWriterTests`（關機清空、逾時、寫入失敗計數）、`ProcessExceptionHooksTests`、`CrashMarkerStoreTests`、`BrowserErrorReporterTests`、`ExceptionLogClipboardTextTests`、`AiExceptionPromptBuilderTests`（不含帳號）、`TraceCodeTests`。
- 新增：`ExceptionLogRetentionBackgroundServiceTests`（本地時間門檻、0 不清、有刪才寫稽核）；整合測試 `ExceptionLogProvider_ShouldBeRegisteredAlongsideNLog`、`UnhandledApiException_ShouldBeRecordedInExceptionLog_WithTraceCodeAndAccount`（API 例外寫入一筆 WebAPI 紀錄，追蹤碼＝`ApiResult.TraceId`、帳號、頁面＝路由樣板）；`AiUsageFeatureTests`（6）；`DiagnosticReportExporterTests`（例外分析提示詞不含追問）；`MonitoringMenuRegistrationTests`（id 39）。
- 瀏覽器錯誤回報（1.1.47）：`ClientErrorApiTests`（未登入 401、記進系統例外紀錄並帶呼叫者與只留路徑、超過每個帳號上限丟棄）、`BrowserErrorReporterTests`（依帳號限流）；端對端 ERR-01（WASM 頁的 JS 錯誤記進系統例外紀錄）。
- 1.1.40 API：`ExceptionLogApiTests`（授權 401／403、清單與堆疊、CSV、AI 分析串流與不含帳號、中斷記取消、PDF、刪除與清除，稽核 `via=Api`）、`ExceptionLogApiWithoutAiTests`（AI 未設定回 400）。
- 頁面：端對端 EXC-01（明細按 AI 分析邊產生邊顯示、稽核由伺服器寫）；MON-01／02。
- 實跑：`dotnet ef migrations list` 後 `Exception/pending` 沒有補登檔；啟動後 API 回應的 TraceId 與日誌第 2 欄是同一組 8 碼。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/`：`TraceCode`、`ExceptionContextAccessor`、`ExceptionLogProvider`、`ExceptionLogWriter`、`LoggingPipelineMonitor`、`ProcessExceptionHooks`、`CrashMarkerStore`、`BrowserScriptException`、`BrowserErrorReporter`、`AiExceptionPromptBuilder`
- 瀏覽器錯誤回報（1.1.47）：`src/MeetingRecord/MeetingRecord.Web/Controllers/ClientErrorController.cs`、`wwwroot/js/client-error-reporter.js`、`src/MeetingRecord/MeetingRecord.Web.Client/Services/ClientErrorReporter.cs`、`Layout/WasmLayout.razor`（註冊）、`src/MeetingRecord/MeetingRecord.Web/Diagnostics/BrowserErrorReporter.cs`（依帳號限流）、`Web/Configuration/ClientErrorReportingSettings.cs`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ApplicationBuilderExtensions.cs`（`UseHttpRequestLogging`、`UseExceptionContextUser`）、`Auth/RequestActorResolver.cs`、`BackgroundServices/ExceptionLogRetentionBackgroundService.cs`、`Program.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/ExceptionLogService.cs`、`Services/Other/ExceptionStackFileStore.cs`、`Helpers/ExceptionSignature.cs`
- API（1.1.40）：`src/MeetingRecord/MeetingRecord.Web/Controllers/ExceptionLogController.cs`、`Web/Export/ExceptionLogCsv.cs`、`Web/Diagnostics/ExceptionAiFailure.cs`；型別 `src/MeetingRecord/MeetingRecord.Dtos/Diagnostics/`（`ExceptionLogAdapterModel.cs`、`ExceptionLogQuery.cs`、`ExceptionLogApiDtos.cs`）；來源分類 `src/MeetingRecord/MeetingRecord.Share/Helpers/ExceptionSources.cs`
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/ExceptionLog.cs`、`Migrations/20261014052712_AddExceptionLog.cs`
- 頁面：`src/MeetingRecord/MeetingRecord.Web.Client/Pages/Monitoring/ExceptionLogPage.razor(.cs/.css)`、`src/MeetingRecord/MeetingRecord.Web.Client/Api/ExceptionLogApi.cs`；剪貼簿組字 `src/MeetingRecord/MeetingRecord.Dtos/Diagnostics/ExceptionLogClipboardText.cs`；`Web/Export/CsvExport.cs`
- 交叉連結：[日誌檢視 PRD](日誌檢視-prd.md)、[日誌與設定檔說明](../operations/日誌與設定檔說明.md)、[Token 用量 PRD](Token用量-prd.md)、[AI 對話紀錄 PRD](AI對話紀錄-prd.md)（每次 AI 例外分析的送出內容與回應）
