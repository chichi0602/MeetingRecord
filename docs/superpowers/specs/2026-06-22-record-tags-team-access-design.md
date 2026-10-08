# 設計規格：紀錄分類/團隊標籤與團隊權控（階段二）

- 文件版本：1.0
- 文件狀態：已封存（樣板時期設計規格，部分內容已被後續版本取代）
- 現行系統版本：0.4.23
- 首次實作版本：0.4.1
- 最後核對日期：2026/07/14

> 📌 本文為樣板時期（0.3.0～0.4.0）的設計規格快照，內容維持當時原貌，**請勿據此開發**。
> ⚠️ 本檔頭的「現行系統版本／最後核對日期」**刻意停在快照當時**，不隨系統版本推進。

> 📌 現況差異（2026/10/08 核對 0.4.118）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - 選單權限的「位置索引三處同步」已改為宣告式 `Menu.json` 唯一 `id` → `SidebarMenuService.MenuPermissionMap` 權限鍵（`ApplyPermissionStructure` 仍在，但改以 id 對應，重排選單不錯位）
> - `MyTask`／樣板 `Meeting` 模組已於 0.4.24 移除；現行的會議紀錄（0.4.27 起）是本系統另行開發的功能
> - 「雙資料庫 migration」已收斂為 SQLite 單軌（0.4.24）
> - 團隊權控已整個改寫：角色＝能做什麼、團隊＝誰的資料（專案主責＋協作團隊、`UserTeam`）、分類＝什麼資料（0.4.101～0.4.103）；紀錄上的「團隊」換行分隔標籤欄位、`RoleView.DefaultTeamsJson`、「無團隊即公開」皆已移除（分類仍以 `TagStringHelper` 換行分隔儲存，但只是標籤、不影響可見性） —— 見 [紀錄分類與團隊權控 PRD](../../prd/紀錄分類與團隊權控-prd.md)

> 以 superpowers brainstorming 流程產出。對應實作見 changelog [`2026-06-22-紀錄分類團隊與權控.md`](../../changelog/2026-06-22-紀錄分類團隊與權控.md)。

## 背景與目標

承接階段一（Category/Team 主資料）。本階段把分類/團隊掛到 Project/MyTask/Meeting 成為多值標籤，並導入以角色為基礎的團隊行級權控，做法移植自母專案 KnowledgeExtraction.AI。

## 範圍決策（與使用者確認）

- 權控執行面 **同母專案**：Blazor 服務層（清單過濾 + 單筆 `GetAsync(id)` 守門 + 檔案下載守門）；雙模式 `IRecordAccessScopeProvider`；Web API repository 路徑不做行級過濾。
- Migration 延續階段一：僅 SQLite。

## 架構

- **儲存**：`Categories`/`Teams` 以換行分隔字串（`TagStringHelper`，`"\n值\n"` 精確比對）；`RoleView.DefaultTeamsJson` 以 JSON 陣列（`TeamJsonHelper`）。
- **權控**：`IRecordAccessScopeProvider.GetAsync()` 回 `RecordAccessScope(IsAdmin, Teams)`；非管理員清單套 `BuildTeamAccessPredicate`，單筆/下載用 `IsTeamAccessible`。使用者團隊來自 `CurrentUser.TeamList`（登入時由角色 `DefaultTeams` 載入）。
- **轉換**：AutoMapper `ForMember` 處理 List↔分隔字串、`DefaultTeams`↔`DefaultTeamsJson`。
- **UI**：三紀錄頁工具列分類/團隊過濾、表格標籤欄、編輯多選；角色頁預設團隊多選。

## 可見性規則

| 使用者 | 可見紀錄 |
|--------|----------|
| 管理員 | 全部 |
| 非管理員（角色有團隊） | 無團隊（公開）或 Teams 與角色團隊有交集 |
| 非管理員（角色無團隊） | 僅無團隊（公開） |

## 測試

- `TagStringHelperTests`：往返、去重去空白、精確成員比對述詞、`IsTeamAccessible` 四情境。
- `ProjectServiceTeamAccessTests`：管理員全見、非管理員交集/公開、無團隊僅公開、團隊過濾、單筆守門。

## 驗證結果

- `dotnet build -c Release`：0 錯誤。
- `dotnet test`：79 筆通過（既有 65 + 新增 14）。
- SQLite migration `AddRecordTagsAndRoleTeams` 為 7 欄 delta。

## 後續

- 階段三：changelog 通用型改善移植。
