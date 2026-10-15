# 稽核紀錄 PRD

- 文件版本：1.1
- 文件狀態：部分實作（第一批查詢頁與基礎建設 1.0.32、第二批帳號安全事件與權限拒絕 1.0.33 已實作；業務資料事件在第三批補上）
- 現行系統版本：1.0.33
- 首次實作版本：樣板時期（0.4.25 以前，寫入 `AuditLog` 表，migration `AddAuditLog`）、1.0.32（查詢頁、來源 IP、保留期限）、1.0.33（帳號安全事件、權限拒絕）；移植自 NET10-Blazor-Starter 的 /audit-logs
- 最後核對日期：2026/10/15

## 一、目標與範圍

系統早就把登入、使用者與角色異動、各管理頁的匯出與清除寫進 `AuditLog` 表，但一直沒有任何畫面可以看，只能直接查 SQLite。本功能提供查詢頁，回答「誰、何時、從哪裡、對什麼、做了什麼、結果如何」，並補上來源 IP、保留期限與清除。

- **範圍（1.0.32，第一批）**：
  - 查詢頁 `/audit-logs`：篩選、明細、CSV 匯出、清除 N 天前、全部清除。
  - 來源 IP：寫入服務自己取得，呼叫端不用改。
  - 保留期限：每天自動清除超過 `LogRetentionSettings:AuditLogDays`（預設 365）天的紀錄。
  - 動作代碼集中成 `AuditActions` 常數並附中文名稱。
  - 寫入服務改用自己的短命 DbContext。
- **範圍（1.0.33，第二批）**：
  - 登出（使用者自己登出、被系統強制登出並註明原因）、Google 登入（成功／失敗／待審核、連結既有帳號、自動建立帳號）、API 權杖更新失敗。
  - 登入成功改由呼叫端在真的登入之後才寫，並以 `via=Web`／`via=Api` 區分入口。
  - 密碼異動（首次登入改密碼、右上角變更密碼、設定 API 密碼、管理者重設密碼）、使用者啟停用、加入／移出團隊。
  - 權限拒絕：API 回 403、頁面顯示「你沒有權限存取此頁面」。
- **後續批次（已規劃，尚未實作）**：
  - 第三批：專案、會議、待辦、團隊、分類、提示詞的新增／修改／刪除／啟停用；檔案上傳／刪除／下載；PDF 匯出；會議流程動作（重新轉錄、歸屬／移出專案、產生／修改草稿、待辦完成）；AI 問答的對話操作（新增、改名、清除歷程、修改訊息）。
- **非範圍**：
  - 單筆刪除（刻意不提供，稽核紀錄是追究責任的證據）。
  - Web API（內部管理頁）。
  - 修改前後值（只記非敏感摘要）。
  - 重複登入失敗的告警。
  - 登入頁驗證碼錯誤、欄位空白（還沒驗證帳密，只寫日誌）。
  - 資料範圍擋下的存取（看不到的專案／會議當成不存在、回 404），留待第三批評估。
  - JWT 政策型的 403（`Program.cs` 的 `OnForbidden`，目前只有測試探針會走到）。
  - 給非管理者看。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/audit-logs` | 系統管理 → 帳號與權限（id 35）→ 稽核紀錄（id 40，`fact_check`） | **只限管理者**（`CheckIsAdmin()`＋`SidebarMenuService.AdminOnlyMenuIds`；不在權限目錄，不能勾給角色） | 系統管理員 |

## 三、畫面與欄位

### 篩選列

| 欄位 | 控制項 | 比對方式 |
| --- | --- | --- |
| 發生時間（起）～（迄） | 兩個含時分秒的日期選擇器 | 本地時間轉成 UTC 後比對，兩端都含 |
| 動作 | 下拉，第一項「動作：不限」；選項顯示中文名稱 | 代碼完全相同。選項取自資料庫實際出現過的代碼，舊代碼也篩得到 |
| 結果 | 下拉：不限／成功／失敗 | 相等 |
| 操作者帳號 | 文字 | 部分比對 |
| 來源 IP | 文字 | **前綴**比對（`10.0.` 不會比到 `110.0.0.1`） |
| 關鍵字 | 文字 | 動作代碼、操作者帳號、目標類型、目標識別、摘要、IP 任一部分比對 |

按「查詢」才套用；換頁、排序與匯出都用按查詢當下的條件。

### 工具列

| 按鈕 | 行為 |
| --- | --- |
| 查詢 | 回到第一頁重查 |
| 重新整理 | 重新載入動作選單與資料 |
| 匯出 | 目前條件下的全部資料（最多 10,000 筆，超過時提示），CSV，UTF-8 含 BOM；沒有資料時停用 |
| 清除 N 天前 | N＝`AuditLogDays`；設為 0（不自動清）時用 365。確認後執行 |
| 全部清除 | 紅色確認後執行 |

### 說明列

說明記錄範圍、時間以伺服器本地時區顯示（資料庫存 UTC）、清除本身也會留下紀錄，以及目前的保留天數。

### 表格

伺服器端分頁，預設新到舊；只有「發生時間」可排序（第二鍵為 Id，同一秒的多筆分頁不會重複或漏掉）。

| 欄位 | 內容 |
| --- | --- |
| 發生時間 | 本地時間 `yyyy-MM-dd HH:mm:ss` |
| 結果 | 成功（綠）／失敗（紅）膠囊 |
| 動作 | 中文名稱膠囊，依類別換底色，滑過顯示代碼；查不到中文的舊代碼直接顯示代碼 |
| 操作者 | `帳號（Id=N）`；系統排程或匿名顯示「（系統／匿名）」 |
| 來源 IP | 沒有時顯示「—」（排程與 1.0.32 以前的紀錄） |
| 目標 | `類型#識別` |
| 摘要 | 過長省略 |
| 操作 | 查看 |

