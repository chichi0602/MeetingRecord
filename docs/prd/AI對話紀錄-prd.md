# AI 對話紀錄 PRD

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：1.0.53
- 首次實作版本：1.0.29（記錄）、1.0.30（頁面）；移植自 NET10-Blazor-Starter 的 /ai-call-logs
- 最後核對日期：2026/10/20

## 一、目標與範圍

AI 的花費在「Token 用量」看得到，但「當時送了什麼、AI 回了什麼」以前完全沒有留下。會議紀錄品質不好、待辦抽錯、分析結論奇怪時，沒有辦法回頭查。本功能把每一次付費 AI 呼叫的完整送出內容與回應記錄下來，讓管理者可以查詢、比對、匯出。

- **範圍**：
  - 記錄：所有經過 `AiCallGateway` 的呼叫，包括會議紀錄生成（含分段摘要）、語音轉錄、AI 問答、抽出待辦、AI 日誌分析、AI 例外分析，以及 1.0.35 起系統健康監控的「測試 AI 連線」（關聯說明「AI 連線測試：文字生成／語音轉錄」）、1.0.43 起使用說明頁的「問 AI 助理」（功能別「使用說明 AI 助理」，關聯說明「使用說明提問：＜問題前 40 字＞」，同一次開頁的連續提問共用 ThreadKey `help:{guid}`）。
  - 查詢頁 `/ai-call-logs`：篩選、明細、CSV、JSON、PDF。
  - 保留期限與手動清除；刪除會議或專案時連帶刪除。
