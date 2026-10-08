# 紀錄分類與團隊權控 PRD

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.0
- 最後核對日期：2026/10/08

> **0.4.99 起，本文件描述的「紀錄上的團隊標籤字串」權控不再影響任何資料的可見性。** 0.4.102 起的現行機制是**主責＋協作團隊**：團隊＝誰的資料（部門，`Team`／`UserTeam`），專案透過 `ProjectTeam` 關聯表掛恰好一個主責團隊（`IsPrimary`，必填）加 0～多個協作團隊（比對 Id，不是名稱字串）；使用者所屬團隊與主責或協作有交集才看得到，**沒有公開專案**；會議跟著專案走（未歸屬的只有上傳者看得到），提示詞範本對所有人開放；判斷一律經 `ProjectAccessService`。角色的預設團隊（`RoleView.DefaultTeamsJson`）已刪除，`EffectiveTeamResolver` 只回傳使用者直接所屬的團隊。`Meeting`／`PromptTemplate` 的 `Teams` 字串欄位 0.4.99 起不再作用，**0.4.103 已刪除**（migration `RemoveLegacyTeamTags`，既有標籤資料一併刪除），`TagStringHelper.BuildTeamAccessPredicate`／`IsTeamAccessible`（「沒標團隊＝公開」的舊規則）與 `DataRequest.TeamFilters` 也一併刪除。**本文件第二節以後凡提到紀錄上的 `Teams` 標籤、團隊過濾與這兩個方法的段落，都是 0.4.98 以前的歷史紀錄。**
>
> **分類＝什麼資料**：0.4.102 起專案（新欄位 `Project.Categories`）與會議（`Meeting.Categories`）又會寫入分類標籤，仍以本文件描述的 `TagStringHelper` 換行包夾字串存名稱、以「含任一即符合」過濾，**不影響可見性**。
>
> 沿革：0.4.101 曾把 `/teams` 改名「資料群組」、規則是「專案沒掛群組＝公開」，0.4.102 改回「團隊清單」並取消公開專案。現行規則見 [團隊清單 PRD](團隊清單-prd.md)、[專案項目 PRD](專案項目-prd.md)、《系統功能總覽》第五節與《開發慣例與限制速查》§4.0。

## 一、目標與範圍

這是一份跨功能的權威文件，定義兩件貫穿所有業務紀錄（目前為專案項目與會議紀錄提示詞）的能力：

1. 紀錄的「分類 / 團隊」多值標籤欄位（分類供檢索、團隊供資料權控）。
2. 團隊資料權控（列級可見性）與動作級 RBAC 授權（API 動作級把關）。

授權判定為單一權威來源，UI 與 API 共用；其他 PRD（首頁與導覽等）連結至本文件。