### 明細窗（`form-modal-large`，唯讀）

發生時間（本地）、發生時間（UTC）、結果、動作名稱、動作代碼、操作者、來源 IP、目標類型、目標識別、摘要全文（空的顯示「（無）」）。

### CSV 欄位

發生時間（本地）、結果、動作名稱、動作代碼、操作者帳號、操作者Id、目標類型、目標識別、摘要、來源IP。檔名 `MeetingRecord-audit-yyyyMMdd-HHmmss.csv`。以 `=`、`+`、`-`、`@` 開頭的儲存格前補單引號（`CsvExport`，防公式注入）。

## 四、內部系統運作

- **資料表** `AuditLog`：`OccurredAt`（**UTC**）、`ActorUserId`、`ActorAccount`（快照）、`Action`、`TargetType`、`TargetId`、`Detail`、`Success`、`ClientIp`（1.0.32，最長 64）。
  - 索引（1.0.32，migration `AddAuditLogClientIpAndIndexes`）：`OccurredAt`、`Action`、`(ActorUserId, OccurredAt)`。
- **寫入** `IAuditLogService` → `AuditLogService`（Business/Services/Other）：
  - 1.0.32 起每次用同一組 `DbContextOptions<BackendDBContext>` 自己 new 一個短命 context 寫入。以前借用整條 Blazor 連線共用的 context：SaveChanges 會順便存掉呼叫端還沒存的變更，失敗時那筆稽核又留在追蹤器裡，下次存檔被重送。
  - 寫入失敗只記 Warning，不中斷業務動作。
  - 不要在交易裡面呼叫（新 context 走另一條連線，SQLite 會等交易的寫入鎖）；目前一律在 Commit 之後寫。
- **來源 IP** `IClientIpProvider` → `Web/Auth/ClientIpProvider`（scoped）：
  - 先用 Blazor 連線建立／重連時由 `ApplicationCircuitHandler` 記下的 IP，其次用目前 `HttpContext` 的連線 IP。IPv4-mapped IPv6 轉回 IPv4。
  - 反向代理後面靠既有的 `UseForwardedHeaders`（X-Forwarded-For）。
  - 取 IP 失敗不影響寫入（記 null）。
- **查詢** `AuditLogQueryService`（Business/Services/DataAccess）：`GetAsync`、`GetForExportAsync`、`GetDistinctActionsAsync`、`PurgeBeforeAsync(utc)`、`PurgeAsync(days)`、`ClearAllAsync`。UTC↔本地的三個轉換點（篩選、顯示、清除門檻）集中在這裡；SQLite 讀回來是 `Kind=Unspecified`，先標成 UTC 再轉。刪除用 `ExecuteDelete`，不經變更追蹤。
- **自動清除** `AuditLogRetentionBackgroundService`：啟動時先跑一次，之後每 24 小時；門檻 `DateTime.UtcNow - AuditLogDays`；有刪才寫 `Audit.AutoPurge`（摘要 `rows=..; days=..`，沒有操作者）。
- **動作代碼** `AuditActions`（Share/Helpers）：常數＋`DisplayNames` 中文對照。代碼一經使用就不改名。

