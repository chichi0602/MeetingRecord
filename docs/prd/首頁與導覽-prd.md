# 首頁與導覽 PRD

- 文件版本：1.8
- 文件狀態：已實作
- 現行系統版本：0.4.50
- 首次實作版本：既有腳手架核心功能（「關於」對話窗為 0.4.24 新增）
- 最後核對日期：2026/09/04

## 一、目標與範圍

提供系統的進入點與整體導覽骨架：未登入者的品牌 landing 畫面（`/`）、登入後導向工作頁，以及依權限過濾的側邊功能選單。

> **0.4.45：「首頁」（`/App`）已移除。** `HomeAuthed.razor` 只有一行 `<ProjectViewView/>`，與 `/projects` 是同一個畫面。頁面、選單項（id=1）、`MenuPermissionMap[1]` 與「首頁」權限鍵都已刪除，**登入後的降落點改為 `/meetings`**。

- 範圍：landing 路由與登入降落導向、側邊選單（`Menu.json`）之載入、宣告式權限過濾、收合／展開與圖示呈現，以及右上角使用者選單（含「關於」系統資訊對話窗）。
- 非範圍：各業務頁面（專案、使用者、角色、分類、團隊）之內容；登入／登出流程本身；權限鍵的授予（屬角色管理）。動作級授權與團隊資料權控見「紀錄分類與團隊權控 PRD」。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/` | 非選單（landing） | 無（`EmptyLayout`，任何人） | 未登入訪客 |
| `/dashboard` | 選單 id=11「儀表板」 | 頁面鍵「儀表板」（管理員豁免） | 已登入使用者（0.4.54 新增） |
| `/meetings` | 選單 id=61「會議紀錄」 | 頁面鍵「會議紀錄」（管理員豁免） | 已登入使用者（0.4.45 起的登入降落點） |
| 側邊選單 | — | 各項目依 `MenuPermissionMap` 對應之權限鍵過濾 | 已登入使用者 |

## 三、畫面與欄位

- Landing（`/` → `Home.razor` → `SplashView`）：品牌圖示、標題「Blazor 開發啟動範本專案」、說明文字與「系統載入中」狀態列；採 `EmptyLayout`，不含側邊選單。
- 登入降落（0.4.45 起）：帳密登入、Google 登入、根路徑 `/` 的啟動畫面三條路都導向 `/meetings`（會議紀錄）。原本的 `/App` 頁面已刪除。
- 側邊選單（`NavMenu.razor` + `SidebarMenuNode`）：
  - 依 `Menu.json` 階層渲染，支援展開與「收合」兩種型態（收合時以圖示 flyout 呈現）。
  - 每項含 `name`、`icon`（Material 圖示）、`url` 或子選單 `subMenu`。
  - 無任何可用項目時顯示「尚無可用選單」。
  - 0.4.33 起分為兩個區塊：**核心功能**（儀表板／會議紀錄／專案項目／待辦事項，皆為頂層項；儀表板為 0.4.54 新增）與**功能選單**（系統管理〔使用者管理／角色管理〕、資料定義〔分類清單／團隊清單／提示詞清單〕、登出）。區塊由 `Menu.json` 頂層節點的 `section` 欄位宣告，加新區塊不需改程式。
  - **標題區（0.4.44 改版，0.4.47～0.4.49 調整尺寸）**：專案標誌（與登入頁、`favicon.svg` 同一份三橢圓「AI」線稿，以 `currentColor` 描邊）＋ 系統名稱 ＋ 副標「管理後台功能清單」。系統名稱讀 `SystemSettings.SystemInformation.SystemName`，**不寫死**。收合態只留標誌（0.4.46：標題文字改用 `display: none`，`opacity`／`width` 歸零會留下高度把標題列撐長）。**標誌尺寸略大於該狀態的選單圖示**（展開 2rem、收合 2.5rem；等大會顯得單薄，且 SVG 的 viewBox 已收緊為 `5 5 110 110` 去掉四周空白），頁首高度與底線則與 `MainLayout` 的 `.top-row` 完全相同（`min-height: 4.5rem` ＋ 1px 底線），兩區的頁首邊界才會切齊。
  - **收合鈕（0.4.44 起移出側邊欄）**：改由 `MainLayout.razor` 渲染，浮在側邊欄右邊界上、垂直置中，收合／展開時跟著滑動。放在 `.sidebar` 外面是因為 `.sidebar` 有 `overflow: hidden`，擺在裡面沒辦法跨出邊界。
  - **選中樣式（0.4.45 定案）**：`--oat-300`（#D4BDA8）圓角色塊，比側邊欄底色深一階，文字與圖示皆為深棕（對比 6.4:1），無色條、無陰影；hover 用淺一階的 `--oat-200` 以資區別。收合態的圖示按鈕套同一套。
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
- id→權限鍵對應（節錄）：11→`角色_儀表板`「儀表板」、21→`角色_專案項目`「專案項目」、22→`角色_待辦事項`「待辦事項」、61→`角色_會議紀錄`「會議紀錄」、3→`角色_系統管理`「系統管理功能」、31→`角色_使用者管理`「使用者管理」、32→`角色_角色管理`「角色管理」、5→`角色_資料定義`「資料定義管理功能」、51→`角色_分類清單`「分類清單」、52→`角色_團隊清單`「團隊清單」、53→`角色_提示詞清單`「提示詞清單」、4→`角色_登出`「登出」。
- 選單過濾僅隱藏無權項目，並非授權邊界；實際資料存取由 API 端 `[HasPermission]` 與團隊權控把關（見「紀錄分類與團隊權控 PRD」）。
- 管理員（`IsAdmin`）於 `CheckAccessPage` 短路，選單全可見。
- 右上角使用者選單與「關於」對話窗不做權限過濾：任何已登入者皆可開啟；內容僅為系統識別資訊，不含連線字串、金鑰或其他機敏設定。

## 六、錯誤與邊界

- 找不到／無法解析 `Menu.json`：記錄警告或錯誤並回傳空清單，選單顯示「尚無可用選單」，不致中斷頁面。
- 未登入者進入 `/meetings` 等受保護頁：`AuthenticationStateHelper.Check` 導向 `/Auths/Logout`；帳號停用、缺角色或需改密碼者亦於此攔截並導向。
- 使用者無任一項目權限：選單為空，僅顯示提示文字。

## 七、驗收與測試

- `MeetingRecord.Tests/MenuIconTests.cs::MenuJson_AllIcons_ShouldBeNonEmptyAndAllowed`：`Menu.json` 每項圖示非空且屬允許集合。
- 手動驗收：以不同角色登入，確認選單僅顯示具權限之項目；管理員可見全部；重排 `Menu.json` 順序不影響權限對應。
- 手動驗收（關於）：點右上角使用者名稱 →「關於」，對話窗顯示七列資訊，系統版本須與 `appsettings.json` 之 `SystemVersion` 一致；關閉後再次開啟，「已運作時間」應有增加。
- 權限判定來源之測試見 `PermissionCheckerTests.cs`（詳「紀錄分類與團隊權控 PRD」）。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Home.razor:1`（`/` landing）
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