- 範圍：標籤欄位的儲存與過濾、使用者「有效團隊」解析、紀錄可見範圍規則、`[HasPermission]` 動作級授權、管理員豁免。
- 非範圍：分類清單／團隊清單的 CRUD 頁面本身；角色與權限鍵的授予流程（屬角色管理）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/projects` | 專案管理子選單 | 頁面鍵，動作另需 `頁面:動作` | 一般使用者 |
| `/prompttemplates` | 資料定義子選單 | 提示詞清單頁面鍵，動作另需 `頁面:動作` | 管理者 |
| API `Project`／`PromptTemplate` Controller | 非選單 | `[HasPermission(頁面, 動作)]` | UI 呼叫端 / 外部（JWT） |
| `/categories`、`/teams` | 資料定義子選單 | 分類清單／團隊清單頁面鍵（0.4.101 曾改名「資料群組」，0.4.102 改回） | 管理者 |

（原表列的 `/Task`、`/meeting` 兩條路由已於 0.4.24 隨「工作項目」「會議記錄」移除，本次一併校正。）

分類與團隊為系統層級主檔；各紀錄以多值標籤引用其名稱。

### ⚠️ 會議紀錄自 0.4.35 起不再從 UI 設定團隊（歷史）

> 本小節是 0.4.35～0.4.98 的狀態。0.4.99 起會議跟著專案走；**0.4.103 `Meeting.Teams` 欄位與相關判斷已刪除**，下述「加回團隊 `Select` 即可恢復」已不成立。

會議紀錄頁（`/meetings`）於 0.4.35 移除了分類／團隊的清單欄位、過濾器與表單欄位，定位收斂為「上傳音檔 → 產出逐字稿」，歸屬改在專案項目頁處理。

當時 `Meeting.Categories` / `Meeting.Teams` 兩個資料庫欄位與 `MeetingService` 的 11 處權限判斷**都保留未動**，因此：

- 舊資料維持原本的可見範圍；
- 但**新建的會議 `Teams` 恆為 `null`＝公開**，等於會議的列級權控對新資料失效。

這是使用者在知悉副作用後所做的決定。要恢復控管只需把表單的團隊 `Select` 加回 `MeetingViewView.razor`。細節見 [會議紀錄 PRD](會議紀錄-prd.md)。

### 專案項目已完全退出團隊權控（0.4.39）

`Project` 的 `Categories`／`Teams` 兩個欄位已於 0.4.39 **從資料庫中刪除**（migration `RemoveProjectDescriptionPriorityCategoriesTeams`），連同 6 處團隊權限判斷一併移除：`ProjectService` 4 處、`MeetingService.RequestDraftAsync` 的專案守門、`TodoService.BeforeAddCheckAsync`。

設計理由（使用者決定）：**角色權限只決定「能做什麼功能」，不決定「能看到哪些資料」**。專案的存取改為純粹的功能級 RBAC——具備「專案項目」頁面權限即可看到所有專案，動作另以 `[HasPermission]` 控管。

因此本文件的團隊可見性規則**不再適用於 `Project`**，當時僅適用於 `Meeting` 與 `PromptTemplate`（0.4.99 起兩者也不適用，0.4.103 欄位已刪除；`Todo` 的兩個欄位也已於 0.4.66 移除，migration `RemoveTodoCategoriesTeams`）。細節見 [專案項目 PRD](專案項目-prd.md)。

## 三、畫面與欄位

> 以下「團隊」多選、團隊過濾、`TeamsText` 欄與可見範圍規則皆為 0.4.98 以前的設計，**0.4.103 已全數刪除**；現行畫面只剩「分類」。

- 紀錄編輯（如 `ProjectViewView` Modal）：
  - 「分類」多選，供檢索過濾，不影響可見性。
  - 「團隊」多選，Placeholder「選擇團隊（不設定表示公開）」；決定該筆紀錄的可見範圍。
- 清單工具列：分類過濾、團隊過濾（多選）與關鍵字搜尋。
- 表格欄位：分類（`CategoriesText`）、團隊（`TeamsText`）以文字呈現。
- 可見範圍規則（非管理員）：
  - 無團隊（null 或空）＝ 公開，任何人可見。
  - 有團隊＝ 僅當紀錄團隊與使用者「有效團隊」有交集才可見。
  - 管理員一律可見全部。

## 四、內部系統運作

1. 標籤字串（`TagStringHelper`）：多值以換行分隔並前後包夾，例 `"\n團隊A\n團隊B\n"`。此格式可用 `Contains("\n團隊A\n")` 在 SQLite 做「精確成員」比對（本系統只支援 SQLite），避免子字串誤判（如「團隊」誤中「團隊2」）。
   - `ToStored` 去空白／去重（忽略大小寫，保留順序）；`ToList` 還原；`Wrap` 包單一名稱；`BuildContainsAnyPredicate` 產生「含任一即符合」的過濾述詞。0.4.103 起只用於分類（及專案的詞彙表、與會人員等多值欄位）。
2. 使用者有效團隊（`EffectiveTeamResolver.GetEffectiveTeamNamesAsync`）：直接綁定使用者的團隊（`UserTeam`）。~~0.4.100 以前另聯集使用者角色的預設團隊（`RoleView.DefaultTeamsJson`）~~，該欄位已於 0.4.101 刪除。
3. 存取範圍解析（`RecordAccessScopeProvider.GetAsync` → `RecordAccessScope(IsAdmin, Teams)`）：
   - Blazor 互動情境用已填入的 `CurrentUserService`。
   - Web API／檔案下載（JWT/Cookie）情境由 `HttpContext` 的 Sid claim 載入使用者並解析有效團隊。
   - 兩者皆無法解析時回傳「非管理員、無團隊」（0.4.98 以前等於只能看到公開紀錄；0.4.102 起沒有公開專案，等於看不到任何專案資料）。
4. 查詢範圍套用：0.4.99 起一律經 `ProjectAccessService`（見開頭說明）。~~非管理員時以 `TagStringHelper.BuildTeamAccessPredicate` 於查詢加上「公開或團隊交集」述詞；單筆讀取／子項存取以 `IsTeamAccessible` 判斷~~——0.4.98 以前的做法，**這兩個方法 0.4.103 已刪除**。管理員短路看全部。
5. 動作級授權（`HasPermissionAttribute`）：解析呼叫者 userId 後委由 `IPermissionChecker.HasPermissionAsync` 判定；管理員短路，擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。

## 五、權限與安全

- 單一權威來源：`IPermissionChecker` 為 UI（`AuthenticationStateHelper.CheckAccessPage`／`CheckAccessAction`）與 API（`[HasPermission]`）共用的權限判定來源，兩端一致。
- 宣告式頁面權限：`Menu.json` 唯一 `id` ＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數（見「首頁與導覽 PRD」）。
- 動作級 RBAC：受保護 CRUD 以 `[HasPermission("頁面", "動作")]` 標註（View/Create/Edit/Delete/Export）；未登入回 401、無權限回 403，皆維持 `ApiResult` 格式。
- 團隊權控為資安不變量：列級可見性由伺服器端（`ProjectAccessService`）強制，UI 過濾僅為輔助，不可作為授權邊界。
- 管理員豁免：`IsAdmin` 於 `PermissionChecker`、`CheckAccessPage/Action`、`ProjectAccessService`、查詢範圍皆短路，一律通行且可見全部。

## 六、錯誤與邊界

- 無分類：無分類標籤；過濾清單空表示不套用該過濾。~~無團隊標籤＝公開~~（0.4.98 以前的規則，0.4.103 已刪除；0.4.102 起沒有公開專案）。
- 非管理員且不屬於任何團隊：看不到任何專案與其下的會議、待辦，只看得到自己上傳的未歸屬會議（0.4.98 以前是「僅見公開紀錄」）。
- 標籤精確比對避免「團隊」誤命中「團隊2」等子字串問題。
- API 未登入回 401、越權回 403，維持 `ApiResult`，不洩漏資料。
- 子項（如附件）存取沿用父紀錄的團隊可見性判斷。

## 七、驗收與測試

- `MeetingRecord.Tests/TagStringHelperTests.cs`：`ToStored_ThenToList_ShouldRoundTrip`、`ToStored_ShouldTrimDeduplicateAndDropBlanks`、`ToStored_WithNoValidValues_ShouldReturnNull`、`BuildContainsAnyPredicate_ShouldMatchExactMemberOnly`、`BuildContainsAnyPredicate_WithEmptyValues_ShouldMatchAll`（0.4.103 起只測分類標籤用得到的字串處理；`IsTeamAccessible_*` 已隨方法刪除）。
- `MeetingRecord.Tests/EffectiveTeamResolverTests.cs`：`ShouldReturnDirectUserTeams`、`ShouldReturnAllDirectTeamsOnly`、`ShouldReturnEmptyForUnknownUser`（角色預設團隊已於 0.4.101 刪除，原 `ShouldReturnRoleDefaultTeams`／`ShouldUnionAndDeduplicate` 隨之移除）。
- ~~`MeetingRecord.Tests/ProjectServiceTeamAccessTests.cs`~~：已不存在（團隊標籤可見性測試隨 0.4.39／0.4.99／0.4.103 陸續移除）；現行可見性測試見 `ProjectAccessTests.cs`。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`：`HasPermissionAsync_ForAdmin_ShouldReturnTrueForAnyKey`、`HasPermissionAsync_WhenRoleHasKey_ShouldReturnTrue`、`HasPermissionAsync_LegacyBarePageKey_ShouldGrantAnyActionOfThatPage`、`HasPermissionAsync_GranularViewOnly_ShouldNotGrantEdit`、`GetEffectivePermissionKeysAsync_WithMultipleRoles_ShouldReturnUnion`。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Business/Helpers/TagStringHelper.cs`（標籤字串與 `BuildContainsAnyPredicate`；`BuildTeamAccessPredicate`／`IsTeamAccessible` 0.4.103 已刪除）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/EffectiveTeamResolver.cs`（`GetEffectiveTeamNamesAsync`，有效團隊解析）
- `src/MeetingRecord/MeetingRecord.Web/Auth/RecordAccessScopeProvider.cs`（`GetAsync`，存取範圍解析）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/IRecordAccessScopeProvider.cs`（`RecordAccessScope`）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/ProjectService.cs`、`src/MeetingRecord/MeetingRecord.Business/Services/Other/ProjectAccessService.cs`（查詢範圍套用，0.4.99 起）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/PermissionChecker.cs`（`HasPermissionAsync`：先擋停用帳號，再做管理員短路）
- `src/MeetingRecord/MeetingRecord.Web/Filters/HasPermissionAttribute.cs`（`OnAuthorizationAsync`，401/403 與 `ApiResult`）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/ProjectController.cs`（各動作方法上的 `[HasPermission]` 動作級標註）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/AuthenticationStateHelper.cs`（`CheckAccessAction`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/PermissionKeys.cs`（`PermissionKey.For`／`PageOf`）
- 交叉連結：[認證授權與權限機制](../security/認證授權與權限機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)、[紀錄標籤與團隊存取設計](../superpowers/specs/2026-06-22-record-tags-team-access-design.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
