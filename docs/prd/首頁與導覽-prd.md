# 首頁與導覽 PRD

- 文件版本：1.16
- 文件狀態：已實作
- 現行系統版本：1.1.58
- 首次實作版本：樣板（NET10-Blazor-Starter）既有核心功能（「關於」對話窗為 0.4.24 新增）
- 最後核對日期：2026/11/13

## 一、目標與範圍

提供系統的進入點與整體導覽骨架：根路徑 `/` 導向工作頁、全站外框（側邊功能選單、頂端列、使用者選單），以及依權限過濾的選單。

> 系統沒有獨立的「首頁」功能頁，**登入後的降落點是 `/meetings`**（0.4.45 起）。

- 範圍：根路徑與登入降落導向、找不到頁、全站外框（`AppShell`）、側邊選單（`Menu.json`）之載入、宣告式權限過濾、收合／展開與圖示呈現，以及右上角使用者選單（含「關於」系統資訊對話窗）。
- 非範圍：各業務頁面（專案、使用者、角色、分類、團隊）之內容；登入／登出流程本身；權限鍵的授予（屬角色管理）。動作級授權與資料可見範圍見〈權限與資料可見範圍總覽〉。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/` | 非選單（導向 `/meetings`） | 登入即可（未登入由伺服器直接導到登入頁，1.1.58 起） | 已登入使用者 |
| `/not-found` | 非選單 | 登入即可 | 打錯網址的使用者 |
| `/dashboard` | 選單 id=11「儀表板」 | 無，登入即可看（0.4.97 起；列在 `PublicMenuIds`） | 已登入使用者（0.4.54 新增） |
| `/meetings` | 選單 id=61「會議紀錄」 | 頁面鍵「會議紀錄」（管理員豁免） | 已登入使用者（0.4.45 起的登入降落點） |
| `/help` | 選單 id=12「使用說明」 | 頁面鍵「使用說明」（管理員豁免） | 已登入使用者 |
| 側邊選單 | — | 各項目依 `MenuPermissionMap` 對應之權限鍵過濾 | 已登入使用者 |

## 三、畫面與欄位

- 首頁 `/`（`Web.Client/Pages/HomePage.razor`）：登入狀態交給 `WasmLayout`（停用、須改密碼各自導向；未登入在伺服器就導走了），通過後換到 `/meetings`（`replace`，上一頁不會回到 `/`）。沒有品牌開場畫面。
- **載入畫面**（1.1.58 起）：所有 WASM 頁在瀏覽器下載、啟動 WASM 的期間，畫面中央顯示系統名稱（`SystemSettings.SystemInformation.SystemName`）、轉圈動畫、「系統載入中，第一次開啟需要較長時間，請稍候…」與下載百分比。
  - 超過 20 秒還沒載完，多一段提示「載入時間比平常久，請確認網路連線；若一直沒有反應，請重新整理頁面或聯絡系統管理員。」與「重新整理」按鈕。
  - 外框（或「無法取得登入狀態」的訊息）畫好後才消失，中間不會閃一下空白。
  - 以前第一次開網址要先下載約 4.8 MB，這段時間一片空白，使用者分不出是系統壞了還是自己操作錯。開過一次以後檔案在瀏覽器快取裡，載入畫面通常一閃即過。
- 找不到頁 `/not-found`（`Web.Client/Pages/NotFoundPage.razor`）：「找不到這個頁面」，附回會議紀錄的連結；是 `ClientRoutes` 的 `NotFoundPage`，伺服器回 404 時也轉到這裡（`UseStatusCodePagesWithReExecute("/not-found")`）。不認得的網址只顯示一次這頁，不會反覆重新載入。
- 登入降落（0.4.45 起）：帳密登入、Google 登入、根路徑 `/` 三條路都導向 `/meetings`（會議紀錄）。
- 全站外框（`Web.Client/Components/Layout/AppShell.razor`，由 `WasmLayout` 掛上）：側邊欄、頂端列（頁面標題依目前網址比對 `Menu.json` 的項目名稱，比對不到顯示「系統首頁」）、通知鈴、使用者選單、變更密碼與「關於」對話窗、全站唯一的 `FormModalHelper`、背景工作進度面板與 `#blazor-error-ui`。頁面內容外包一層 `ErrorBoundary`，出錯時顯示「這個頁面發生未預期的錯誤，請重新整理頁面；若持續發生請聯絡系統管理員。」。
- 側邊選單（`Web.Client/Components/Layout/NavMenu.razor` + `SidebarMenuNode`）：
  - 依 `Menu.json` 階層渲染，支援展開與「收合」兩種型態（收合時以圖示 flyout 呈現）。0.4.120 起選單有三層：收合態的 flyout 遇到分組（沒有 `url`、有 `subMenu`）會畫成一列帶右箭頭的觸發鈕，懸停再往右彈出第二層 flyout，不把孫節點攤平；鍵盤以 `:has(:focus-visible)` 展開（移植自 NET10-Blazor-Starter）。
  - 每項含 `name`、`icon`（Material 圖示）、`url` 或子選單 `subMenu`。
  - 無任何可用項目時顯示「尚無可用選單」。
  - 0.4.33 起分為兩個區塊：**核心功能**（儀表板 `/dashboard`／會議紀錄 `/meetings`／專案項目 `/projects`／待辦事項 `/todos`／使用說明 `/help`，皆為頂層項；儀表板為 0.4.54 新增）與**功能選單**（系統管理〔0.4.120 起分組，1.0.30 起三組：帳號與權限（id 35，`manage_accounts`）＝使用者管理 `/myusers`／角色管理 `/roleviews`／稽核紀錄 `/audit-logs`（id 40，1.0.32）；監控與診斷（id 36，`analytics`）＝系統健康監控 `/system-health`／日誌檢視 `/logs`（0.4.121）／日誌等級設定 `/log-level-setting`／系統例外紀錄 `/system-exceptions`（0.4.122）／資料庫用量 `/database-usage`；AI 管理（id 62，`psychology`，1.0.30）＝AI 對話紀錄 `/ai-call-logs`（id 63，`forum`）／Token 用量 `/token-usage`（id 33，`toll`；原「監控與診斷」下的「AI 用量分析」`/ai-usage`，舊網址保留為別名）〕、資料定義〔分類清單／團隊清單／提示詞清單；「團隊清單」0.4.99～0.4.100 曾移除、0.4.101 以「資料群組」之名放回、0.4.102 改回原名〕、登出）。區塊由 `Menu.json` 頂層節點的 `section` 欄位宣告，加新區塊不需改程式。
  - **標題區（0.4.44 改版，0.4.47～0.4.49 調整尺寸）**：專案標誌（與登入頁、`favicon.svg` 同一份三橢圓「AI」線稿，以 `currentColor` 描邊）＋ 系統名稱 ＋ 副標「管理後台功能清單」。系統名稱讀 `SystemSettings.SystemInformation.SystemName`（經 Session API 的 `SystemName` 傳到前端），**不寫死**。收合態只留標誌（0.4.46：標題文字改用 `display: none`，`opacity`／`width` 歸零會留下高度把標題列撐長）。**標誌尺寸略大於該狀態的選單圖示**（展開 2rem、收合 2.5rem；等大會顯得單薄，且 SVG 的 viewBox 已收緊為 `5 5 110 110` 去掉四周空白），頁首高度與底線則與 `AppShell` 頂端列的 `.top-row` 完全相同（`min-height: 4.5rem` ＋ 1px 底線），兩區的頁首邊界才會切齊。
  - **收合鈕（0.4.44 起移出側邊欄）**：由 `AppShell.razor` 渲染，浮在側邊欄右邊界上、垂直置中，收合／展開時跟著滑動。放在 `.sidebar` 外面是因為 `.sidebar` 有 `overflow: hidden`，擺在裡面沒辦法跨出邊界。
  - **選中樣式（0.4.45 定案）**：`--oat-300`（#D4BDA8）圓角色塊，比側邊欄底色深一階，文字與圖示皆為深棕（對比 6.4:1），無色條、無陰影；hover 用淺一階的 `--oat-200` 以資區別。收合態的圖示按鈕套同一套。