### 目前記錄的動作（1.0.33）

| 類別 | 代碼 |
| --- | --- |
| 登入 | `Login.Success`（`via=Web`／`Api`）、`Login.Failed`、`Login.LockedOut`、`Login.Disabled`、`Login.GoogleSuccess`／`GoogleFailed`／`GooglePending` |
| 登出與權杖 | `Logout.Manual`、`Logout.Forced`（`reason=invalid-claim`／`deleted`／`disabled`／`no-role`／`permission-error`）、`Token.RefreshFailed`（`reason=AccountStateChanged`／`InvalidToken`） |
| 密碼 | `Password.Change`（`via=FirstLogin`／`Dialog`，目前密碼錯誤時記失敗）、`Password.ApiSet`、`Password.Reset`（管理者在編輯視窗填新密碼） |
| 使用者與角色 | `User.Create`／`Update`／`Delete`／`Enable`／`Disable`／`TeamAdd`／`TeamRemove`／`GoogleLink`／`GoogleCreate`、`Role.Create`／`Update`／`Delete` |
| 權限 | `Permission.Denied`（目標 `ApiEndpoint#{方法} {路徑}` 或 `Page#{路由}`） |
| AI 用量上限、AI 對話紀錄、Token 用量 | `Ai.QuotaBlocked`、`AiCallLog.View`／`DownloadJson`／`ExportPdf`／`Export`／`Delete`／`Purge`／`ClearAll`／`AutoPurge`、`AiUsage.Export`／`ExportPdf`／`Delete`／`Purge`／`ClearAll`／`AutoPurge` |
| 監控與診斷 | `ExceptionLog.Export`／`Delete`／`Purge`／`ClearAll`／`AutoPurge`／`AiAnalyze`／`AiAnalyzeExportPdf`、`LogLevel.Apply`／`Restore`、`LogViewer.Export`／`AiAnalyze`／`AiAnalyzeExportPdf` |
| 稽核紀錄本身 | `Audit.Export`、`Audit.Purge`、`Audit.ClearAll`、`Audit.AutoPurge` |

### 寫入規則（1.0.33）

- **登入成功由呼叫端寫**：`MyUserServiceLogin.LoginAsync` 只寫失敗類（帶 `via`）。帳密對了不代表真的登入了——API 會因為必須先改密碼而拒絕（寫 `Login.Failed reason=MustChangePassword`），網頁簽入 Cookie 也可能失敗——所以 `Login.Success` 由網頁在 `SignInAsync` 之後、API 在發出權杖之後才寫。
- **強制登出**：`AuthenticationStateHelper.Check` 把人踢出時導向 `/Auths/Logout?reason=…`（`LogoutReasons`）。登出頁在 `SignOutAsync` 之前取身分，原因在白名單裡寫 `Logout.Forced`，否則有身分就寫 `Logout.Manual`；沒身分也沒原因不寫。使用者手動帶 `?reason=` 只會改變自己那筆的分類，沒有危害。
- **頁面權限拒絕**：各頁在拒絕分支呼叫 `AuthenticationStateHelper.RecordPageAccessDeniedAsync(route)`。刻意不放進 `CheckAccessPage`／`CheckIsAdmin`，因為側邊欄過濾每個選單項目都會呼叫它們。`PageAccessDeniedAuditTests` 掃原始碼守門。管理頁執行動作前的靜默 `CheckIsAdmin` 不記。
- **API 權限拒絕**：`HasPermissionAttribute` 回 403 前寫入；寫入失敗照樣回 403。401（未登入）不記。
- **自己改密碼**：操作者與目標都是本人。欄位空白、兩次不一致這類輸入錯誤不記；目前密碼錯誤記失敗。
- **團隊成員**：實際異動 0 人時不寫；摘要最多列 50 個使用者編號。

## 五、權限與安全

