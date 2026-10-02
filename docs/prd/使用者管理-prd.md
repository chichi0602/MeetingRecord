# 使用者管理 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.113
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/01

> **0.4.102：「資料群組」改回「團隊」。** 團隊就是部門（例如業務部、管理部、研發部），一個人可以屬於多個；使用者看得到專案的條件是「他的任一團隊是該專案的主責或協作團隊」，**不再有公開專案**。欄位、清單欄與批次按鈕都改稱「團隊」，行為不變。
>
> **0.4.101：角色與團隊分開設定。** 角色＝可以做什麼（功能與動作權限），團隊＝誰的資料（當時畫面叫「資料群組」），每個帳號分別關聯兩者。表單恢復「角色」與「額外角色」選單（權限取聯集），另有「團隊」多選（存 `UserTeam`）；「管理者」勾選框凌駕兩者。清單多「角色」「團隊」兩欄，並可勾選多人批次加入／移出某個團隊。0.4.98～0.4.100 的「身分只有管理者／一般使用者」「所屬團隊（專案）＝協作者」都已撤回；啟動時也不再把所有帳號收斂成一般使用者（`RoleConsolidationService` 改為只建預設角色的 `DefaultRoleSeeder`）。

## 一、目標與範圍

提供管理員維護系統使用者帳號的完整能力：查詢、新增、修改、刪除，並在同一表單設定角色（能做什麼）、團隊（誰的資料，決定看得到哪些專案）與是否為管理者。所有指派透過 RBAC 雙寫落地至關聯表，作為 UI 與 API 共用的權限來源。

- **範圍**：`/myusers` 使用者維護頁與 `MyUserService` CRUD、`GetUserAssignmentsAsync` 回填、`SyncAssignmentsAsync` 雙寫、團隊批次加入／移出、稽核寫入。
- **非範圍**：登入、鎖定、改密碼與 Google 建帳見 [登入與帳號流程](登入與帳號流程-prd.md)；角色本身與權限矩陣編輯見 [角色管理](角色管理-prd.md)；團隊本身的維護（含從團隊端加減成員）見 [團隊清單](團隊清單-prd.md)（`/teams`）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/myusers` | 系統管理 → 使用者管理（`Menu.json` id=31）| 僅管理員（`AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

- 頁面 `MyUserView` 初始化先執行 `Check`，未通過即導向登出；非管理員顯示「你沒有權限存取此頁面」並停止載入。
- 本頁為 Blazor 元件、無對應的 `MyUser` API 控制器；動作級 `[HasPermission("resource:action")]` 套用於業務資料 API（分類／團隊／專案），使用者維護僅以管理員身分閘控。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 勾選框、帳號、名稱、Email、角色（列出全部角色名稱；管理者顯示「管理者（…）」）、團隊、狀態、建立時間、更新時間。「狀態」是啟用／停用膠囊，有使用者管理的修改權限時可以直接點擊切換（0.4.105，二次確認；停用後該帳號無法登入；`MyUserService.SetStatusAsync`），不必進編輯視窗；工具列含新增、重新整理、搜尋、清空搜尋。搜尋比對帳號／名稱／Email／角色名稱。
- **批次調整團隊**（0.4.101）：在表格勾選多人 → 選一個團隊 → 按「加入團隊」或「移出團隊」（先跳確認），由 `MyUserService.AddUsersToTeamAsync`／`RemoveUsersFromTeamAsync` 只增刪勾選者的 `UserTeam` 列。調整一批人的可見範圍靠這裡，不是靠改角色。
- **維護表單**（Modal）欄位：
  - 帳號（必填，唯一）、密碼（新增必填；編輯留白＝沿用既有密碼）、名稱（必填）、Email。
  - 角色（必填，主要角色 `RoleViewId`）、額外角色（多選，權限與主要角色取聯集）。
  - 團隊（多選，決定看得到哪些專案：專案的主責或協作團隊有他所屬的團隊；不選就看不到任何專案，只看得到自己上傳、尚未歸屬的會議）。
  - 啟用（`Status`）、「管理者（不看角色與團隊，擁有全部功能、看得到所有資料）」（`IsAdmin`）核取方塊。
- 編輯前以 `Clone()` 複製當前列並載入額外角色與團隊回填。

## 四、內部系統運作

View（`MyUserView`）→ `MyUserService` → `BackendDBContext`：