- 右上角使用者選單（`AppShell.razor`）：顯示目前使用者名稱與「管理員」標記，展開後含「變更密碼」「關於」「登出」三項。「設定 API 密碼」不在選單上（0.4.113 起；`/Profile` 頁面保留，只能直接輸入網址進入）。
- 「關於」對話窗（`AppShell.razor`）：以 AntDesign `Modal`（`form-modal-compact`、無 Footer）呈現七列唯讀系統資訊（排版見 `AboutItems`）。

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

1. 伺服器送出 WASM 頁之前（`Web/Components/App.razor.cs`，1.1.58 起）：
   - 未登入就直接導到 `/Auths/Login?ReturnUrl=<原網址>`，不必先下載 WASM；登入後導回原網址。缺檔的 404 重新執行到 `/not-found` 時不導，維持 404。
   - 已登入就送出載入畫面（`#app-loading`，純 HTML／CSS）與 `wwwroot/js/app-loading.js`（20 秒提示、提供 `meetingRecordAppLoading.hide()`）；百分比是 `blazor.web.js` 寫在 `<html>` 的 `--blazor-load-percentage-text`。
2. `WasmLayout` 開頁時同時呼叫 `GET api/v1/session`（登入狀態與權限鍵）與 `GET api/v1/session/menu`（已過濾的選單）：登入在這期間失效（401）由 `ApiClient` 整頁導到 `/Auths/Login`；帳號刪除、停用或沒有角色導到 `/Auths/Logout?reason=…`；須改密碼導到 `/ChangePassword`；通過才畫出 `AppShell`，並註冊瀏覽器錯誤回報。外框或錯誤訊息畫好後呼叫 `meetingRecordAppLoading.hide()` 拿掉載入畫面；要整頁導走的不拿，留到換頁。
3. 選單 API（`SessionController.GetMenu`）呼叫 `SidebarMenuService.LoadAuthorizedMenuItemsAsync` 讀取選單並過濾：
   - `ReadMenuItemsFromDisk` 由 `MagicObjectHelper.Menu結構定義`（`Datas/Menu.json`）反序列化，經 `ICacheService` 快取（key `sidebar:menu:raw`）。
   - `ApplyPermissionStructure` 依每項唯一 `id` 從 `MenuPermissionMap`（id→權限鍵）填入 `PermissionName`；找不到對應時退回以 `Name` 為權限名。
   - `FilterAuthorizedMenuItems(items, canAccessPage, isAdmin)` 遞迴過濾（任何深度）：列在 `AdminOnlyMenuIds`（34、37、38、39、30、33、63、40）的項目非管理者一律排除、不看權限；其餘項目 id 列在 `PublicMenuIds`（免權限，0.4.97 起只有儀表板）、項目自身權限（`Name` 或 `PermissionName` 任一）通過，或其子項尚有可見項目時保留。列在 `GroupOnlyMenuIds`（1.0.30：35、36、62）的分組本身沒有權限，只看子項。
   - `ApplyPermissionStructure` 與 `FilterAuthorizedMenuItems` 皆為 `public static` 純函式，由 `MonitoringMenuRegistrationTests` 直接測試。
