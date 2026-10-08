# 使用說明 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.78
- 最後核對日期：2026/10/08

## 一、目標與範圍

提供寫給**終端使用者**的操作說明，在系統內 `/help` 頁直接閱讀。內容以一條完整路徑貫穿（上傳 → 轉錄 → 產生會議紀錄 → 檢視編修匯出 → 歸屬專案 → 抽待辦，加上隨時可用的 AI 問答），並集中說明「哪些動作會花錢」、專案可見範圍、鍵盤慣例與常見問題。

- **一份原始檔，兩個出口**：內容只有一份 `docs/guides/系統使用說明.md`，建置時以 `EmbeddedResource` 嵌進 Web 組件，`/help` 頁再渲染出來。文件庫的那一份與系統裡的那一頁結構上不可能分岔；**改說明只改那個 .md，不要另寫一份**。
- 範圍：說明頁閱讀（唯讀）、隱藏維護者用的文件表頭、與 AI 問答／會議紀錄共用的 Markdown 渲染管線。
- 非範圍：
  - 欄位字典（功能一改就過期，刻意不寫進使用說明）。
  - 頁內搜尋、目錄側欄、依角色顯示不同內容、多語系。
  - 依頁面跳出的情境說明（tooltip 以外的 contextual help）。
  - 線上編輯說明內容——改內容一律改原始檔並重新建置。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/help`（`Components/Pages/Helps/HelpPage.razor`，掛 `MainLayout`，內容在 `HelpView`） |
| REST API | 無 |
| 選單路徑 | 核心功能區：使用說明（`Menu.json` id=12，icon `help_outline`，排在待辦事項之後） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[12] = MagicObjectHelper.角色_使用說明` |
| 權限鍵 | 頁面鍵「使用說明」（`MagicObjectHelper.角色_使用說明`），單一開關、沒有動作欄；管理員短路 |
| 主要使用者 | 所有角色。`RolePresets` 的「一般使用者」「檢視者」「主管」都含此鍵（管理者全部權限）；新帳號預設的一般使用者初始權限也取自這裡 |

## 三、畫面與欄位

- 整頁就是渲染後的說明正文，**不顯示文件表頭**（文件版本、文件狀態、現行系統版本、最後核對日期，以及「這個檔案同時是 `/help` 內容來源」的維護提醒）——畫面從第一個 `## 這套系統在做什麼` 開始。
- 內容尚未取得時顯示「使用說明載入中…」；嵌入資源讀不到時顯示「使用說明載入失敗，請聯絡系統管理員。」（見六）。
- 沒有頁面權限時只顯示紅色提示「你沒有權限存取此頁面」。
- 版面：刻意**不限寬**（全站其他頁都是滿版，一致性優先）；解除全域 `.markdown-content` 為對話框預覽設計的 `max-height: 60vh`／`overflow: auto`／`font-size: 0.85rem`，改成整頁自然捲動、字級 0.95rem，不出現第二層捲軸。
- 現行章節（以原始檔的 `##` 為準）：這套系統在做什麼（含「開始之前：什麼要先有什麼」依賴表）→ 完整流程：從錄音到待辦事項（1～4 必經、5 歸屬專案與 6 抽出待辦為選用、「隨時都可以：AI 問答」不編號）→ ⚠️ 哪些動作會花錢 → 專案項目怎麼用 → AI 問答 → 待辦事項 → 提示詞範本 → 鍵盤與操作慣例 → 資料會存在哪裡 → **常見問題 FAQ（固定在最後一節）**。
- FAQ 分組：轉錄與逐字稿、會議紀錄、待辦事項、AI 問答、權限與畫面、管理員才會遇到的；每則是 `**Q：…？**` 與 `A：…` 各自獨立一段。

## 四、內部系統運作

