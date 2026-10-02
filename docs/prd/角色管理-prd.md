# 角色管理 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.110
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/01

> **0.4.102：權限矩陣的「資料群組」改回「團隊清單」。** 0.4.101 的「資料群組」權限鍵（含 `資料群組:edit` 這類動作鍵）由 `RbacBackfillService.RenameLegacyPermissionKeysAsync` 啟動時就地改回「團隊清單」，原本有的角色照樣有。角色本身的行為與 0.4.101 相同。三者分工：**角色＝能做什麼，團隊＝誰的資料，分類＝什麼資料**。
>
> **0.4.101：角色＝可以做什麼，團隊（當時畫面叫「資料群組」）＝可以看到什麼。**
> - 角色只管功能與動作權限，**不帶任何資料範圍**：「預設團隊」欄位（`RoleView.DefaultTeamsJson`）已連同資料庫欄位刪除（migration `AddProjectTeamRemoveDefaultTeams`）。看得到哪些專案改由使用者所屬的團隊決定，見 [團隊清單](團隊清單-prd.md)。
> - **恢復多角色**：使用者管理頁又可指派主要角色與額外角色（權限取聯集），在本頁新建的角色有地方用了。管理者（`MyUser.IsAdmin`）仍凌駕一切。
> - 0.4.98～0.4.100 的「只有管理者／一般使用者兩種身分、每次啟動把所有帳號收斂成一般使用者」已撤回。`RoleConsolidationService` 改名 `DefaultRoleSeeder`：只確保「一般使用者」角色存在（舊名「預設角色」就地改名），**只在新建或改名的當下**把權限設成 `RolePermissionService.GetGeneralUserPermissionNames()`，之後以本頁設定為準；**不動任何帳號的角色**。
> - 權限矩陣「資料定義管理功能」底下多一項「資料群組」（原「團隊清單」，不在一般使用者的初始權限裡；0.4.102 已改回「團隊清單」）。舊權限鍵「團隊清單」與其動作鍵由 `RbacBackfillService` 啟動時就地改名，原本有的角色照樣有。

## 一、目標與範圍

提供管理員維護角色（`RoleView`）與其**動作粒度權限矩陣**的能力（0.4.101 起角色不再設定預設團隊）。角色權限以權限鍵集合表示，透過 RBAC 雙寫落地為 `RolePermissionMap`，成為 UI 與 API 動作級授權（`[HasPermission]`／`IPermissionChecker`）的單一權威來源。

