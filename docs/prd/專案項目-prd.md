# 專案項目 PRD

- 文件版本：2.2
- 文件狀態：已實作
- 現行系統版本：0.4.37
- 首次實作版本：既有腳手架核心功能（0.4.31 頁面全面改版）
- 最後核對日期：2026/09/02

## 一、目標與範圍

提供「專案項目（Project）」的建立、查詢、修改、刪除與附件管理能力，同時作為新增其他領域 CRUD 模組時的參考樣板。

- 範圍：專案選擇與單筆維護（含表單驗證）、多檔附件上傳／下載／刪除、動作級授權與團隊可見範圍控管；**0.4.31 起**另含「挑一份逐字稿以 AI 產生會議紀錄」與本專案的歷史會議紀錄清單（檢視／編修）。
- 非範圍：專案間的相依關係／甘特圖、工時統計、跨專案報表、附件線上預覽；待辦事項的抽取與管理（TodoList 尚未實作）。

> **0.4.31 版面變更**：本頁由分頁表格 CRUD 改為以專案為中心的操作介面（專案選擇器 + 摘要列 + AI 區塊 + 歷史會議紀錄）。**副作用是分頁、分類過濾、團隊過濾與關鍵字搜尋隨表格一併移除**，改以專案下拉的搜尋替代；專案數量成長到數百筆時這個版面要重新檢討。設計依據見 [會議記錄流程 Wireframe 設計規格](../superpowers/specs/2026-08-31-meeting-flow-wireframe-design.md)。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/projects`（`ProjectPage.razor`，掛 `MainLayout`） |
| REST API | `api/Project`、`api/v1/Project`（JWT Bearer） |
| 選單路徑 | 專案管理（`Menu.json` id=2）→ 專案項目（id=21） |
| 權限鍵 | 資源鍵「專案項目」（`MagicObjectHelper.角色_專案項目`），動作 `view`／`create`／`edit`／`delete`／`export`；管理員短路。**`export` 自 0.4.34 起才有實際用途（匯出 Markdown），既有角色皆未勾選，需管理員另行授予** |
| 主要使用者 | 具「專案項目」對應動作權限的登入者；管理員可見全部 |

> 頁面進入檢查用 `CheckAccessPage(角色_專案項目)`（葉節點鍵），工具列與操作鈕再以 `CheckAccessAction(角色_專案項目, 動作)` 個別控管。0.4.33 前頁面守門用的是群組鍵 `角色_專案管理`，與按鈕層不一致（全站唯一例外，`MeetingViewView` 一直是用葉節點鍵）；該群組已於 0.4.33 從選單移除，故一併改為葉節點鍵。

## 三、畫面與欄位

- 專案選擇列：可搜尋的專案下拉（`ProjectService.GetSelectableAsync`，依標題排序、不分頁）＋ 新增／編輯／刪除／重新整理四顆 Material Icon 按鈕。
- 專案摘要列：負責人、期程、狀態、完成度、本專案的會議紀錄份數。
- AI 區塊（需 `edit` 權限才顯示）：逐字稿下拉、提示詞下拉、「AI 轉會議紀錄」按鈕。逐字稿下拉列出**全部**轉錄完成的逐字稿，已被其他專案取用的呈現為不可選並標示「已屬：專案名」；屬於本專案的可重選以更換提示詞重新產生（會先跳確認對話框，因為會覆蓋）。
- 歷史會議紀錄清單：來源逐字稿、使用提示詞、生成狀態、產生時間、操作（檢視／編修草稿、**匯出 Markdown**、預覽逐字稿）。不分頁。
- **匯出 Markdown**（0.4.34）：把表頭（專案、會議、日期、使用提示詞、產生時間）加上會議紀錄內文組成 `.md` 檔，經 JS interop 直接推給瀏覽器下載，**檔案不落地、不新增 HTTP 檔案輸出面**。內容不含逐字稿。檔名為 `會議紀錄_{標題}_{產生日}.md`，會去除影音副檔名與檔名非法字元。
- Icon 一律使用 Material Icons Outlined，不使用 emoji。
- 編輯表單欄位：標題（必填）、開始日期、結束日期、狀態（必填，`StatusOptions`）、完成百分比（0-100）、負責人（必填）。**0.4.37 移除描述、優先級、分類、團隊四欄**；實體與 DTO 的欄位都保留未動，API 路徑仍可寫入這四個欄位。
  - ⚠️ **優先級雖已從表單移除，但仍是必填**且 `ProjectService.ValidateBusinessRulesAsync` 會驗證它必須是 `低/中/高` 之一，因此新增時由 `OnAddAsync` 帶入預設「中」。動這段程式碼時不能把預設值拿掉，否則所有新增／修改都會被擋下並回「專案優先順序不合法。」
- 附件：`專案附件` 一次可多選，單檔上限 1GB；待上傳清單可移除，已上傳檔案可下載（`/api/project-files/{id}/download`）或標記移除。

## 四、內部系統運作

- 資料流：`ProjectPage.razor` → `ProjectViewView`（`.razor.cs`）→ `ProjectService` → `BackendDBContext.Project`。REST API 走 `ProjectController` → `ProjectRepository`（與 UI 的 Service 為兩條路徑，皆回 `ApiResult`）。
- 讀取：清單 `GetAsync(DataRequest)` 使用 `AsNoTracking`；單筆 `GetAsync(int)` 以 `Include(x => x.Files)` 帶附件。
- 編輯前處理：開啟修改視窗時以 `ProjectService.GetAsync(id)` 重新取得資料副本（非重用清單物件），並清空待上傳／待移除清單。
- AI 產生會議紀錄：`MeetingService.RequestDraftAsync(meetingId, projectId, promptTemplateId)` 檢查團隊權限、轉錄狀態、歸屬衝突與是否正在生成，通過後寫入 `Meeting.ProjectId` 與提示詞快照並排入 `IMeetingDraftQueue`，實際生成由背景 worker 執行（見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)）。
- 刪除專案：`OnDelete(DeleteBehavior.SetNull)` —— 底下的會議紀錄不會被刪除，只解除歸屬；確認對話框會明白告知這件事。
- 寫入前清追蹤：`AddAsync`／`UpdateAsync`／`DeleteAsync` 進入時皆呼叫 `CleanTrackingHelper.Clean<Project>(context)`（`ProjectService.cs:201,233,286`）。
- 附件 Adapter：UI 以 `ProjectUploadFileInput`（FileName/ContentType/FileSize/Content）傳入；Service 依主表 `CreatedAt` 年／月建立目錄，檔名以 GUID 產生，落地後寫入 `ProjectFile`；刪除主表時先刪實體檔再刪紀錄。
- Migration：模型異動需在 `MeetingRecord.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- 動作級授權：`ProjectController` 各端點標註 `[HasPermission(角色_專案項目, 動作)]`（`ProjectController.cs:36,67,113,152,201`）；無權限回 403 且維持 `ApiResult` 結構；管理員短路。
- UI 與 API 共用同一 RBAC 權威（`IPermissionChecker`）。
- 團隊可見範圍：非管理員清單以 `TagStringHelper.BuildTeamAccessPredicate` 只看到公開（無團隊）或與自身團隊交集的專案；單筆／附件下載以 `IsTeamAccessible` 守門，越界回空模型或 `null`（`ProjectService.cs:75-79,185-190,360-365`）。**0.4.37 起表單已不能設定團隊，這些判斷對新資料形同虛設**，詳見下方專節。