4. 進頁判定規則為 `AuthenticationStateHelper.CanAccessPage(權限鍵, isAdmin, name)`（純函式；有整頁鍵或「頁面:view」即可）：權限鍵由 `IPermissionChecker.GetEffectivePermissionKeysAsync`（RBAC 多角色聯集）供給；管理員短路一律通過。前端 `PermissionView.CanAccessPage` 用同一套規則決定頁面能不能進。
5. `Menu.json` 以 `id` 對應權限鍵，重排選單順序不會錯位。
6. 「關於」對話窗於**點擊當下**經 `GET api/v1/system/about` 取資料：伺服器端 `SystemAboutProvider` 讀 `IOptions<SystemSettings>`、`IWebHostEnvironment` 與 Singleton `SystemStartupState`，已運作時間在呼叫當下計算，所以每次開啟都是最新值。

## 五、權限與安全

- 頁面權限採宣告式三件組：`Menu.json`（每項唯一 `id`）＋ `SidebarMenuService.MenuPermissionMap`（id→權限鍵）＋ `MagicObjectHelper` 權限鍵常數。
- 免權限頁面：`SidebarMenuService.PublicMenuIds`（目前只有 11「儀表板」，0.4.97 起）。這類頁面不放進 `MenuPermissionMap`，也不列在角色管理的權限矩陣。
- 管理者限定頁面：`SidebarMenuService.AdminOnlyMenuIds`（0.4.120 起：34「系統健康監控」、38「日誌等級設定」、30「資料庫用量」；0.4.121 加入 37「日誌檢視」；0.4.122 加入 39「系統例外紀錄」，圖示 `bug_report`，排在日誌等級設定之後；1.0.30 加入「AI 管理」的 33「Token 用量」與 63「AI 對話紀錄」；1.0.32 加入「帳號與權限」的 40「稽核紀錄」）。非管理者一律看不到（舊資料庫殘留的權限列也無效）；不放進 `MenuPermissionMap`，也不列在權限矩陣，頁面以 `PermissionView.IsAdmin` 守門，資料 API 以 `[AdminOnly]` 擋下。
- 純分組：`SidebarMenuService.GroupOnlyMenuIds`（0.4.120 起：35「帳號與權限」、36「監控與診斷」；1.0.30 加入 62「AI 管理」），沒有權限鍵，底下有可見項目才顯示。
- id→權限鍵對應：12→`角色_使用說明`「使用說明」、21→`角色_專案項目`「專案項目」、22→`角色_待辦事項`「待辦事項」、61→`角色_會議紀錄`「會議紀錄」、3→`角色_系統管理`「系統管理功能」、31→`角色_使用者管理`「使用者管理」、32→`角色_角色管理`「角色管理」、5→`角色_資料定義`「資料定義管理功能」、51→`角色_分類清單`「分類清單」、52→`角色_團隊清單`「團隊清單」、53→`角色_提示詞清單`「提示詞清單」、4→`角色_登出`「登出」。1.0.30 起 33 不再對應權限鍵（改為管理者限定），原權限鍵「AI 用量分析」退役（`已退役權限鍵_AI用量分析`，由 `RbacBackfillService` 啟動時清除）。
- 選單過濾僅隱藏無權項目，並非授權邊界；實際資料存取由 API 端 `[HasPermission]` 與團隊可見範圍（`ProjectAccessService`，專案的主責＋協作團隊）把關（見〈權限與資料可見範圍總覽〉與「團隊清單 PRD」）。
- 管理員（`IsAdmin`）於 `CanAccessPage` 短路，選單全可見（含 `AdminOnlyMenuIds`）。
- 頁面擋下使用者時顯示「你沒有權限存取此頁面」，並呼叫 `PageAccessApi.RecordPageDeniedAsync(route)` 寫稽核。
- 右上角使用者選單與「關於」對話窗不做權限過濾：任何已登入者皆可開啟；內容僅為系統識別資訊，不含連線字串、金鑰或其他機敏設定。