- **範圍**：`/roleviews` 角色維護頁、`RoleViewService` CRUD、`RolePermissionService` 權限矩陣序列化、`RbacWriteService.SyncRolePermissionsAsync` 雙寫、稽核寫入。
- **非範圍**：使用者與角色的指派見 [使用者管理](使用者管理-prd.md)；登入與帳號安全見 [登入與帳號流程](登入與帳號流程-prd.md)；資料可見範圍見 [團隊清單](團隊清單-prd.md)、[專案項目](專案項目-prd.md)與 [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/roleviews` | 系統管理 → 角色管理（`Menu.json` id=32）| 僅管理員（`AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

- `RoleViewView` 初始化先 `Check`，非管理員顯示「你沒有權限存取此頁面」並停止載入。
- 角色本身以 Blazor 頁面、管理員身分閘控（無 `RoleView` API 控制器）；此處編輯出來的權限鍵，才是各業務 API 控制器 `[HasPermission("頁面", "動作")]` 的授權依據。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 名稱、建立時間、更新時間，可排序；工具列含新增、**建立預設角色**（0.4.108）、重新整理、搜尋、清空搜尋（搜尋比對名稱）。
- **建立預設角色**（0.4.108）：上線時系統是空的，按一下（確認後）建出下列四個角色並設好權限；已有同名角色的略過、不覆蓋，可重複按。定義在 `RolePresets`（`MeetingRecord.Business/Helpers/RolePresets.cs`），服務為 `RoleViewService.AddPresetsAsync`，每建一個記一筆 `Role.Create` 稽核。
  | 角色 | 權限 |
  |---|---|
  | 管理者 | 全部頁面、全部動作（`GetRolePermissionAllName()`）。注意：這是一般角色；真正「不受任何限制」的是使用者管理的「管理者」勾選（`IsAdmin`）。 |
  | 一般使用者 | 使用說明、登出；專案項目、待辦事項、會議紀錄可檢視／新增／修改／匯出（不能刪除） |
  | 檢視者 | 使用說明、登出；專案項目、待辦事項、會議紀錄只能檢視 |
  | 主管 | 使用說明、登出；專案項目、待辦事項、會議紀錄全部動作；分類清單、團隊清單全部動作 |
- 「一般使用者」也是新帳號的預設角色；空系統第一次啟動時 `DefaultRoleSeeder` 就會建好，初始權限同樣取自 `RolePresets`（`RolePermissionService.GetGeneralUserPermissionNames`），兩邊不會分岔。測試：`RolePresetsTests`。
- **維護表單**（Modal）：
  - 名稱（必填，唯一）。
  - ~~預設團隊~~（0.4.99 起拿掉表單欄位，0.4.101 刪除資料庫欄位）。
  - **動作粒度權限矩陣**（角色項目）：依 `RolePermissionService` 的群組結構呈現。每個群組（母項，如「系統管理功能」）有一個群組核取方塊；群組下每個頁面節點提供「（全部）」核取方塊，以及五個動作核取方塊：檢視、新增、編輯、刪除、匯出（`view/create/edit/delete/export`）。
- **矩陣互動語意**：勾「（全部）」等同該頁裸鍵、代表全部動作，並停用個別動作核取方塊（舊制相容）；勾任一動作或頁面會自動點亮所屬群組；取消群組會連帶清掉其下所有頁面權限。

## 四、內部系統運作

View（`RoleViewView`）→ `RoleViewService` → `BackendDBContext`：

- **矩陣 ↔ 權限鍵**（`RolePermissionService`）：`GetPermissionInput` 將勾選狀態轉為權限鍵清單——群組名、裸頁面鍵（＝全動作），或 `PermissionKey.For(頁面, 動作)`（如「專案項目:edit」）；`SetPermissionInput` 反向回填矩陣。清單序列化為 `RoleView.TabViewJson`。
- **新增／修改**（`AddAsync`／`UpdateAsync`）：`CleanTrackingHelper.Clean` 清追蹤；以 `GetPermissionInputToJson` 產生 `TabViewJson` 存檔；再 `ParsePermissionKeys` 解析並呼叫 `RbacWriteService.SyncRolePermissionsAsync` 雙寫至 `RolePermissionMap`；寫 `Role.Create`／`Role.Update` 稽核（含權限鍵數）。
- **RBAC 雙寫**（`RbacWriteService.SyncRolePermissionsAsync`）：`EnsurePermissionsAsync` 對缺漏的權限鍵自動補建 `Permission` 列，再對 `RolePermissionMap` 差異化增刪，使角色權限與矩陣一致。
- **刪除**（`DeleteAsync`，0.4.110 修正）：`MyUser.RoleViewId` 是 Restrict 外鍵，0.4.109 以前只要有人以這個角色當主要角色，資料庫就擋下刪除，畫面卻照樣顯示「刪除成功」，看起來一直刪不掉。現在：
  - 確認視窗先顯示有幾位使用者掛著這個角色（`CountUsersAsync`，主要或額外都算）。
  - 刪除時在同一個交易裡，先把以它為主要角色的人換掉主要角色：優先用他身上的其他角色，沒有就用「一般使用者」，再沒有就用剩下的第一個角色（同時補上 `UserRole`）。任何人都不會變成沒有角色——沒有角色的帳號登入時會被登出。
  - **唯一擋下的情況：刪的是系統中最後一個角色**（「系統至少要保留一個角色」），否則所有帳號（含管理者）都會無法登入。
  - 額外角色的關聯（`UserRole`）與權限關聯（`RolePermissionMap`）由資料庫 Cascade 刪除；寫 `Role.Delete` 稽核（含換掉主要角色的人數）。
  - 畫面依回傳結果顯示成功或錯誤訊息。測試：`RoleViewDeleteTests`。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時由 `RolePermissionService` 建立權限目錄（`Permission`，含 `GroupName`／`SortOrder`），並依各角色 `TabViewJson` 補寫 `RolePermissionMap`，冪等。0.4.101 曾把「團隊清單」改名為「資料群組」，0.4.102 起 `RenameLegacyPermissionKeysAsync` 把「資料群組」（含 `資料群組:edit` 這類動作鍵）就地改回「團隊清單」（`Permission.Key` 與 `TabViewJson` 都改）。
- **預設角色**（`DefaultRoleSeeder`，每次啟動、冪等）：確保「一般使用者」存在，只在新建或改名時設初始權限，不動帳號的角色。
- **權限判定**（`PermissionChecker`）：使用者角色取自 `UserRole`（多角色）並容錯併入 legacy `RoleViewId`；join `RolePermissionMap`／`Permission` 得有效權限鍵集合；管理員短路回 true；擁有裸頁面鍵者視為具該頁全部動作。
- **前置檢查**：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查角色名稱唯一性。
- **新帳號預設角色**：`Get預設新建帳號角色Async` 以名稱「一般使用者」（`MagicObjectHelper.預設角色`）查詢，供新使用者預帶。

## 五、權限與安全

- RBAC 表（`Permission`／`RolePermissionMap`／`UserRole`）為 UI 與 API 共用的**單一權威**；登入後 `AuthenticationStateHelper` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync` 載入有效權限鍵（多角色聯集）。
- 動作級授權：API 控制器以 `[HasPermission(頁面, 動作)]` 判權，無權限回 `ApiResult` 403（`ForbiddenResult`）；未登入回 401；管理員短路一律通過。UI 以 `CheckAccessAction` 依動作顯示／停用按鈕。
- 角色本頁僅管理員可進入；不輸出任何機密欄位。

## 六、錯誤與邊界

- 角色名稱重複：前置檢查回「角色名稱已存在，無法新增／修改。」
- 修改對象不存在：回「找不到要修改的角色資料。」
- `TabViewJson` 解析失敗：`OtherDependencyData` 以空權限初始化矩陣（不致命）。
- 未設任何權限：該角色無有效權限鍵，成員（非管理員）將無對應頁面／動作。
- 矩陣使用未在目錄中的權限鍵時，雙寫會自動補建 `Permission` 列。

## 七、驗收與測試

- `MeetingRecord.Tests/RbacWriteServiceTests.cs`：`SyncRolePermissionsAsync_ShouldAddAndRemoveToMatchKeys`、`ShouldCreateMissingPermissionRows`。
- `MeetingRecord.Tests/RbacBackfillServiceTests.cs`：建立權限目錄、由 `TabViewJson` 連結角色權限、舊鍵「團隊清單」改名不掉權限、冪等。
- `MeetingRecord.Tests/DefaultRoleSeederTests.cs`（原 `RoleConsolidationServiceTests`）：舊名就地改名、沒有角色時新建、角色管理調整過的權限不被蓋回、不動帳號的角色、冪等。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`：管理員全通過、角色具／缺鍵、裸頁面鍵授予全動作、僅 `view` 不含 `edit`、多角色聯集。
- `MeetingRecord.Tests/AuditEventsTests.cs`：`Role.Create`／`Role.Delete` 稽核。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Admins/RoleViewPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/RoleViewView.razor:103`（權限矩陣）、`RoleViewView.razor.cs:368`（矩陣互動）、`:394`（動作欄定義）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/RoleViewService.cs:155`（Add）、`:188`（Update）、`:321`（回填矩陣）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs:95`（`SetPermissionInput`）、`:116`（`GetPermissionInput`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RbacWriteService.cs:16`（`SyncRolePermissionsAsync`）、`:84`（`EnsurePermissionsAsync`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/PermissionChecker.cs:16`（判定）、`RbacBackfillService.cs:35`（權限目錄）
- `src/MeetingRecord/MeetingRecord.Web/Filters/HasPermissionAttribute.cs:31`（API 403）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/PermissionKeys.cs:9`（`PermissionActions`／`PermissionKey`）
- RBAC 資料表：`RoleView`、`Permission`、`RolePermissionMap`、`UserRole`（`src/MeetingRecord/MeetingRecord.AccessDatas/Models/`）
- 交叉連結：[使用者管理](使用者管理-prd.md)、[登入與帳號流程](登入與帳號流程-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
