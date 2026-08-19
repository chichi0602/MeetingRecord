# 首頁與導覽 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.24
- 首次實作版本：既有腳手架核心功能（「關於」對話窗為 0.4.24 新增）
- 最後核對日期：2026/08/17

## 一、目標與範圍

提供系統的兩個進入點與整體導覽骨架：未登入者的品牌 landing 畫面（`/`）、登入後的工作首頁（`/App`），以及依權限過濾的側邊功能選單。

- 範圍：landing／dashboard 兩個路由、側邊選單（`Menu.json`）之載入、宣告式權限過濾、收合／展開與圖示呈現，以及右上角使用者選單（含「關於」系統資訊對話窗）。
- 非範圍：各業務頁面（專案、使用者、角色、分類、團隊）之內容；登入／登出流程本身；權限鍵的授予（屬角色管理）。動作級授權與團隊資料權控見「紀錄分類與團隊權控 PRD」。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/` | 非選單（landing） | 無（`EmptyLayout`，任何人） | 未登入訪客 |
| `/App` | 選單 id=1「首頁」 | 頁面鍵「首頁」（管理員豁免） | 已登入使用者 |
| 側邊選單 | — | 各項目依 `MenuPermissionMap` 對應之權限鍵過濾 | 已登入使用者 |

## 三、畫面與欄位

- Landing（`/` → `Home.razor` → `SplashView`）：品牌圖示、標題「Blazor 開發啟動範本專案」、說明文字與「系統載入中」狀態列；採 `EmptyLayout`，不含側邊選單。
- Dashboard（`/App` → `HomeAuthed.razor` → `ProjectViewView`）：登入後首頁即專案清單檢視（工具列、分類／團隊過濾、搜尋、表格與新增／編輯 Modal），套用主版面與側邊選單。
- 側邊選單（`NavMenu.razor` + `SidebarMenuNode`）：
  - 依 `Menu.json` 階層渲染，支援展開與「收合」兩種型態（收合時以圖示 flyout 呈現）。
  - 每項含 `name`、`icon`（Material 圖示）、`url` 或子選單 `subMenu`。
  - 無任何可用項目時顯示「尚無可用選單」。
  - 現有結構：首頁、專案管理（專案項目）、系統管理（使用者管理／角色管理）、資料定義（分類清單／團隊清單）、登出。
- 右上角使用者選單（`MainLayout.razor`）：顯示目前使用者名稱與「管理員」標記，展開後含「變更密碼」「設定 API 密碼」「關於」「登出」四項。
- 「關於」對話窗（`MainLayout.razor` 之 `about-modal`）：以 AntDesign `Modal`（寬 520、無 Footer）呈現七列唯讀系統資訊。

  | 項目 | 來源 |
  | --- | --- |
  | 系統名稱 | `SystemSettings.SystemInformation.SystemName` |
  | 系統描述 | `SystemSettings.SystemInformation.SystemDescription` |
  | 系統版本 | `SystemSettings.SystemInformation.SystemVersion`（唯一版本來源） |
  | 執行環境 | `IWebHostEnvironment.EnvironmentName` |
  | .NET 版本 | `RuntimeInformation.FrameworkDescription` |
  | 啟動時間 | `SystemStartupState.StartedAt`（`yyyy/MM/dd HH:mm:ss`） |
  | 已運作時間 | `DateTimeOffset.Now - StartedAt`（`dd.hh:mm:ss`） |

## 四、內部系統運作

1. `SidebarMenuService.LoadAuthorizedMenuItemsAsync` 讀取選單並過濾：
   - `ReadMenuItemsFromDisk` 由 `MagicObjectHelper.Menu結構定義`（`Datas/Menu.json`）反序列化，經 `ICacheService` 快取（key `sidebar:menu:raw`）。
   - `ApplyPermissionStructure` 依每項唯一 `id` 從 `MenuPermissionMap`（id→權限鍵）填入 `PermissionName`；找不到對應時退回以 `Name` 為權限名。
   - `FilterAuthorizedMenuItems` 遞迴過濾：項目自身權限（`Name` 或 `PermissionName` 任一）通過，或其子項尚有可見項目時保留。
2. 權限判定唯一來源為 `AuthenticationStateHelper.CheckAccessPage(name)`：比對 `CurrentUser.RoleList`（由 `IPermissionChecker.GetEffectivePermissionKeysAsync` 供給的 RBAC 有效權限鍵集合）；管理員短路一律通過。
3. `Menu.json` 以 `id` 對應權限鍵，重排選單順序不會錯位（已移除舊「位置索引三處同步」耦合）。
4. 「關於」對話窗由 `MainLayout.OnAboutClick` 於**點擊當下**組出資料列：注入 `IOptions<SystemSettings>`、`IWebHostEnvironment` 與 Singleton `SystemStartupState`。已運作時間必須在開啟當下計算並存成欄位，否則 Blazor Server 不會自動刷新而顯示過期值。

## 五、權限與安全

- 頁面權限採宣告式三件組：`Menu.json`（每項唯一 `id`）＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數。
- id→權限鍵對應（節錄）：1→`角色_首頁`「首頁」、2→`角色_專案管理`「專案管理功能」、21→`角色_專案項目`「專案項目」、3→`角色_系統管理`「系統管理功能」、31→`角色_使用者管理`「使用者管理」、32→`角色_角色管理`「角色管理」、5→`角色_資料定義`「資料定義管理功能」、51→`角色_分類清單`「分類清單」、52→`角色_團隊清單`「團隊清單」、4→`角色_登出`「登出」。
- 選單過濾僅隱藏無權項目，並非授權邊界；實際資料存取由 API 端 `[HasPermission]` 與團隊權控把關（見「紀錄分類與團隊權控 PRD」）。
- 管理員（`IsAdmin`）於 `CheckAccessPage` 短路，選單全可見。
- 右上角使用者選單與「關於」對話窗不做權限過濾：任何已登入者皆可開啟；內容僅為系統識別資訊，不含連線字串、金鑰或其他機敏設定。

## 六、錯誤與邊界

- 找不到／無法解析 `Menu.json`：記錄警告或錯誤並回傳空清單，選單顯示「尚無可用選單」，不致中斷頁面。
- 未登入者進入 `/App` 等受保護頁：`AuthenticationStateHelper.Check` 導向 `/Auths/Logout`；帳號停用、缺角色或需改密碼者亦於此攔截並導向。
- 使用者無任一項目權限：選單為空，僅顯示提示文字。

## 七、驗收與測試

- `MeetingRecord.Tests/MenuIconTests.cs::MenuJson_AllIcons_ShouldBeNonEmptyAndAllowed`：`Menu.json` 每項圖示非空且屬允許集合。
- 手動驗收：以不同角色登入，確認選單僅顯示具權限之項目；管理員可見全部；重排 `Menu.json` 順序不影響權限對應。
- 手動驗收（關於）：點右上角使用者名稱 →「關於」，對話窗顯示七列資訊，系統版本須與 `appsettings.json` 之 `SystemVersion` 一致；關閉後再次開啟，「已運作時間」應有增加。
- 權限判定來源之測試見 `PermissionCheckerTests.cs`（詳「紀錄分類與團隊權控 PRD」）。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Home.razor:1`（`/` landing）
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/HomeAuthed.razor:1`（`/App` dashboard）
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Commons/SplashView.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs:16`（`MenuPermissionMap`）、`:46`（載入與過濾）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/NavMenu.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/MainLayout.razor:1`（使用者選單與「關於」對話窗）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/MainLayout.razor.cs:1`（`OnAboutClick`）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemStartupState.cs:1`（啟動時間來源）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/AuthenticationStateHelper.cs:179`（`CheckAccessPage`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs:28`（角色權限鍵常數）
- 交叉連結：[紀錄分類與團隊權控 PRD](紀錄分類與團隊權控-prd.md)、[認證授權與權限機制](../security/認證授權與權限機制.md)
