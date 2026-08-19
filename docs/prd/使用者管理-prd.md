# 使用者管理 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.24
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/08/17

## 一、目標與範圍

提供管理員維護系統使用者帳號的完整能力：查詢、新增、修改、刪除，並在同一表單指派主要角色、額外角色（多角色，權限取聯集）與直接綁定的團隊。所有指派透過 RBAC 雙寫落地至關聯表，作為 UI 與 API 共用的權限來源。

- **範圍**：`/myusers` 使用者維護頁與 `MyUserService` CRUD、`GetUserAssignmentsAsync` 回填、`SyncAssignmentsAsync` 雙寫、稽核寫入。
- **非範圍**：登入、鎖定、改密碼與 Google 建帳見 [登入與帳號流程](登入與帳號流程-prd.md)；角色本身與權限矩陣編輯見 [角色管理](角色管理-prd.md)；團隊清單維護見 `/teams`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/myusers` | 系統管理 → 使用者管理（`Menu.json` id=31）| 僅管理員（`AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

- 頁面 `MyUserView` 初始化先執行 `Check`，未通過即導向登出；非管理員顯示「你沒有權限存取此頁面」並停止載入。
- 本頁為 Blazor 元件、無對應的 `MyUser` API 控制器；動作級 `[HasPermission("resource:action")]` 套用於業務資料 API（分類／團隊／專案），使用者維護僅以管理員身分閘控。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 帳號、名稱、Email、角色（`RoleViewName`）、狀態、管理員、建立時間、更新時間，皆可排序；工具列含新增、重新整理、搜尋、清空搜尋。搜尋比對帳號／名稱／Email／角色名稱。
- **維護表單**（Modal）欄位：
  - 帳號（必填，唯一）、密碼（新增必填；編輯留白＝沿用既有密碼）、名稱（必填）、Email。
  - 角色（必填，單選主要角色 `RoleViewId`）。
  - 額外角色（多選，`AdditionalRoleIds`，與主要角色權限取聯集）。
  - 團隊（多選團隊名稱，直接綁定此使用者；不設定則沿用其角色的預設團隊）。
  - 啟用（`Status`）、管理員（`IsAdmin`）核取方塊。
- 編輯前以 `Clone()` 複製當前列並載入既有指派回填；新增時預設帶入「預設角色」。

## 四、內部系統運作

View（`MyUserView`）→ `MyUserService` → `BackendDBContext`：

- **新增**（`AddAsync`）：`CleanTrackingHelper.Clean` 清追蹤；產生 `Salt`、以 `SecurePasswordHasher.HashPassword` 雜湊密碼；存檔後 `SyncAssignmentsAsync` 雙寫角色與團隊；寫 `User.Create` 稽核。
- **修改**（`UpdateAsync`）：清追蹤、以 `Entry(...).State = Modified` 更新；密碼留白時沿用既有 `Password`／`Salt`，否則重新雜湊；再 `SyncAssignmentsAsync`；寫 `User.Update` 稽核。
- **刪除**（`DeleteAsync`）：`Entry(...).State = Deleted`；寫 `User.Delete` 稽核（含帳號）。
- **RBAC 雙寫**（`SyncAssignmentsAsync` → `RbacWriteService`）：`SyncUserRolesAsync` 以 `UserRole` 反映主要＋額外角色（去重）；團隊名稱先解析為 `Team.Id`，`SyncUserTeamsAsync` 以 `UserTeam` 差異化增刪。
- **回填**（`GetUserAssignmentsAsync`）：由 `UserRole` 扣除主要角色得額外角色、由 `UserTeam` join `Team` 得團隊名稱。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時將既有 `MyUser.RoleViewId` 補寫成 `UserRole`、角色預設團隊補寫成 `UserTeam`，冪等執行，確保舊資料進入 RBAC 表。
- **有效團隊**：登入後由 `EffectiveTeamResolver` 決定（使用者直綁團隊優先，否則沿用角色預設團隊）。
- **前置檢查**：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查帳號唯一性。
- **稽核 actor**：`ResolveActor` 取目前登入者；未登入時 actor 為 null。

## 五、權限與安全

- 本頁僅管理員可進入（`CheckIsAdmin`）；權限判定以 RBAC 表為單一權威，管理員短路一律通過。
- API 端業務資料以 `HasPermissionAttribute` 判權，無權限回 `ApiResult` 403（管理員短路），與本頁指派結果一致。
- 清單／單筆輸出經 `OtherDependencyData` 將密碼欄位清空；不輸出 `Salt`、雜湊、Token。
- 密碼一律 PBKDF2 雜湊儲存；細節見 [登入與帳號流程](登入與帳號流程-prd.md) 與安全文件。

## 六、錯誤與邊界

- 新增未輸入密碼：前端與 `AddAsync` 皆拒絕（「新增使用者時必須輸入密碼。」）。
- 帳號重複：新增／修改前置檢查回「帳號已存在，無法新增／修改。」
- 修改對象不存在：回「找不到要修改的使用者資料。」；刪除同理。
- 團隊名稱查無對應 `Team`：該名稱不會產生 `UserTeam`（僅同步存在的團隊）。
- 未設額外角色／團隊：僅保留主要角色與角色預設團隊。

## 七、驗收與測試

- `MeetingRecord.Tests/MyUserServiceAssignmentTests.cs`：`AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam`（多角色＋團隊落地 `UserRole`／`UserTeam`）。
- `MeetingRecord.Tests/RbacWriteServiceTests.cs`：`SyncUserRolesAsync`／`SyncUserTeamsAsync` 差異化增刪對帳。
- `MeetingRecord.Tests/RbacBackfillServiceTests.cs`：由 `RoleViewId` 建 `UserRole`、由角色預設團隊建 `UserTeam`、冪等。
- `MeetingRecord.Tests/AuditEventsTests.cs`：`User.Create`／`User.Update`／`User.Delete`（含帳號）與未登入 actor 為 null。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`：多角色聯集有效權限鍵、管理員短路。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Admins/MyUserPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/MyUserView.razor:44`、`MyUserView.razor.cs:193`（編輯回填）、`:405`（多角色／團隊變更）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/MyUserService.cs:210`（Add）、`:250`（Update）、`:305`（雙寫）、`:332`（回填）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacWriteService.cs:44`（`SyncUserRolesAsync`）、`:64`（`SyncUserTeamsAsync`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacBackfillService.cs:95`、`:123`（啟動回填）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/EffectiveTeamResolver.cs`（有效團隊）
- RBAC 資料表：`MyUser`、`RoleView`、`UserRole`、`UserTeam`、`RolePermissionMap`、`Permission`（`src/MeetingRecord/MeetingRecord.AccessDatas/Models/`）
- 交叉連結：[登入與帳號流程](登入與帳號流程-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
