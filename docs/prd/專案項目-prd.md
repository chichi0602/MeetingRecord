# 專案項目 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.24
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/08/17

## 一、目標與範圍

提供「專案項目（Project）」的建立、查詢、修改、刪除與附件管理能力，同時作為新增其他領域 CRUD 模組時的參考樣板。

- 範圍：清單查詢（關鍵字搜尋、分類／團隊過濾、排序、分頁）、單筆維護（含表單驗證）、多檔附件上傳／下載／刪除、動作級授權與團隊可見範圍控管。
- 非範圍：專案間的相依關係／甘特圖、工時統計、跨專案報表、附件線上預覽；本腳手架不含 LLM／RAG 能力。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/projects`（`ProjectPage.razor`，掛 `MainLayout`） |
| REST API | `api/Project`、`api/v1/Project`（JWT Bearer） |
| 選單路徑 | 專案管理（`Menu.json` id=2）→ 專案項目（id=21） |
| 權限鍵 | 資源鍵「專案項目」（`MagicObjectHelper.角色_專案項目`），動作 `view`／`create`／`edit`／`delete`；管理員短路 |
| 主要使用者 | 具「專案項目」對應動作權限的登入者；管理員可見全部 |

> 頁面進入檢查用 `CheckAccessPage(角色_專案管理)`（群組鍵「專案管理功能」），工具列與操作鈕再以 `CheckAccessAction(角色_專案項目, 動作)` 個別控管。

## 三、畫面與欄位

- 工具列：新增、重新整理、分類過濾（多選）、團隊過濾（多選）、關鍵字輸入、清空、搜尋。
- 清單欄位（`ProjectViewView.razor:72-83`）：標題、描述、開始日期、結束日期、狀態、優先級、完成百分比、負責人、分類、團隊、建立時間、更新時間；標題預設遞增排序。
- 可排序欄位（`ProjectService.cs:81-155`）：Title、StartDate、EndDate、Status、Priority、CompletionPercentage、Owner、CreatedAt、UpdatedAt。
- 搜尋比對欄位（`ProjectService.cs:57-62`）：Title、Description、Status、Priority、Owner。
- 分頁：`RemoteDataSource`，預設每頁 `MagicObjectHelper.PageSize`。
- 編輯表單欄位（`Project` 實體 / `ProjectCreateUpdateDto`）：標題（必填）、描述、開始日期、結束日期、狀態（必填，`StatusOptions`）、優先級（必填，`PriorityOptions`）、完成百分比（0-100）、負責人（必填）、分類（多值標籤）、團隊（多值標籤，不設定＝公開）。
- 附件：`專案附件` 一次可多選，單檔上限 1GB；待上傳清單可移除，已上傳檔案可下載（`/api/project-files/{id}/download`）或標記移除。

## 四、內部系統運作

- 資料流：`ProjectPage.razor` → `ProjectViewView`（`.razor.cs`）→ `ProjectService` → `BackendDBContext.Project`。REST API 走 `ProjectController` → `ProjectRepository`（與 UI 的 Service 為兩條路徑，皆回 `ApiResult`）。
- 讀取：清單 `GetAsync(DataRequest)` 使用 `AsNoTracking`；單筆 `GetAsync(int)` 以 `Include(x => x.Files)` 帶附件。
- 編輯前處理：開啟修改視窗時以 `ProjectService.GetAsync(id)` 重新取得資料副本（非重用清單物件），並清空待上傳／待移除清單（`ProjectViewView.razor.cs:228-237`）。
- 寫入前清追蹤：`AddAsync`／`UpdateAsync`／`DeleteAsync` 進入時皆呼叫 `CleanTrackingHelper.Clean<Project>(context)`（`ProjectService.cs:201,233,286`）。
- 附件 Adapter：UI 以 `ProjectUploadFileInput`（FileName/ContentType/FileSize/Content）傳入；Service 依主表 `CreatedAt` 年／月建立目錄，檔名以 GUID 產生，落地後寫入 `ProjectFile`；刪除主表時先刪實體檔再刪紀錄。
- Migration：模型異動需在 `MeetingRecord.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- 動作級授權：`ProjectController` 各端點標註 `[HasPermission(角色_專案項目, 動作)]`（`ProjectController.cs:36,67,113,152,201`）；無權限回 403 且維持 `ApiResult` 結構；管理員短路。
- UI 與 API 共用同一 RBAC 權威（`IPermissionChecker`）。
- 團隊可見範圍：非管理員清單以 `TagStringHelper.BuildTeamAccessPredicate` 只看到公開（無團隊）或與自身團隊交集的專案；單筆／附件下載以 `IsTeamAccessible` 守門，越界回空模型或 `null`（`ProjectService.cs:75-79,185-190,360-365`）。

## 六、錯誤與邊界

- 標題重複：`Create` 回 409、`Update` 回 409（`ExistsByNameAsync`）。
- 路由 ID 與 payload ID 不一致：`Update` 回 400。
- 結束日期早於開始日期、狀態／優先級不合法、完成百分比超出 0-100、未設定附件根目錄：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 回失敗訊息。
- 附件超過 1GB：前端即時提示並略過，後端再次驗證。
- 刪除時仍有關聯資料（FK 衝突）：回「此專案仍有關聯資料，無法刪除」。

## 七、驗收與測試

- `MeetingRecord.Tests/ProjectServiceTeamAccessTests.cs`：管理員可見全部（3 筆）、非管理員僅見公開＋交集團隊、無團隊者僅見公開、團隊過濾、單筆越界守門回空模型。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`、`RbacBackfillServiceTests.cs`：動作級授權鍵與 RBAC 回填涵蓋「專案項目」。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Projects/ProjectPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Projects/ProjectViewView.razor.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/ProjectService.cs:1`
- `src/MeetingRecord/MeetingRecord.Web/Controllers/ProjectController.cs:1`
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/Project.cs:1`、`ProjectFile.cs:1`
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs:30`
- 交叉連結：[Web API 設計慣例](../architecture/Web%20API%20設計慣例.md)、[檔案上傳機制](../features/檔案上傳機制.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)

> 備註：UI 以 `/api/project-files/{id}/download` 下載附件，Service 端 `GetFileDownloadAsync` 具團隊權控守門；對外端點的實際註冊位置未在現行 Controllers 找到（未確認）。