### ⚠️ 0.4.37 的權限副作用（刻意為之，非 bug）

0.4.37 依使用者要求把描述、優先級、分類、**團隊**四欄整組從表單移除（理由：「不太需要用到團隊這個欄位」）。

`Project.Teams` 是專案唯一的列權限來源，共 6 處在用：`ProjectService` 4 處（清單、單筆、附件下載守門、`GetSelectableAsync`）、`MeetingService.RequestDraftAsync`、`TodoService.BeforeAddCheckAsync`。因此：

- **新建的專案一律公開**，任何有「專案項目」頁權限的人都看得到。
- **既有已設團隊的專案，只要被編輯一次就會被清成公開**——`ProjectService.UpdateAsync` 用表單值覆寫 `Teams`，而表單移除後傳入的是空 List，`TagStringHelper.ToStored([])` 回傳 `null`。這點與 0.4.35 會議紀錄那次**不同**（那次舊資料不受影響）。
- 連帶影響：選到專案就能看該專案下的歷史會議紀錄、預覽逐字稿、匯出 Markdown；`RequestDraftAsync` 與 `TodoService` 的專案守門對公開專案永遠通過。

服務層 6 處判斷與資料庫欄位**全部保留未動**。要恢復控管只需把表單的團隊 `Select` 加回 `ProjectViewView.razor`，服務層一行都不必改。

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