- 資料流：`HelpPage.razor` → `HelpView.OnInitializedAsync`：`AuthenticationStateHelper.Check`（登入）→ `CheckAccessPage(角色_使用說明)` → `ReadEmbeddedManual()` → `StripDocumentFrontMatter()` → `MarkdownRenderer.ToHtml()` 輸出到 `.markdown-content`。
- **內嵌資源**：`MeetingRecord.Web.csproj` 以 `<EmbeddedResource Include="..\..\..\docs\guides\系統使用說明.md" LogicalName="MeetingRecord.Web.系統使用說明.md" />` 嵌入；`HelpView.ResourceName` 必須與 `LogicalName` 一字不差，不一致時**建置照樣成功**，只有測試抓得到。
- **讀取時去 BOM**：`docs/*.md` 一律含 BOM，`StreamReader(..., detectEncodingFromByteOrderMarks: true)` 把它吃掉；否則 Markdig 會把第一行 `# 標題` 當成普通段落。
- **切表頭**：`HelpView.StripDocumentFrontMatter` 把**第一條 `---` 之前**全部丟掉；找不到分隔線時原樣回傳（寧可多一段表頭，也不要整頁空白）。方法是 `public static`，因為 Web 專案沒有 `InternalsVisibleTo`。
- **渲染管線**：`MarkdownRenderer`（Business 層，全站共用：AI 問答、會議紀錄草稿、本頁）明列 Markdig 擴充（pipe／grid table、強調、清單、腳註、自動連結等），**不用 `UseAdvancedExtensions()`**，並呼叫 `DisableHtml()`；連結只放行 `http`／`https`／`mailto` 與站內相對位址。

### 設計決策

| 議題 | 結論 |
|------|------|
| 為何只寫一份 | 「系統內與 `docs/` 兩者都要」的真正風險是分岔，不是工作量。兩份各寫各的，下次改功能一定只記得改一邊 |
| 為何用 `EmbeddedResource` | `docs/` 不在發佈輸出，不能像 `Menu.json` 那樣從 `ContentRootPath` 讀；也**不能**用 `Content` + `CopyToOutputDirectory`——開發時 `ContentRootPath` 是專案目錄而非 bin，會變成「發佈後看得到、開發時讀不到」。內嵌資源兩種情境行為一致 |
| 為何不設成免權限頁 | 0.4.78 當時沒有免權限選單機制（不在 `MenuPermissionMap` 的項目會退回用名稱當權限鍵），所以照既有慣例註冊權限鍵、作為單元素權限群組，以維持「UI 與 API 共用單一 RBAC 權威」。0.4.97 儀表板加入 `PublicMenuIds` 後，本頁**仍維持需要權限**，由預設角色帶入 |
| 內容取向 | 以完整動線為主、不做欄位字典；費用文案**講成本動因，不講「幾次 API 呼叫」**（講錯數字比不講更糟） |
| 依賴表放哪 | 必須放在 `## 這套系統在做什麼` **裡面**（`###`），不能在它之前另開一節——見五的紅線 2 |
| FAQ 位置與格式（0.4.90） | 放整份文件最後一節；問題與答案各自獨立一段。渲染器單一換行不產生 `<br>`，「粗體問題換行接答案」會黏成同一段，使用者認不出是 FAQ |

## 五、權限與安全

- 頁面權限：`CheckAccessPage(MagicObjectHelper.角色_使用說明)`，沒有權限顯示「你沒有權限存取此頁面」並寫 Warning log；側邊欄依 `MenuPermissionMap[12]` 隱藏。⚠️ 新建自訂角色時要記得勾「使用說明」，否則該角色看不到本頁。
- 內容是純靜態文件，不讀任何業務資料，不受專案／團隊資料權限影響。
- 渲染安全：`DisableHtml()` 讓原始 HTML 一律被逸出；連結 scheme 白名單擋掉 `javascript:`、`data:`、`vbscript:` 等。

### 修改 `docs/guides/系統使用說明.md` 的紅線

1. 檔案開頭必須維持 `# 系統使用說明` 標題：`Manual_RenderedHtml_ShouldStartWithHeading` 把**未切表頭的全文**渲染後要求以 `<h1` 開頭，藉此驗證 BOM 已被吃掉（畫面上這個標題會隨表頭一起被切掉，正文從 `## ` 開始）。
2. **第一條 `---` 之後的第一個非空行必須是 `## ` 開頭**。切表頭是「第一條 `---` 之前全丟」，所以**不可以在正文更前面新增 `---`**，也不可以在那條 `---` 後面先放一段文字或 `###`。
3. 必須保留五個關鍵字：重新轉錄、AI 轉會議紀錄、抽出待辦、AI 問答、費用；渲染後至少要有一個 `<table>`。
4. 渲染管線不支援：原始 HTML（`<br>`、`<details>`、`<span style>` 會原樣印出）、單一換行斷行（要空一行另起段落）、emoji 短碼（直接貼 Unicode 字元）；新增 `---` 前務必空一行，否則會被當成上一段的 setext h2。
5. 檔案須維持 UTF-8 含 BOM（`scripts/Test-DocsEncoding.ps1`）；改完要**重新建置**，畫面才會更新。

## 六、錯誤與邊界