- **新增**（`AddAsync`）：`CleanTrackingHelper.Clean` 清追蹤；產生 `Salt`、以 `SecurePasswordHasher.HashPassword` 雜湊密碼，並設 `MustChangePassword = true`（0.4.113，帳號密碼是管理者提供的，第一次登入要改成自己的；`support` 不設）；存檔後 `SyncAssignmentsAsync` 雙寫角色與團隊；寫 `User.Create` 稽核。
- **修改**（`UpdateAsync`）：清追蹤、以 `Entry(...).State = Modified` 更新；先帶回原本的 `MustChangePassword`（畫面模型沒有這欄，整筆蓋回會被清掉）；密碼留白時沿用既有 `Password`／`Salt`，否則重新雜湊，且**替別人**重設時設 `MustChangePassword = true`（改自己的、`support` 不設）；再 `SyncAssignmentsAsync`；寫 `User.Update` 稽核。
- **刪除**（`DeleteAsync`）：`Entry(...).State = Deleted`；寫 `User.Delete` 稽核（含帳號）。
- **RBAC 雙寫**（`SyncAssignmentsAsync` → `RbacWriteService`）：`SyncUserRolesAsync` 以 `UserRole` 反映主要＋額外角色（去重）；團隊名稱先解析為 `Team.Id`，`SyncUserTeamsAsync` 以 `UserTeam` 差異化增刪。
- **回填**（`GetUserAssignmentsAsync`）：由 `UserRole` 扣除主要角色得額外角色、由 `UserTeam` join `Team` 得團隊名稱。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時將既有 `MyUser.RoleViewId` 補寫成 `UserRole`，冪等執行，確保舊資料進入 RBAC 表。0.4.101 起不再從角色預設團隊補 `UserTeam`（該欄位已刪除）。
- **有效團隊**：登入後由 `EffectiveTeamResolver` 決定，0.4.101 起只取使用者直接所屬的團隊（`UserTeam`）。資料可見範圍的實際判斷在 `ProjectAccessService`。
- **批次加入／移出**（`AddUsersToTeamAsync`／`RemoveUsersFromTeamAsync`）：只動勾選者的 `UserTeam`，已在（或本來就不在）該團隊的人略過，回傳實際異動筆數。
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
- 團隊名稱查無對應 `Team`：該名稱不會產生 `UserTeam`（僅同步存在的團隊）；批次操作選到已不存在的團隊會提示「找不到團隊「X」。」。
- 未設額外角色：僅保留主要角色。未設團隊：看不到任何專案（0.4.102 起沒有公開專案），只看得到自己上傳、尚未歸屬的會議。

## 七、驗收與測試

- `MeetingRecord.Tests/MyUserServiceAssignmentTests.cs`：`AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam`（多角色＋團隊落地 `UserRole`／`UserTeam`）。
- `MeetingRecord.Tests/RbacWriteServiceTests.cs`：`SyncUserRolesAsync`／`SyncUserTeamsAsync` 差異化增刪對帳。
- `MeetingRecord.Tests/RbacBackfillServiceTests.cs`：由 `RoleViewId` 建 `UserRole`、0.4.101 的權限鍵「資料群組」改回「團隊清單」且不掉權限、冪等。
- `MeetingRecord.Tests/ProjectAccessTests.cs`：`BatchAddAndRemove_ShouldOnlyTouchSelectedUsers`（批次加入／移出只動勾選者）。
- `MeetingRecord.Tests/DefaultRoleSeederTests.cs`：`RunAsync_ShouldNotTouchUserRoles`（啟動不再改帳號的角色）。
- `MeetingRecord.Tests/AuditEventsTests.cs`：`User.Create`／`User.Update`／`User.Delete`（含帳號）與未登入 actor 為 null。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`：多角色聯集有效權限鍵、管理員短路。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Admins/MyUserPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/MyUserView.razor:44`、`MyUserView.razor.cs:193`（編輯回填）、`:405`（多角色／團隊變更）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/MyUserService.cs:210`（Add）、`:250`（Update）、`:305`（雙寫）、`:332`（回填）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacWriteService.cs:44`（`SyncUserRolesAsync`）、`:64`（`SyncUserTeamsAsync`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacBackfillService.cs`（啟動回填）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/EffectiveTeamResolver.cs`（有效團隊）
- RBAC 資料表：`MyUser`、`RoleView`、`UserRole`、`UserTeam`、`RolePermissionMap`、`Permission`（`src/MeetingRecord/MeetingRecord.AccessDatas/Models/`）
- 交叉連結：[登入與帳號流程](登入與帳號流程-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
