# 紀錄分類與團隊權控 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.24
- 首次實作版本：0.4.0
- 最後核對日期：2026/08/17

## 一、目標與範圍

這是一份跨功能的權威文件，定義兩件貫穿所有業務紀錄（目前為專案）的能力：

1. 紀錄的「分類 / 團隊」多值標籤欄位（分類供檢索、團隊供資料權控）。
2. 團隊資料權控（列級可見性）與動作級 RBAC 授權（API 動作級把關）。

授權判定為單一權威來源，UI 與 API 共用；其他 PRD（首頁與導覽等）連結至本文件。

- 範圍：標籤欄位的儲存與過濾、使用者「有效團隊」解析、紀錄可見範圍規則、`[HasPermission]` 動作級授權、管理員豁免。
- 非範圍：分類清單／團隊清單的 CRUD 頁面本身；角色與權限鍵的授予流程（屬角色管理）。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/projects`、`/Task`、`/meeting` | 專案管理子選單 | 頁面鍵，動作另需 `頁面:動作` | 一般使用者 |
| API `Project` Controller | 非選單 | `[HasPermission(頁面, 動作)]` | UI 呼叫端 / 外部（JWT） |
| `/categories`、`/teams` | 資料定義子選單 | 分類清單／團隊清單頁面鍵 | 管理者 |

分類與團隊為系統層級主檔；各紀錄以多值標籤引用其名稱。

## 三、畫面與欄位

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

1. 標籤字串（`TagStringHelper`）：多值以換行分隔並前後包夾，例 `"\n團隊A\n團隊B\n"`。此格式可用 `Contains("\n團隊A\n")` 在 SQLite 與 SqlServer 做「精確成員」比對，避免子字串誤判（如「團隊」誤中「團隊2」）。
   - `ToStored` 去空白／去重（忽略大小寫，保留順序）；`ToList` 還原；`Wrap` 包單一名稱。
2. 使用者有效團隊（`EffectiveTeamResolver.GetEffectiveTeamNamesAsync`）：聯集兩來源並去重——
   - 直接綁定使用者的團隊（`UserTeam`）。
   - 使用者角色（`UserRole` ∪ legacy `RoleViewId`）的預設團隊（`RoleView.DefaultTeamsJson`）。
3. 存取範圍解析（`RecordAccessScopeProvider.GetAsync` → `RecordAccessScope(IsAdmin, Teams)`）：
   - Blazor 互動情境用已填入的 `CurrentUserService`。
   - Web API／檔案下載（JWT/Cookie）情境由 `HttpContext` 的 Sid claim 載入使用者並解析有效團隊。
   - 兩者皆無法解析時回傳「非管理員、無團隊」，僅能看到公開紀錄。
4. 查詢範圍套用（如 `ProjectService`）：非管理員時以 `TagStringHelper.BuildTeamAccessPredicate` 於查詢加上「公開或團隊交集」述詞；單筆讀取／子項存取以 `IsTeamAccessible` 判斷；管理員短路看全部。
5. 動作級授權（`HasPermissionAttribute`）：解析呼叫者 userId 後委由 `IPermissionChecker.HasPermissionAsync` 判定；管理員短路，擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。

## 五、權限與安全

- 單一權威來源：`IPermissionChecker` 為 UI（`AuthenticationStateHelper.CheckAccessPage`／`CheckAccessAction`）與 API（`[HasPermission]`）共用的權限判定來源，兩端一致。
- 宣告式頁面權限：`Menu.json` 唯一 `id` ＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數（見「首頁與導覽 PRD」）。
- 動作級 RBAC：受保護 CRUD 以 `[HasPermission("頁面", "動作")]` 標註（View/Create/Edit/Delete/Export）；未登入回 401、無權限回 403，皆維持 `ApiResult` 格式。
- 團隊權控為資安不變量：列級可見性由伺服器端查詢述詞強制，UI 過濾僅為輔助，不可作為授權邊界。
- 管理員豁免：`IsAdmin` 於 `PermissionChecker`、`CheckAccessPage/Action`、`IsTeamAccessible`、查詢範圍皆短路，一律通行且可見全部。

## 六、錯誤與邊界

- 無分類／團隊：紀錄視為公開（團隊）、無分類標籤（分類）；過濾清單空表示不套用該過濾。
- 非管理員且無有效團隊：僅見公開紀錄。
- 標籤精確比對避免「團隊」誤命中「團隊2」等子字串問題。
- API 未登入回 401、越權回 403，維持 `ApiResult`，不洩漏資料。
- 子項（如附件）存取沿用父紀錄的團隊可見性判斷。

## 七、驗收與測試

- `MeetingRecord.Tests/TagStringHelperTests.cs`：`ToStored_ThenToList_ShouldRoundTrip`、`ToStored_ShouldTrimDeduplicateAndDropBlanks`、`BuildContainsAnyPredicate_ShouldMatchExactMemberOnly`、`IsTeamAccessible_*`（公開／交集／無交集／管理員）。
- `MeetingRecord.Tests/EffectiveTeamResolverTests.cs`：`ShouldReturnDirectUserTeams`、`ShouldReturnRoleDefaultTeams`、`ShouldUnionAndDeduplicate`、`ShouldReturnEmptyForUnknownUser`。
- `MeetingRecord.Tests/ProjectServiceTeamAccessTests.cs`：`GetAsync_Admin_ShouldSeeAllRecords`、`GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords`、`GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords`、`GetAsync_WithTeamFilter_ShouldFilterByTeam`。
- `MeetingRecord.Tests/PermissionCheckerTests.cs`：`HasPermissionAsync_ForAdmin_ShouldReturnTrueForAnyKey`、`HasPermissionAsync_WhenRoleHasKey_ShouldReturnTrue`、`HasPermissionAsync_LegacyBarePageKey_ShouldGrantAnyActionOfThatPage`、`HasPermissionAsync_GranularViewOnly_ShouldNotGrantEdit`、`GetEffectivePermissionKeysAsync_WithMultipleRoles_ShouldReturnUnion`。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Business/Helpers/TagStringHelper.cs:12`（標籤字串、`BuildTeamAccessPredicate:110`、`IsTeamAccessible:142`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/EffectiveTeamResolver.cs:16`（有效團隊解析）
- `src/MeetingRecord/MeetingRecord.Web/Auth/RecordAccessScopeProvider.cs:34`（存取範圍解析）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/IRecordAccessScopeProvider.cs:6`（`RecordAccessScope`）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/ProjectService.cs:75`（查詢範圍套用）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/PermissionChecker.cs:16`（`HasPermissionAsync`，管理員短路 `:27`）
- `src/MeetingRecord/MeetingRecord.Web/Filters/HasPermissionAttribute.cs:31`（401/403 與 `ApiResult`）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/ProjectController.cs:36`（`[HasPermission]` 動作級標註）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/AuthenticationStateHelper.cs:202`（`CheckAccessAction`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/PermissionKeys.cs:18`（`PermissionKey.For`／`PageOf`）
- 交叉連結：[認證授權與權限機制](../security/認證授權與權限機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)、[紀錄標籤與團隊存取設計](../superpowers/specs/2026-06-22-record-tags-team-access-design.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
