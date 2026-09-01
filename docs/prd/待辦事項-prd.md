# 待辦事項 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.32
- 首次實作版本：0.4.32
- 最後核對日期：2026/09/01

## 一、目標與範圍

提供「待辦事項（Todo）」的建立、查詢、修改、刪除與完成狀態管理。待辦一定隸屬於一個專案項目，並可回溯到產生它的來源會議紀錄。

- 範圍：清單查詢（關鍵字搜尋、專案／狀態／分類／團隊過濾、排序、分頁）、單筆維護（含表單驗證）、清單上直接勾選完成、動作級授權與團隊可見範圍控管。
- 非範圍：看板版面與拖拉排序、子任務與相依關係、工時統計、提醒通知。
- **本版不含「AI 從會議紀錄抽出待辦」**。`MeetingId` 欄位與外鍵已備妥，待該功能實作時才會有值；目前所有待辦都是手動新增，來源顯示為「— 手動新增」。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/todos`（`TodoPage.razor`，掛 `MainLayout`） |
| REST API | `api/Todo`、`api/v1/Todo`（JWT Bearer） |
| 選單路徑 | 專案管理（`Menu.json` id=2）→ 待辦事項（id=22，icon `checklist`） |
| 權限鍵 | 資源鍵「待辦事項」（`MagicObjectHelper.角色_待辦事項`），動作 `view`／`create`／`edit`／`delete`；管理員短路 |

## 三、畫面與欄位

- 工具列：左側新增、重新整理；右側專案過濾、狀態過濾、分類過濾、團隊過濾、關鍵字輸入、清空、搜尋。Icon 一律 Material Icons Outlined。
- 清單欄位：完成勾選、待辦事項（含描述副標）、所屬專案、來源會議紀錄、負責人、截止日、優先度、狀態、分類、團隊、操作。
- 逾期的截止日以紅色粗體顯示並附「逾期 N 天」；已完成的標題加刪除線。
- 沒有任何可存取的專案時，清單上方顯示提示並停用「新增」——待辦必須隸屬專案。
- 預設排序：有截止日的優先、越早到期越前面，未指定截止日者墊底。
- 編輯表單欄位：待辦標題（必填）、描述、所屬專案（必填）、來源會議紀錄（唯讀，僅在有值時顯示）、負責人、截止日、優先度（必填）、狀態（必填）、分類、團隊。

## 四、設計決策

| 議題 | 結論 |
|------|------|
| 完成狀態 | **只有 `Status` 一個欄位**（待辦／進行中／已完成）。清單上的勾選框是「把 Status 設為已完成」的快捷鍵，**不另存 `IsCompleted` 布林**——兩個欄位並存必然出現「勾了完成但狀態還是進行中」這種互相矛盾的資料 |
| 取消完成後的狀態 | 退回「進行中」而非「待辦」。已經動過的事情退回未開始並不合理 |
| 專案關聯 | `ProjectId` **必填**，`OnDelete(Cascade)`。沒有專案的待辦只會變成沒人管的垃圾資料 |
| 會議紀錄關聯 | `MeetingId` **可空**，`OnDelete(SetNull)`。會議紀錄被刪除時待辦保留，只是失去來源 |
| 團隊權控 | Todo **自己帶 `Categories`／`Teams`**，與其他所有模組一致。缺點是專案改了團隊，底下的待辦不會跟著改 |
| 專案完成度 | **不連動**。`Project.CompletionPercentage` 維持手動填寫——待辦數量與專案進度未必成正比，自動換算反而變成假資訊 |
| 來源會議紀錄的保護 | `MeetingId` 不開放從畫面或 API 覆寫，`TodoService.UpdateAsync` 與 `TodoRepository.UpdateAsync` 都一律沿用資料庫既有值 |

## 五、內部系統運作

- 資料流：`TodoPage.razor` → `TodoViewView` → `TodoService` → `BackendDBContext.Todo`。REST API 走 `TodoController` → `TodoRepository`（兩條路徑並存，皆回 `ApiResult`）。
- 讀取：清單與單筆都 `Include(Project)` 與 `Include(Meeting)` 以取得關聯名稱；`ProjectTitle`／`MeetingTitle` 由 AutoMapper 的 `ForMember` 自導覽屬性帶出。
- 團隊權控只在 Blazor 的 `TodoService` 生效（清單 `BuildTeamAccessPredicate`、單筆 `IsTeamAccessible`），API repository 路徑不做列級過濾——與既有模組的設計界線一致。
- 前置檢查：新增與修改都會驗證所屬專案存在且在使用者的團隊可見範圍內。
- 寫入前後皆 `CleanTrackingHelper.Clean<Todo>(context)`。
- API 新增後會重新載入實體才回傳，否則回應的 `ProjectTitle`／`MeetingTitle` 會是空的。

## 六、相關程式與文件

- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/Todo.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/TodoService.cs:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Todos/TodoViewView.razor:1`
- 交叉連結：[專案項目 PRD](專案項目-prd.md)、[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)、[../architecture/開發慣例與限制速查.md](../architecture/開發慣例與限制速查.md)