- **嵌入資源找不到**（`LogicalName` 與 `ResourceName` 不一致、或檔案沒被嵌入）：`ReadEmbeddedManual` 寫 Error log（列出目前所有內嵌資源名稱），畫面顯示「使用說明載入失敗，請聯絡系統管理員。」。
  ⚠️ csproj 註解、`HelpView.ResourceName` 註解與 `HelpManualTests` 類別註解都寫「會永遠停在『載入中…』」，與現行程式不符——現行是顯示載入失敗訊息。
- 原始檔沒有 `---`：整份原樣渲染（含表頭），不會空白。
- 原始檔內容空白：`StripDocumentFrontMatter` 原樣回傳，畫面停在「使用說明載入中…」。
- 未登入或身分無效：`AuthenticationStateHelper.Check` 導向 `/Auths/Logout`（再回登入頁），不讀資源。

## 七、驗收與測試

`src/MeetingRecord/MeetingRecord.Tests/HelpManualTests.cs`（7 筆）：

- `Manual_ShouldBeEmbeddedInWebAssembly`：資源名稱 `MeetingRecord.Web.系統使用說明.md` 存在於 Web 組件。
- `Manual_ShouldNotStartWithByteOrderMark`：讀出來的字串沒有 BOM。
- `Manual_RenderedHtml_ShouldStartWithHeading`：未切表頭的全文渲染後以 `<h1` 開頭（BOM 沒吃掉會變成 `<p>`）。
- `Manual_ShouldCoverThePaidActions`：五個付費相關關鍵字都在。
- `StripFrontMatter_ShouldRemoveMaintainerHeader`：畫面內容不含「文件版本」「最後核對日期」，且以 `## ` 開頭（紅線 2）。
- `StripFrontMatter_WithoutSeparator_ShouldReturnAsIs`：沒有分隔線時原樣回傳。
- `Manual_RenderedHtml_ShouldContainTables`：全文渲染後含 `<table>`（順帶守住全站共用管線的表格擴充）。

其他：`MenuIconTests.AllowedIcons` 含 `help_outline`（缺了會擋住）。預設角色含「使用說明」目前只由 `RolePresets` 宣告保證，`RolePresetsTests` 沒有針對這個鍵斷言。人工驗收：實跑 `/help` 確認沒有表頭外洩、沒有原始 Markdown 符號、沒有被逸出的 HTML、沒有內嵌捲軸與水平捲軸、FAQ 的 Q 與 A 不黏在同一段；[上架前人工測試清單](../guides/上架前人工測試清單.md) 12-1（一般使用者選單含使用說明）。

## 八、相關程式與文件

### 版本沿革

- 0.4.78：新增 `/help`，內容來源 `docs/guides/系統使用說明.md`（嵌入資源），9 個章節，`HelpManualTests` 7 筆。
- 0.4.89：補「開始之前：什麼要先有什麼」依賴表、最短路徑擴成 6 步完整流程、「常見狀況」整併為分組 FAQ。
- 0.4.90：FAQ 移到最後一節，改為 Q／A 各自獨立段落。
- 之後各版功能異動直接改原始檔內容（例如 0.4.102 的主責＋協作團隊、分類、角色分工）。

### 程式

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Helps/HelpPage.razor`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Helps/HelpView.razor`、`HelpView.razor.cs`（`ResourceName`、`OnInitializedAsync`、`StripDocumentFrontMatter`、`ReadEmbeddedManual`）、`HelpView.razor.css`
- `src/MeetingRecord/MeetingRecord.Web/MeetingRecord.Web.csproj`（`EmbeddedResource` 與 `LogicalName`）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/MarkdownRenderer.cs`（`ToHtml`、`AllowedSchemes`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`角色_使用說明`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 12）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs`（`GetRoleListPermissionAllName` 單元素群組）、`src/MeetingRecord/MeetingRecord.Business/Helpers/RolePresets.cs`
- `src/MeetingRecord/MeetingRecord.Tests/HelpManualTests.cs`

### 文件

- 內容來源：[系統使用說明](../guides/系統使用說明.md)
- Changelog：[系統使用說明](../changelog/2026-09-16-系統使用說明.md)、[使用說明補上 FAQ 與流程順序](../changelog/2026-09-22-使用說明補上FAQ與流程順序.md)
- 交叉連結：[首頁與導覽 PRD](首頁與導覽-prd.md)、[角色管理 PRD](角色管理-prd.md)、[AI 問答 PRD](AI問答-prd.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)

> 返回 [PRD 主控台](README.md)
