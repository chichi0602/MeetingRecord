# 資料庫用量 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.120
- 首次實作版本：0.4.120（資料庫部分移植自 NET10-Blazor-Starter；「檔案儲存用量」為本專案新增）
- 最後核對日期：2026/10/14

## 一、目標與範圍

讓管理者一眼看出資料庫與檔案佔了多少空間、哪張表或哪個目錄在長大、磁碟還剩多少，作為備份規劃與清理決策的依據。本系統真正吃空間的是資料庫**外**的影音、逐字稿與附件，所以同頁一併列出各外部目錄。

- 範圍：`/database-usage` 唯讀頁面；SQLite 檔案大小、頁面配置、各資料表筆數與資料量估算；各外部目錄大小與檔案數；各磁碟可用空間。
- 非範圍：VACUUM、刪除或搬移檔案、歷史趨勢、自動告警、非 SQLite 資料庫。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/database-usage` | 系統管理 → 監控與診斷 → 資料庫用量（id 30） | **只限管理者**（不在角色權限目錄，`SidebarMenuService.AdminOnlyMenuIds`） | 系統管理員／維運 |

## 三、畫面與欄位

- 工具列：「重新整理」（圖示＋文字，載入中停用）；右側顯示兩段量測耗時（毫秒）。
- **資料庫（SQLite）**
  - 卡片：磁碟占用總計（主檔＋WAL＋SHM；未使用 WAL 時副標寫「只有主檔（未使用 WAL）」）、主資料庫檔（檔名）、已配置（page_count × page_size）、可回收（freelist 頁數，> 0 時警示色，VACUUM 可釋放）。
  - 有 WAL 檔時另顯示說明：WAL 比主檔大是正常的。
  - 各資料表表格（不分頁、可排序）：資料表、紀錄筆數、資料量估算、索引數；估算失敗的格子顯示「—」，滑過看原因。
  - 註腳：資料量估算是各欄位內容的位元組加總，不含索引、頁面碎片與閒置頁，不等於磁碟占用。
- **檔案儲存用量**（本專案新增）
  - 卡片：外部目錄合計（大小、檔案數）；每顆磁碟一張「磁碟 X 可用」（副標列出總容量與放在上面的目錄；可用 < 1 GB 時紅字）。
  - 各目錄表格：目錄（名稱＋用途說明）、路徑（未設定／不存在會標示）、大小、檔案數。目錄依序為資料庫、會議影音、逐字稿、專案附件、AI 問答、上傳暫存、下載、登入金鑰、日誌。
  - 註腳：量的是磁碟現況，孤兒檔也會算進去；「資料庫」目錄與上半部的資料庫檔是同一份，不要重複相加。
- 外部目錄計算中顯示「計算中…（影音目錄檔案多時需要一點時間）」。

## 四、內部系統運作

1. `DatabaseUsageService.GetReportAsync`：以 EF 的 `OpenConnectionAsync` 開連線（不關掉別人開著的連線），檔案路徑取自連線的 `DataSource`（一定是 EF 實際在用的檔案），讀 `PRAGMA page_size／page_count／freelist_count`。
2. 資料表清單取自 `sqlite_master`（含 `__EFMigrationsHistory`、`sqlite_sequence`）；索引數一次查回；每張表 `PRAGMA table_info` 後以一次掃描取得 `COUNT(*)` 與 `SUM(COALESCE(LENGTH(CAST(欄 AS BLOB)),0)+…)`——`CAST AS BLOB` 才是位元組數（中文欄位不轉會少算到三分之一）。每張表各自 try/catch，一張壞表不影響整頁。識別字一律加雙引號跳脫。
3. `StorageUsageService.GetReport`：目錄清單宣告在 `GetTargets()`（`SystemSettings.ExternalFileSystem` 各路徑＋`INLogFilePathResolver.GetLogDirectory()`），逐一以 `DirectorySizeCalculator.MeasureDetailed` 量大小與檔案數；依 `Path.GetPathRoot` 去重後以 `DriveInfo` 讀可用與總容量。
4. 畫面先量資料庫（快）並立刻顯示，再以 `Task.Run` 在背景執行緒量外部目錄，不卡住 Blazor circuit；`isLoading` 早退避免同一個 DbContext 被並行使用。

## 五、權限與安全

- 管理者限定：頁面以 `CheckIsAdmin()` 守門，權限判定完成前不渲染；側邊欄由 `AdminOnlyMenuIds` 排除非管理者。
- 唯讀：不提供 VACUUM、刪檔等寫入動作。
- 路徑只來自設定，使用者無法指定。

## 六、錯誤與邊界

- 整體讀取失敗：顯示「讀取資料庫用量失敗：例外型別」。
- 個別資料表估算失敗：該列資料量顯示「—」並列入警示，仍嘗試取回筆數。
- 目錄未設定或不存在：照樣列出，標示「未設定」／「不存在」，大小 0。
- 讀不到磁碟資訊：列入警示，不影響其他列。
- 個別檔案讀不到（寫入中、無權限）略過不計，大小與檔案數是同一批檔案。

## 七、驗收與測試

- `MeetingRecord.Tests/DatabaseUsageServiceTests.cs`（移植自 Starter）：中文以位元組計、NULL 欄位不讓整列變 NULL、空表為 0 而非 null、筆數、具名與 UNIQUE 索引數、含雙引號的表名、包含系統表、頁面統計可讀、記憶體資料庫降級、記錄耗時。
- `MeetingRecord.Tests/StorageUsageServiceTests.cs`：各目錄大小與檔案數、不存在／未設定的目錄照樣列出、同一顆磁碟合併。
- `MeetingRecord.Tests/DashboardMetricsTests.cs`：`DirectorySizeCalculator.MeasureDetailed` 含子目錄的檔案數。
- `MeetingRecord.Tests/MonitoringMenuRegistrationTests.cs`：選單 id 30 位置、名稱、路由與管理者限定。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/DatabaseUsageService.cs`、`DatabaseUsageModels.cs`、`StorageUsageService.cs`
- `src/MeetingRecord/MeetingRecord.Share/Helpers/DirectorySizeCalculator.cs`（`MeasureDetailed`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Monitoring/DatabaseUsagePage.razor`、`Components/Views/Monitoring/DatabaseUsageView.razor(.cs/.css)`
- 交叉連結：[儀表板 PRD](儀表板-prd.md)（儲存空間卡片）、[系統健康監控 PRD](系統健康監控-prd.md)、[資料模型與資料庫](../architecture/資料模型與資料庫.md)
