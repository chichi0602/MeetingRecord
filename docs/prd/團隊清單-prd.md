# 團隊清單 PRD

- 文件版本：2.1
- 文件狀態：已實作
- 現行系統版本：0.4.109
- 首次實作版本：0.3.0
- 最後核對日期：2026/10/01

> **0.4.102：名稱改回「團隊清單」，團隊＝部門＝「誰的資料」。** 三者分工：**團隊＝誰的資料**（例如業務部、管理部、研發部，決定看得到哪些專案）、**分類＝什麼資料**（描述用標籤，見 [分類清單](分類清單-prd.md)）、**角色＝能做什麼**（見 [角色管理](角色管理-prd.md)）。每個專案恰好一個**主責團隊**加 0～多個**協作團隊**，**沒有公開專案**。團隊還是某專案的主責團隊時不能刪。
>
> 沿革（0.4.101 的繞道）：0.4.99～0.4.100 曾把本頁從選單移除、改用專案成員（負責人／協作者）控管；0.4.101 以「資料群組」之名回到選單（專案沒掛群組＝公開、新專案帶入建立者全部群組）；使用者覺得「資料群組」不好懂，0.4.102 改回「團隊」並改成主責＋協作。0.4.101 的「資料群組」權限鍵在啟動時就地改回「團隊清單」。升級時舊專案成員與 0.4.101 的資料會補上主責團隊（見 [專案項目 PRD](專案項目-prd.md) 與 [開發慣例與限制速查 §4.0](../architecture/開發慣例與限制速查.md)）。

## 一、目標與範圍

提供「團隊」（Entity `Team`）主資料與其成員的維護能力，讓具權限的使用者在 `/teams` 頁面完成查詢、新增、修改、刪除，並在同一表單增減成員。`Name` 唯一（不分大小寫），`Code` 為選填、有填則須唯一。

團隊如何決定可見範圍：

- 使用者所屬的團隊存在 `UserTeam`（本頁「成員」欄位與使用者管理的「團隊」欄位是同一份），一個人可以屬於多個團隊。
- 專案的團隊存在 `ProjectTeam`（在專案表單設定）：恰好一個主責團隊（`IsPrimary = true`，必填）加 0～多個協作團隊。使用者所屬的任一團隊是專案的主責或協作團隊就看得到；**沒有公開專案**。會議、待辦、附件、AI 問答跟著專案走。
- 例：王小明屬於研發部＋管理部；專案 A 主責研發部、B 主責管理部、C 主責業務部、D 主責業務部但協作有研發部 → 他看得到 A、B、D，看不到 C。
- 管理者（`IsAdmin`）不受限。判斷一律經 `ProjectAccessService`，比對的是 Id，不是名稱，改團隊名稱不影響可見性。

非範圍：
- 不做階層或組織圖（純平面清單）。
- 不做匯入／匯出、軟刪除（刪除為實體刪除）。批次把一群人加入／移出團隊在 [使用者管理](使用者管理-prd.md) 做。
- 分類清單上的「負責團隊（參考）」（`CategoryTeam`）只是參考資訊，不影響可見性，維護在 [分類清單](分類清單-prd.md)。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/teams`（`TeamPage.razor`，`MainLayout`） |
| 選單路徑 | 資料定義（id=5）> 團隊清單（id=52，icon `groups`，`url=/teams`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[52] = 角色_團隊清單` |
| UI 頁面權限 | 頁面鍵「團隊清單」（`MagicObjectHelper.角色_團隊清單`，`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `團隊清單:view` / `團隊清單:create` / `團隊清單:edit` / `團隊清單:delete` |
| 主要使用者 | 具「團隊清單」角色權限者（**不在一般使用者的初始權限裡**）；系統管理員無條件可存取 |

> 0.4.101 的權限鍵「資料群組」（含 `資料群組:edit` 等動作鍵）由 `RbacBackfillService.RenameLegacyPermissionKeysAsync` 在啟動時就地改回「團隊清單」（`Permission.Key` 與 `RoleView.TabViewJson`），原本有此權限的角色照樣有。

## 三、畫面與欄位

單頁清單 + Modal 表單（`TeamViewView`）：

- 搜尋：關鍵字比對 `Name`、`Code` 或 `Description`（`Contains`）。
- 排序：可排序欄位 `Name`、`Code`、`IsEnabled`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、代號、描述、**成員數**（0.4.101）、啟用狀態、更新時間、操作（修改／刪除）。新增、修改、刪除按鈕依團隊清單的動作權限顯示（0.4.109）。「啟用狀態」膠囊在有團隊清單修改權限時可直接點擊切換（0.4.105，二次確認；`TeamService.SetEnabledAsync`）。
- 新增／編輯表單欄位：
  - 名稱 `Name`（必填，最長 100）
  - 代號 `Code`（選填，最長 50，有填須唯一）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）；停用只是不再出現在下拉，**不影響已掛上的可見性**
  - **成員**（0.4.101，多選啟用中的帳號；`TeamService.GetSelectableMembersAsync` 提供選項、`GetMemberIdsAsync` 回填、`SyncMembersAsync` 差異化增刪 `UserTeam`）
  - 描述 `Description`（選填，最長 2000）
- 刪除：二次確認，提示不可復原。**非管理者刪除還是某專案主責團隊的團隊時擋下**（管理者不受限，0.4.104），顯示「這個團隊還是以下專案的主責團隊，請先到專案改主責團隊再刪除：A、B」。

## 四、內部系統運作

- UI 路徑：`TeamViewView` →（注入）`TeamService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`TeamController` → `TeamRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Team`（`Id/Name/Code/Description/IsEnabled/CreatedAt/UpdatedAt`），DbSet 為 `context.Team`。關聯：`UserTeam`（使用者↔團隊）、`ProjectTeam`（專案↔團隊，含 `IsPrimary`）、`CategoryTeam`（分類↔團隊，參考用，0.4.102），皆 `(外鍵, TeamId)` 唯一、兩側 Cascade。
- **刪除保護**（0.4.102）：`TeamService.BeforeDeleteCheckAsync` 與 `DeleteAsync`、API 的 `TeamRepository.DeleteAsync` 對**非管理者**以 `ProjectTeamWriter.PrimaryProjectTitlesAsync` 查這個團隊是哪些專案的主責，有就擋（API 回 409）；管理者不受此限（0.4.104）：照刪，受影響的專案就沒有主責團隊，之後編輯專案時再補；下次啟動時 `TeamConversionService` 會替沒有主責的專案補上（取協作團隊中 Id 最小的，沒有就掛「待分配」）。訊息由 `ProjectTeamWriter.PrimaryInUseMessage` 組出。只當協作團隊的可以刪，Cascade 刪掉那筆 `ProjectTeam`，只會縮小可見範圍。
- 其他頁面取選項：`GetAllEnabledNamesAsync()`（使用者管理的團隊下拉）；專案表單的「主責團隊」下拉由 `ProjectService` 提供（管理者全部啟用中團隊，一般使用者只有自己所屬的），「協作團隊」下拉是全部啟用中團隊；分類清單的「負責團隊（參考）」由 `CategoryService` 提供啟用中團隊。
- 查詢一律 `AsNoTracking()`；新增前 `CleanTrackingHelper.Clean<Team>` 清追蹤，寫入後再清一次。
- 編輯前於 UI 以 `Clone()` 複製記錄；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified/Deleted` 提交。
- 模型變更需在 `MeetingRecord.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`團隊清單:view`、`團隊清單:create`、`團隊清單:edit`、`團隊清單:delete`。裸鍵「團隊清單」代表該頁全部動作（向後相容）。
- ⚠️ 能進本頁的人可以把任何帳號加進任何團隊，等於能決定誰看得到哪些專案——這個權限只該給管理者或少數負責權控的人。UI 頁面層只做 `CheckAccessPage`，進得去就能增刪改。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面，API 用 JWT Bearer 並以動作鍵控制個別操作。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（`ToLower()` 不分大小寫，修改時排除自身），重複回「團隊名稱已存在，無法新增／修改。」。API 端另以 `ExistsByNameAsync` 回 409 Conflict。
- 代號重複：僅在 `Code` 非空白時檢查唯一（`ExistsByCodeAsync`），重複回「團隊代號已存在」/409；空白代號可重複（見測試 `WithEmptyCode...`）。
- 非管理者刪除仍是主責的團隊：UI 顯示上述「請先到專案改主責團隊再刪除」訊息；API 回 409 Conflict。管理者照刪（測試 `DeleteTeam_Admin_IsNeverBlocked`）。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的團隊資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱必填、各欄長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service try/catch 回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