- **非範圍**：
  - 重新送出（重播）一次呼叫。
  - 編輯紀錄。
  - 保存音訊或圖片本身。
  - 給非管理者看。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/ai-call-logs` | 系統管理 → AI 管理（id 62）→ AI 對話紀錄（id 63，`forum`） | **只限管理者**（`CheckIsAdmin()`＋`SidebarMenuService.AdminOnlyMenuIds`；不在權限目錄） | 系統管理員 |
| `/ai-call-logs?callId=…` | 從 Token 用量明細窗的「查看 AI 對話紀錄」跳過來（1.0.31 起），直接開啟同一個呼叫識別碼的那一筆 | 同上 | 系統管理員 |

## 三、畫面與欄位

- **篩選**：開始日期、結束日期（含當天）、功能、結果（成功／失敗／已取消）、使用者、模型、關鍵字。
  - 使用者與模型的下拉，只列資料庫裡實際出現過的值。
  - 關鍵字比對關聯說明；貼上完整的呼叫識別碼會直接找到那一筆。
- **工具列**：
  - 查詢、重新整理。
  - 匯出 CSV：只有中繼資料，不含內容；最多 10,000 筆。
  - 清除此日之前（日期＋紅色確認）、全部清除（紅色確認）。
- **說明列**：記錄範圍、敏感性、保留天數，以及「刪除會議或專案時一併刪除」。停用記錄時會註明。
- **表格**：
  - 欄位：時間、功能、結果膠囊（失敗為錯誤色、取消為警示色，滑過顯示錯誤訊息；有部分回應時會註明）、使用者（系統呼叫顯示「（系統）」）、關聯說明、模型、耗時、送出／回應字元、操作（查看／刪除）。
  - 遠端分頁，預設新到舊；時間、耗時、送出字元三欄可以排序。
- **明細窗**（`form-modal-large`）：
  - **中繼資料**：時間、功能、結果與錯誤、使用者（含 Id）、對象（會議或專案編號）、關聯說明、供應商／模型、HTTP 狀態、耗時、用量與費用、呼叫識別碼。
    - 用量與費用取自用量帳本中同一個呼叫識別碼的那一列：token 或音訊時長、估算金額與換算後的台幣。
    - 帳本已被清除時會註明。
  - **同一趟工作**：同一個 ThreadKey 的所有呼叫依時間列出，點一下就切換。例如同一次會議紀錄生成的各段摘要與最終整理、同一段 AI 問答、同一個音檔的各段轉錄。
  - **檢視方式**：完整對話、送出內容、回應三種。
    - 回應預設以 Markdown 顯示（`MarkdownRenderer` 的安全輸出），可切換純文字。
    - 系統指示與送出內容一律以純文字顯示。
    - 每個區塊都有字數與「複製」。
  - 語音轉錄的送出內容改顯示檔名、第幾段、時長（推估時會註明），並說明音訊不保存。圖片只列類型與大小。
  - 單一區塊超過 20 萬字時，改成純文字並截斷顯示，完整內容請下載 JSON。
  - **下載 JSON**：內容檔原文。
  - **匯出 PDF**：內文是回應，附錄是送出內容，表頭是中繼資料。
  - 內容檔不存在時，顯示警示並只保留摘要。

### 多選刪除（1.0.53）

- 勾選後「刪除所選」，連同內容檔刪除、不影響 Token 用量；每筆各寫一次稽核 `AiCallLog.Delete`，與單筆相同。表頭只選這一頁。共通規則見[畫面與欄位字典 0.5](../guides/畫面與欄位字典.md)。

## 四、內部系統運作

1. **記錄**（1.0.29）：`AiCallGateway` 在每次呼叫結束時寫入，成功、失敗、取消都記。
   - 用量帳本 `AiUsageLog` 與 AI 對話紀錄用同一個 `CallId` 一對一對應。
   - 串流中途取消或失敗時，已收到的部分回應也會留下（`IsResponsePartial`）。
2. **儲存**：
   - 資料表 `AiCallLog` 只存中繼資料，沒有外鍵。
   - 內容存成 JSON 檔 `AiCallLogPath/yyyyMM/{CallId}.json`（`AiCallLogEntry`，SchemaVersion 1）。
   - 寫入順序是先寫檔、再寫列；列寫不進去就刪檔。
   - 檔案只透過 `AiCallLogFileStore` 寫入與刪除。
3. **不存的東西**：API 金鑰、HTTP 標頭、網址 query、圖片的位元組、音訊。
4. **查詢**：`AiCallLogService` 的 `GetAsync`／`GetForExportAsync`／`GetFilterOptionsAsync`／`FindIdByCallIdAsync`／`GetDetailAsync`。
5. **刪除**：
   - 手動：`DeleteAsync`／`PurgeBeforeAsync`／`ClearAllAsync`，一律連同內容檔。
   - 刪除會議時：`DeleteByMeetingAsync`。
   - 刪除專案時：`DeleteByProjectAsync`，只刪沒有 MeetingId 的專案層級紀錄，因為會議在刪除專案後仍然存在。
6. **保留期限**：`AiLogRetentionBackgroundService` 每天依 `AiCallLogSettings:RetentionDays`（預設 90，範圍 1～3650，沒有永久保留）清除，並刪掉更早月份的孤兒檔。停用記錄時照樣清除。
7. **設定**：`AiCallLogSettings:Enabled`（預設 true）關掉後不再寫新的對話紀錄，用量帳本照記。

## 五、權限與安全

- **只限管理者**：
  - 頁面以 `CheckIsAdmin()` 守門，判定前不渲染。
  - 刪除、清除在方法內再檢查一次。
  - 側邊欄以 `AdminOnlyMenuIds` 排除非管理者。
- **敏感內容**：內容含會議逐字稿、會議紀錄、專案文件與 AI 問答全文。
  - `AiCallLogPath` 只開放給應用程式身分，不可放在網站目錄或下載目錄內。
  - 寫入備份政策時要考慮這些內容。
- **稽核**：只記編號與呼叫識別碼，不記內容。
  - 查看也記：`AiCallLog.View`。
  - 其他動作：`DownloadJson`、`ExportPdf`、`Export`、`Delete`、`Purge`、`ClearAll`、`AutoPurge`。
- **CSV 不含內容**：要看內容就得打開明細，而打開明細會留下稽核。
- **PDF**：所有字串都經過 HTML 跳脫，回應的 Markdown 走安全輸出。

## 六、錯誤與邊界

- **內容檔遺失**（被清除或寫檔失敗）：明細仍顯示摘要，並提示原因。
- **用量帳本被清除**：明細的「用量與費用」會註明查不到。
- **深層連結找不到**：顯示「可能已超過保留天數被清除、已手動刪除，或當時停用了記錄」。
- **內容過大**：截斷顯示，完整內容以下載 JSON 取得。
- **最後一頁的唯一一筆被刪掉**：頁碼自動回到最後一頁。

## 七、驗收與測試

- `AiCallGatewayTests`、`AiCallGatewayUsageTests`：1.0.29 的記錄規則。
- `AiCallLogServiceTests`：
  - 寫入、停用、截斷、寫檔或資料庫失敗。
  - 連帶刪除、保留期限。
  - 1.0.30 加入：篩選、排序、分頁、用呼叫識別碼找、明細（內容、用量、同一趟工作、檔案遺失）、刪除與清除、篩選選項、CSV 不含內容、三種檢視方式、轉錄的送出內容。
- `AiManagementMenuRegistrationTests`：選單、只限管理者、不在權限目錄。
- `RbacBackfillServiceTests`：「AI 用量分析」退役。
- **人工驗收**（見上架前人工測試清單）：
  - 管理者看得到「系統管理 → AI 管理 → AI 對話紀錄」。
  - 做一次 AI 問答後，清單出現一筆，明細看得到問題與回答。
  - 一次會議紀錄生成在「同一趟工作」裡列出各段。
  - 下載 JSON 與匯出 PDF 中文正常。
  - 刪除會議後，該會議的紀錄消失。
  - 非管理者看不到選單，直接打網址顯示無權限。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Business/Services/AiUsage/AiCallGateway.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/AiCallLogService.cs`、`Services/Other/AiCallLogFileStore.cs`
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/AiCallLog.cs`、`Models/Systems/AiCallLogModels.cs`、`AiCallLogQuery.cs`、`AiCallLogSettings.cs`
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/AiManagement/AiCallLogPage.razor`、`Components/Views/AiManagement/AiCallLogView.razor(.cs/.css)`
- `src/MeetingRecord/MeetingRecord.Web/BackgroundServices/AiLogRetentionBackgroundService.cs`
- 交叉連結：[Token 用量 PRD](Token用量-prd.md)、[日誌與設定檔說明](../operations/日誌與設定檔說明.md)、[正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)
