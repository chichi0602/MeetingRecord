# 選單/CheckAccessPage 改用 IPermissionChecker（0.4.22）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：0.4.22
- 最後核對日期：2026/07/14

## 目的

階段四權限重構收尾：消除 UI 與 API 的**雙權威來源**。改動前 UI 端（選單、`CheckAccessPage`、`CheckAccessAction`）讀 `CurrentUser.RoleList`，而該清單來自**主要角色的 `TabViewJson`**；API 端（`HasPermissionAttribute`）則讀 `IPermissionChecker`（RBAC 表 `RolePermissionMap`＋`Permission`，且已 honor 多角色）。兩者靠角色存檔時雙寫維持一致，是技術債，且造成 **UI 忽略使用者額外角色（`UserRole`）**——多角色使用者的 UI 與 API 不一致。

本次把 UI 的權限來源收斂到 `IPermissionChecker`，UI/API 共用單一 RBAC 權威來源，並自然修好多角色 UI 聯集。

## 變更範圍

- **`AuthenticationStateHelper`**（`MeetingRecord.Business/Services/Other`）
  - 建構子注入 `IPermissionChecker`。
  - `Check`：登入態初始化時以 `GetEffectivePermissionKeysAsync(userId)` 一次載入 RBAC 有效權限鍵（多角色聯集），覆寫 `CopyFrom` 由 `TabViewJson` 得到的 `CurrentUser.RoleList`。UI 判定維持同步比對記憶體集合（不逐項查 DB）。
  - `CheckAccessPage`：補管理員短路（`GetEffectivePermissionKeysAsync` 不含 admin 隱含全通過，回空集合），比照 `CheckAccessAction`，否則管理員選單/頁面會全被隱藏。
- **測試** `MeetingRecord.Tests/AuthenticationStateHelperTests.cs`
  - fixture `AddUserAsync` 補雙寫 `RolePermissionMap`（`RbacWriteService.SyncRolePermissionsAsync`），使 RBAC 來源的 `RoleList` 含權限鍵。
  - `CreateHelper` 多傳 `PermissionChecker`。
  - 新增多角色測試 `Check_WithMultipleRoles_ShouldInitializeRoleListAsUnion`（斷言 `RoleList` 為主要∪額外）。

## 設計重點

- **不逐項 async 查 DB**：Razor `@if` 會大量呼叫 `CheckAccessPage`/`CheckAccessAction`，故在 `Check` 一次載入有效權限鍵到記憶體集合，UI 判定維持同步。
- **零破壞**：RBAC 表與 `TabViewJson` 是同一份 `GetPermissionInput` 輸出的雙寫，**單角色行為完全不變**；唯一新增行為是多角色聯集（修好落差）與管理員明確短路（強健化）。
- `SidebarMenuService` 不需改：`FilterAuthorizedMenuItems` 仍呼叫 `CheckAccessPage`，來源改變後自動生效。
- **無模型/schema 變更 → 不需 migration**。

## 驗證

- 單元測試：129 個全綠（含新增多角色測試）。
- 建置：`dotnet build` 綠燈。
- 實跑（多角色聯集 before/after，SQLite/Development，瀏覽器實際點擊）：
  - **auditor**（主要=稽核員 view-only + 額外=預設角色 full）：專案頁**新增/修改/刪除鈕全顯示**（＝聯集），改動前僅反映主要角色 view-only。
  - **rouser**（單一稽核員）：無任何動作鈕（單角色不變）。
  - **support**（管理員）：全部按鈕顯示（admin 短路生效）。

## 注意事項

- `TabViewJson` 退居相容欄位，僅供角色編輯畫面回填；權威判定以 RBAC 表為準。
- `appsettings.json` `SystemVersion` 0.4.21 → 0.4.22。