- 只限管理者；頁面判定完成前不渲染任何內容，也不讀資料。
- 清除與全部清除前再檢查一次管理者身分。
- 清除類動作一律在刪除**之後**才寫稽核，這筆才會留在清空後的表裡——「誰清了稽核紀錄」永遠答得出來。
- 摘要只放非敏感資訊：名稱、數量、編號、檔名；不放密碼、會議逐字稿與紀錄內容、AI 問答內容、提示詞本文。

## 六、錯誤與邊界

- 載入失敗顯示錯誤訊息，表格清空。
- 清除後停在已不存在的頁時，自動夾回最後一頁。
- 匯出超過 10,000 筆只匯出最新 10,000 筆並提示。
- 時間只依伺服器時區顯示，不依瀏覽器時區。
- 1.0.32 以前的紀錄沒有 IP。

## 七、驗收與測試

- `AuditLogQueryServiceTests`：排序與分頁、本地／UTC 轉換（篩選、顯示、清除門檻）、每種篩選、匯出、動作選項、清除與全部清除、顯示文字。
- `AuditLogServiceTests`：寫入、失敗不拋例外、**不會順便存掉呼叫端未存的變更**、IP 寫入、取 IP 失敗仍寫入。
- `ClientIpProviderTests`：連線 IP 優先、IPv4-mapped 轉換、沒有連線時為 null。
- `AuditLogRetentionBackgroundServiceTests`：過期才刪、刪完才寫 `Audit.AutoPurge`、0 天不清、沒有可刪不寫。
- `AuditActionsConventionTests`：每個代碼都有中文名稱且符合 `名詞.動作`；原始碼不得以字串常值寫稽核。
- `AuditLogMenuRegistrationTests`：選單位置、名稱、網址、圖示、只限管理者、非管理者即使有同名權限也看不到。
- 1.0.33：
  - `MyUserServiceLoginTests`：成功不寫稽核（由呼叫端寫）、失敗摘要帶 `via`。
  - `GoLiveReviewApiTests`：API 登入成功 `via=Api`、必須先改密碼只記失敗、停用後換發與無效權杖記 `Token.RefreshFailed`。
  - `ApiIntegrationTests`：無權限呼叫拿到 403 時有 `Permission.Denied`。
  - `AuditEventsTests`：管理者重設密碼、首次登入改密碼（成功與目前密碼錯誤）、對話窗改密碼、設定 API 密碼、啟停用、加入／移出團隊。
  - `ExternalLoginServiceTests`：連結既有帳號、自動建立、既有連結不寫。
  - `LogoutAuditTests`：強制登出原因白名單、主動登出、未登入不寫。
  - `AuthenticationStateHelperTests`：踢出時導向帶原因的登出頁、`RecordPageAccessDeniedAsync`。
  - `PageAccessDeniedAuditTests`：每個拒絕分支都有寫稽核。

## 八、相關程式與文件

- `Share/Helpers/AuditActions.cs`、`AccessDatas/Models/AuditLog.cs`、`Migrations/*_AddAuditLogClientIpAndIndexes.cs`
- `Business/Services/Other/AuditLogService.cs`、`IClientIpProvider.cs`、`Business/Services/DataAccess/AuditLogQueryService.cs`
- `Models/Systems/AuditLogQuery.cs`、`LogRetentionSettings.cs`、`Models/AdapterModel/AuditLogAdapterModel.cs`
- `Web/Auth/ClientIpProvider.cs`、`Web/Components/ApplicationCircuitHandler.cs`、`Web/BackgroundServices/AuditLogRetentionBackgroundService.cs`
- `Web/Components/Pages/Admins/AuditLogPage.razor`、`Views/Admins/AuditLogView.razor(.cs/.css)`
- 1.0.33：`Share/Helpers/AuditVia.cs`（`AuditVia`、`LogoutReasons`）、`MyUserServiceLogin`、`Login.razor.cs`、`Logout.razor.cs`、`AuthController`、`ExternalAuthController`、`ExternalLoginService`、`MyUserService`、`AuthenticationStateHelper`、`HasPermissionAttribute`、17 個頁面的拒絕分支
- [認證授權與權限機制](../security/認證授權與權限機制.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §8.3、變更紀錄：[第一批](../changelog/2026-10-15-稽核紀錄查詢頁與基礎建設.md)、[第二批](../changelog/2026-10-15-稽核紀錄帳號安全事件與權限拒絕.md)