## 六、錯誤與邊界

- 找不到／無法解析 `Menu.json`：記錄警告或錯誤並回傳空清單，選單顯示「尚無可用選單」，不致中斷頁面。
- 未登入者進入 `/meetings` 等受保護頁：伺服器直接導到 `/Auths/Login?ReturnUrl=…`（1.1.58 起），登入後回到原網址。已登入、但在頁面上登入失效：Session API 回 401，整頁導到 `/Auths/Login`。帳號停用、刪除或缺角色導到 `/Auths/Logout?reason=…`，需改密碼者導到 `/ChangePassword`（這些在 `WasmLayout` 處理）。
- WASM 載入很慢或下載失敗：載入畫面一直在，20 秒後出現提示與「重新整理」按鈕。
- 取得登入狀態失敗（非 401／403）：版面顯示「無法取得登入狀態：…」，不畫外框。
- 頁面元件發生未預期的例外：`WasmLayout` 的 `ErrorBoundary` 顯示錯誤橫幅，外框與選單照常可用。
- 使用者無任一項目權限：選單為空，僅顯示提示文字。

## 七、驗收與測試

- `MeetingRecord.Tests/MenuIconTests.cs::MenuJson_AllIcons_ShouldBeNonEmptyAndAllowed`：`Menu.json` 每項圖示非空且屬允許集合。
- `MeetingRecord.Tests/MonitoringMenuRegistrationTests.cs`（0.4.120）：選單 id 全樹唯一、系統管理下三個分組（1.0.30 起）、管理者限定頁不在權限目錄、非管理者即使殘留舊權限（含 1.0.30 退役的「AI 用量分析」）也看不到、分組名稱不會被當成權限鍵。
- `MeetingRecord.Tests/AiManagementMenuRegistrationTests.cs`（1.0.30）：「AI 管理」分組在系統管理下、依序為 AI 對話紀錄與 Token 用量、Token 用量已離開「監控與診斷」、兩頁只限管理者、分組列在 `GroupOnlyMenuIds`、權限目錄不含這兩頁與退役鍵。
- 手動驗收：以不同角色登入，確認選單僅顯示具權限之項目；管理員可見全部；重排 `Menu.json` 順序不影響權限對應；收合側邊欄後，經第二層 flyout 可點到每個第三層項目。
- 手動驗收（關於）：點右上角使用者名稱 →「關於」，對話窗顯示七列資訊，系統版本須與 `appsettings.json` 之 `SystemVersion` 一致；關閉後再次開啟，「已運作時間」應有增加。
- `MeetingRecord.Tests/WasmPageEntryTests.cs`（1.1.58）：未登入開 WASM 頁 302 到登入頁並帶 ReturnUrl、缺檔維持 404、登入頁沒有載入畫面但有背景預先載入、已登入的 WASM 頁有載入畫面與系統名稱、帶 ReturnUrl 登入後回原頁。端對端 LGN-09（未登入開內頁 → 登入 → 回原網址）、WASM-11（WASM 下載期間顯示載入畫面，畫好後消失、不擋操作）。
- 權限判定來源之測試見 `PermissionCheckerTests.cs`（規則見〈權限與資料可見範圍總覽〉）。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web.Client/Pages/HomePage.razor`（`/`）、`NotFoundPage.razor`（`/not-found`）
- `src/MeetingRecord/MeetingRecord.Web/Components/App.razor`、`App.razor.cs`（未登入導向、載入畫面）、`wwwroot/js/app-loading.js`、`wwwroot/app.css` 的 `.app-loading*`
- `src/MeetingRecord/MeetingRecord.Web.Client/Layout/WasmLayout.razor`（Session／選單 API、導向、`ErrorBoundary`、瀏覽器錯誤回報註冊、拿掉載入畫面）
- `src/MeetingRecord/MeetingRecord.Web.Client/Components/Layout/AppShell.razor`、`AppShell.razor.cs`（外框、頁面標題、使用者選單、變更密碼與「關於」對話窗）、`AboutItems.cs`
- `src/MeetingRecord/MeetingRecord.Web.Client/Components/Layout/NavMenu.razor`、`NavMenu.razor.css`、`SidebarMenuNode.razor`（收合 flyout 第三層）
- `src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap`、`PublicMenuIds`、`AdminOnlyMenuIds`、`GroupOnlyMenuIds`、`LoadAuthorizedMenuItemsAsync` 載入與過濾）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/SessionController.cs`（`api/v1/session`、`api/v1/session/menu`）、`SystemController.cs`（`api/v1/system/about`）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemAboutProvider.cs`、`SystemStartupState.cs`（「關於」資料與啟動時間來源）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/AuthenticationStateHelper.cs`（`CanAccessPage`／`CanDoAction` 規則）、`MeetingRecord.Web.Client/Auth/PermissionView.cs`（前端同規則）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`角色_*` 角色權限鍵常數）
- 交叉連結：[權限與資料可見範圍總覽](../security/權限與資料可見範圍總覽.md)、[認證授權與權限機制](../security/認證授權與權限機制.md)
