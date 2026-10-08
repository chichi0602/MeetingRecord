# 設計規格：分類清單 / 團隊清單管理頁面（階段一）

- 文件版本：1.0
- 文件狀態：已封存（樣板時期設計規格，部分內容已被後續版本取代）
- 現行系統版本：0.4.23
- 首次實作版本：0.3.0
- 最後核對日期：2026/07/14

> 📌 本文為樣板時期（0.3.0～0.4.0）的設計規格快照，內容維持當時原貌，**請勿據此開發**。
> ⚠️ 本檔頭的「現行系統版本／最後核對日期」**刻意停在快照當時**，不隨系統版本推進。

> 📌 現況差異（2026/10/08 核對 0.4.118）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - 選單權限的「位置索引三處同步」已改為宣告式 `Menu.json` 唯一 `id` → `SidebarMenuService.MenuPermissionMap` 權限鍵（`ApplyPermissionStructure` 仍在，但改以 id 對應，重排選單不錯位）
> - `MyTask`／樣板 `Meeting` 模組已於 0.4.24 移除；現行的會議紀錄（0.4.27 起）是本系統另行開發的功能
> - 「雙資料庫 migration」已收斂為 SQLite 單軌（0.4.24）
> - 團隊權控已整個改寫：角色＝能做什麼、團隊＝誰的資料（專案主責＋協作團隊、`UserTeam`）、分類＝什麼資料（0.4.101～0.4.103）；紀錄上的「團隊」換行分隔標籤欄位、`RoleView.DefaultTeamsJson`、「無團隊即公開」皆已移除（分類仍以 `TagStringHelper` 換行分隔儲存，但只是標籤、不影響可見性） —— 見 [紀錄分類與團隊權控 PRD](../../prd/紀錄分類與團隊權控-prd.md)

> 以 superpowers brainstorming 流程產出。對應實作見 changelog [`2026-06-22-分類與團隊清單.md`](../../changelog/2026-06-22-分類與團隊清單.md)。

## 背景與目標

NET10-Blazor-Starter 將以母專案 KnowledgeExtraction.AI 為藍本，分階段成長為知識庫萃取系統。本規格涵蓋**階段一**：新增兩個獨立主資料管理頁面（Category 分類、Team 團隊），含 Web API、權限與雙資料庫 migration 政策，作為階段二「紀錄分類/團隊標籤 + 團隊權控」的前置基礎。

## 範圍決策（與使用者確認）

- 交付方式：**分階段**（本規格僅階段一）。
- 四頁面整合深度（階段二）：**完整團隊權控**（同母專案）。
- Category/Team **提供 Web API Controller**（與既有 CRUD API 一致）。
- changelog 通用型改善：**另開階段**（階段三）。
- SqlServer migration：腳手架 SqlServerMigrations 專案從未 bootstrap，本階段**僅產生 SQLite migration**。

## 架構

兩個獨立主資料 CRUD，完全沿用腳手架既有分層慣例：

```
Blazor Page (@page) → XxxViewView (.razor/.cs/.css)
   → Service (DataRequest/DataRequestResult/VerifyRecordResult, BeforeXxxCheck)
Web API Controller (ApiResult, JWT, /api + /api/v1)
   → Repository (PagedResult, SearchRequestDto)
共用：Entity ↔ AdapterModel ↔ Dto/CreateUpdateDto（AutoMapper 雙向）
```

### Entity 欄位
- Category：Id、Name（必填、唯一）、Description?、IsEnabled、CreatedAt、UpdatedAt。
- Team：Id、Name（必填、唯一）、Code?（選填、有填則唯一）、Description?、IsEnabled、CreatedAt、UpdatedAt。

### 驗證
- Service 層 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 做名稱唯一性（trim、不分大小寫；更新排除自身）；Team 另驗 Code 唯一。
- API 層 Controller 以 `ExistsByNameAsync`／`ExistsByCodeAsync` 回 409 Conflict。

### 權限（位置索引對應，三處同步）
- `MagicObjectHelper`：`角色_資料定義`、`角色_分類清單`、`角色_團隊清單`。
- `RolePermissionService.GetRoleListPermissionAllName()`：在「登出」群組之前插入 `[資料定義, 分類清單, 團隊清單]`。
- `Menu.json`：對應位置（系統管理後、登出前）新增「資料定義」群組。
- 不變量：`SidebarMenuService.ApplyPermissionStructure` 以 index 對應 menu 與權限群組，故兩者順序必須一致。預設角色啟動 seed 自動同步；既有自訂角色需重新勾選。

## 測試

- `CategoryServiceTests`、`TeamServiceTests`（in-memory SQLite fixture）：名稱/代號唯一性、更新排除自身、新增持久化。

## 驗證結果

- `dotnet build -c Release`：0 錯誤。
- `dotnet test`：65 筆通過（既有 52 + 新增 13）。
- SQLite migration `AddCategoryAndTeam` 為僅含 Category/Team 的 delta。

## 後續階段（備忘）

- 階段二：Project/Task/Meeting 加 `Categories`/`Teams`、`RoleView.DefaultTeamsJson`、`TagStringHelper`/`TeamJsonHelper`、`IRecordAccessScopeProvider` 行級權控、四頁面多選與篩選、角色頁指派團隊。
- 階段三：changelog 通用型改善移植。