## 七、驗收與測試

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/TeamServiceTests.cs`：

- `BeforeAddCheckAsync_WithUniqueNameAndCode_ShouldSucceed`：名稱與代號皆唯一可新增。
- `BeforeAddCheckAsync_WithDuplicateName_ShouldFail`：名稱重複被拒。
- `BeforeAddCheckAsync_WithDuplicateCode_ShouldFail`：代號重複被拒。
- `BeforeAddCheckAsync_WithEmptyCode_ShouldSucceedEvenIfAnotherEmptyCodeExists`：空代號不觸發唯一檢查。
- `BeforeUpdateCheckAsync_WithSameRecord_ShouldSucceed`：同一筆用原名／原代號可通過。
- `BeforeUpdateCheckAsync_WithCodeUsedByOtherRecord_ShouldFail`：代號被他筆占用被拒。
- `AddAsync_ShouldPersistTeam`：新增後可查回並保留代號與啟用狀態。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper。

團隊的可見規則、刪除保護與成員同步另見 `ProjectAccessTests.cs`（`WangXiaoMing_SeesProjectsWherePrimaryOrCollaboratorIsHisTeam`、`ProjectWithoutTeams_IsNotPublic_OnlyAdminSeesIt`、`DeleteTeam_NonAdmin_StillPrimary_ShouldBeBlocked_CollaboratorOnly_ShouldPass`、`SyncMembers_ShouldAddAndRemove_AndChangeVisibility`、`BatchAddAndRemove_ShouldOnlyTouchSelectedUsers` 等）；權限鍵改回見 `RbacBackfillServiceTests.RunAsync_ShouldRenameLegacyTeamListKeyWithoutLosingGrants`。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Teams/TeamPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Teams/TeamViewView.razor.cs:70`（頁面權限檢查）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/TeamController.cs:36`（`[HasPermission]` 動作鍵）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/TeamService.cs:122`（AddAsync / 前置檢查含代號唯一、刪除保護）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/Team.cs:8`（Entity 欄位）、`UserTeam.cs`、`ProjectTeam.cs`、`CategoryTeam.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/ProjectAccessService.cs`（可見規則的唯一入口）、`ProjectTeamWriter.cs`（主責團隊寫入與刪除保護）
- `src/MeetingRecord/MeetingRecord.Dtos/Models/TeamCreateUpdateDto.cs:9`、`src/MeetingRecord/MeetingRecord.Dtos/Commons/TeamSearchRequestDto.cs:6`
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs:38`、`src/MeetingRecord/MeetingRecord.Share/Helpers/PermissionKeys.cs:9`
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs:28`、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json:63`
- `src/MeetingRecord/MeetingRecord.Tests/TeamServiceTests.cs:1`
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../superpowers/specs/2026-06-22-category-team-pages-design.md](../superpowers/specs/2026-06-22-category-team-pages-design.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
