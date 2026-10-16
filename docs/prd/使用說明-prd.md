# 使用說明 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：1.0.43
- 首次實作版本：0.4.78
- 最後核對日期：2026/10/16

## 一、目標與範圍

提供寫給**終端使用者**的操作說明，在系統內 `/help` 頁直接閱讀。內容以一條完整路徑貫穿（上傳 → 轉錄 → 產生會議紀錄 → 檢視編修匯出 → 歸屬專案 → 抽待辦，加上隨時可用的 AI 問答），並集中說明「哪些動作會花錢」、專案可見範圍、鍵盤慣例與常見問題。

- **一份原始檔，兩個出口**：內容只有一份 `docs/guides/系統使用說明.md`，建置時以 `EmbeddedResource` 嵌進 Web 組件，`/help` 頁再渲染出來。文件庫的那一份與系統裡的那一頁結構上不可能分岔；**改說明只改那個 .md，不要另寫一份**。
- 範圍：說明頁閱讀（唯讀）、隱藏維護者用的文件表頭、分類瀏覽與頁內搜尋（1.0.42）、頁首「問 AI 助理」（1.0.43，AI 只根據使用說明回答操作問題）、與 AI 問答／會議紀錄共用的 Markdown 渲染管線。
- 非範圍：
  - 欄位字典（功能一改就過期，刻意不寫進使用說明）。
  - 依角色顯示不同的**畫面**內容、多語系、全文索引或模糊比對（搜尋只做子字串比對）。唯一依角色不同的是 AI 助理**送給 AI 的內容**（非管理者不含「管理員」分類，見四）。
  - AI 助理的對話保存、分享、匯出與逐題費用確認（對話只活在這一頁；費用以常駐灰字提醒，比照 AI 問答）。
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

- **不顯示文件表頭**（文件版本、文件狀態、現行系統版本、最後核對日期，以及「這個檔案同時是 `/help` 內容來源」的維護提醒）。
- **分類瀏覽（1.0.42）**：不再整份一頁到底。左欄固定 6 個分類按鈕（捲動時黏住），順序固定：新手入門｜會議與逐字稿｜專案與待辦｜AI 與費用｜管理員｜常見問題；右欄只顯示選中分類的章節，預設第一類「新手入門」。
- **搜尋（1.0.42）**：頂端搜尋框（佔位文字提示「按 / 快速搜尋」，輸入後 200ms 才查），搜尋中右側顯示「共 n 筆」、左欄分類取消選取，右欄改列結果卡片：麵包屑（分類 › 章 › 小節）＋該段渲染後內容，關鍵字以 `<mark>` 高亮。沒有結果時顯示「找不到「…」。試試其他關鍵字，或看左邊的「常見問題」。」；清空搜尋框回到分類瀏覽。
- **快速鍵（1.0.42）**：不在輸入框裡時按 `/` 聚焦搜尋框；在搜尋框按 `Esc` 清空。
- **切換分類不跳動（1.0.43）**：點左欄分類後，只有說明內容區頂端已捲出視窗上方時才（瞬間）捲回頂端；頂端還在畫面上就不捲。1.0.42 一律 `scrollIntoView`，頂端明明看得到也被對齊到視窗最上緣，看起來是「往上跳一下」。
- **問 AI 助理（1.0.43，位置依使用者選擇放在整頁最上方）**：一張卡片，標題「問 AI 助理」。
  - 輸入框（佔位文字「輸入操作上的問題，例如：怎麼把會議歸屬到專案？（Enter 送出、Shift+Enter 換行）」）：Enter 送出、Shift+Enter 換行，輸入法選字中的 Enter 不送出（`FormKeyboardHelper`；`DebounceMilliseconds=0`，同 AI 問答）；右側「發問」，生成中變「停止」（取消後仍照樣記帳，帳本記 Cancelled）。
  - 對話串：問題泡泡＋回答；回答串流中以純文字顯示（尚未收到字時顯示「正在讀使用說明…」），完成後以 Markdown 渲染；停止的那一輪保留已收到的部分並加「（已停止）」。有對話且不在生成中時標題列出現「清除對話」。對話串在卡片內捲動（最高 45vh）。
  - 對話**不保存**：只存在這一頁，重新整理就清空；離開頁面會取消進行中的回答。追問時只帶已成功回答的輪次當上下文。
  - 卡片底部常駐灰字「每次提問會把使用說明送給 AI 並產生少量費用；回答僅供參考，以使用說明為準。」，**不做逐題費用確認**（使用者選擇，比照 AI 問答）。
  - 未設定 AI 供應商：輸入框與發問停用，佔位文字改為不能用的原因。已達用量上限或呼叫失敗：錯誤訊息顯示在該輪回答的位置。
- **站內錨點（1.0.42）**：說明內的 `[…](#錨點)` 連結點下去會先切到錨點所在分類、清空搜尋，再捲到該標題（目標可能不在目前分類）。
- 內容尚未取得時顯示「使用說明載入中…」；嵌入資源讀不到時顯示「使用說明載入失敗，請聯絡系統管理員。」（見六）。
- 沒有頁面權限時只顯示紅色提示「你沒有權限存取此頁面」。
- 版面：刻意**不限寬**（全站其他頁都是滿版，一致性優先）；解除全域 `.markdown-content` 為對話框預覽設計的 `max-height: 60vh`／`overflow: auto`／`font-size: 0.85rem`，改成整頁自然捲動、字級 0.95rem，不出現第二層捲軸。窄螢幕（≤768px）分類改成頂端一排橫向膠囊按鈕（1.0.42）。
- 分類對應（以原始檔每個 `##` 前一行的 `<!-- 說明分類：XXX -->` 為準，1.0.42）：

  | 分類 | 章節 |
  |------|------|
  | 新手入門 | 這套系統在做什麼、鍵盤與操作慣例、資料會存在哪裡 |
  | 會議與逐字稿 | 完整流程：從錄音到待辦事項、提示詞範本 |
  | 專案與待辦 | 專案項目怎麼用、待辦事項 |
  | AI 與費用 | ⚠️ 哪些動作會花錢、AI 問答 |
  | 管理員 | 監控與診斷、AI 管理、稽核紀錄 |
  | 常見問題 | 常見問題 FAQ |
- 現行章節（以原始檔的 `##` 為準）：這套系統在做什麼（含「開始之前：什麼要先有什麼」依賴表）→ 完整流程：從錄音到待辦事項（1～4 必經、5 歸屬專案與 6 抽出待辦為選用、「隨時都可以：AI 問答」不編號）→ ⚠️ 哪些動作會花錢 → 專案項目怎麼用 → AI 問答 → 待辦事項 → 提示詞範本 → 鍵盤與操作慣例 → 資料會存在哪裡 → 監控與診斷（管理員）→ AI 管理（管理員）→ 稽核紀錄（管理員）→ **常見問題 FAQ（固定在最後一節）**。原始檔順序不必與分類順序一致，畫面依分類歸組、類內照原始檔順序。
- FAQ 分組：轉錄與逐字稿、會議紀錄、待辦事項、AI 問答、權限與畫面、管理員才會遇到的；每則是 `**Q：…？**` 與 `A：…` 各自獨立一段。搜尋時一則問題就是一張卡片、標題是問題本身（1.0.42）。

## 四、內部系統運作

- 資料流：`HelpPage.razor` → `HelpView.OnInitializedAsync`：`AuthenticationStateHelper.Check`（登入）→ `CheckAccessPage(角色_使用說明)` → `ReadEmbeddedManual()` → `StripDocumentFrontMatter()` → `HelpManual.Parse()`（1.0.42）→ 選中分類的預先渲染 HTML 或搜尋結果輸出到 `.markdown-content`。
- **`HelpManual`（1.0.42，`Components/Views/Helps/HelpManual.cs`，純邏輯、可單元測試）**：
  - `Parse`：依 `##` 切章（```` ``` ```` 圍欄內的行不算標題），讀每章前一行的 `<!-- 說明分類：XXX -->` 歸組，**標記行在渲染前移除**（`MarkdownRenderer` 有 `DisableHtml()`，留著會原樣印出）；章與章之間的 `---` 丟掉；每個分類預先渲染一次 HTML。
  - 標記漏了或名稱不在 6 類內：該章歸到「新手入門」，記在 `UnknownMarkers`，`HelpView` 寫 Warning log；測試會擋。
  - 搜尋單位 `HelpTopic`：每章的引言（第一個 `###` 之前）、每個 `###` 小節、每則 `**Q：…**` 問答各一筆；`Breadcrumb` 為「分類 › 章 › 小節」（小節與章同名時不重複）。
  - `Search(query)`：以空白切成多個關鍵字、不分大小寫、**全部都要出現**（標題＋內文，AND）；標題命中者排前，其餘照文件順序；空白查詢回空清單。
  - `FindCategoryOfAnchor(id)`：找含該標題 `id` 的分類，供站內錨點跳轉。
  - `BuildAssistantContext(includeAdmin)`（1.0.43）：依分類順序串接（已去標記，每類前加 `# 分類：XXX`），給 AI 助理當參考資料。`includeAdmin=false` 時**排除「管理員」分類**（使用者選擇：一般使用者用不到、送出量約少四分之一，也避免 AI 介紹他看不到的功能）；管理者全送。注意 FAQ 的「管理員才會遇到的」小節在「常見問題」分類內，仍會送出。
- **瀏覽器端（1.0.42）**：`wwwroot/js/help-view.js`（`App.razor` 載入）＋ `Services/HelpViewInterop.cs`（Scoped 註冊）：`/` 聚焦、`Esc` 清空（呼叫 .NET `ClearSearchFromJs`）；高亮只用 TreeWalker 在文字節點包 `<mark>`，不改 HTML 字串或屬性；攔截說明內容裡的 `a[href^="#"]`，呼叫 .NET `JumpToAnchorFromJs` 切分類、清搜尋後 `scrollIntoView`。interop 失敗只記 log 不拋出（與 `TodoAutoFitInterop` 同理由）。`scrollTo(root, id)`：有錨點時捲到該標題；切分類（無錨點）時只在 `.help-view-body` 的 `getBoundingClientRect().top < 0` 才 `scrollIntoView({behavior:'auto'})`（1.0.43）。
- **AI 助理（1.0.43，`MeetingRecord.Business/Services/HelpAssistant/`）**：
  - `HelpAssistantPromptBuilder`（純邏輯）：`SystemPrompt` 規定只根據 `<使用說明>` 回答、沒寫到就答「使用說明沒有寫到這件事」並建議聯絡管理員（不要猜）、繁體中文（台灣用語）、步驟用條列、按鈕與頁面名稱用「」、最後另起一行「參考：章節 › 小節」、只回答系統操作問題。`BuildUserPrompt`：說明全文包在 `<使用說明>` 標籤內＋最近 `MaxHistoryTurns=3` 輪（每則截到 2,000 字）＋本次問題（截到 1,000 字，空白丟 `ArgumentException`）。`BuildRelatedInfo`：「使用說明提問：＜問題前 40 字＞」。
  - `HelpAssistantService`（Scoped，形狀比照 `DiagnosticAiService`）：供應商取 `LlmSettings.DefaultProvider`，`IsAvailable`／`UnavailableReason`；一律經 `AiCallGateway`，所以用量上限、Token 用量帳本與 AI 對話紀錄都自動處理；功能別 `AiUsageFeature.HelpAssistant = 9`（「使用說明 AI 助理」，token 計費）；`ThreadKey` 為 `help:{每次開頁一個 guid}`，同一頁的連續提問在 AI 對話紀錄串成一串。
  - 稽核：每次提問寫 `Help.AiAsk`（「使用說明 AI 提問」），detail 記問題字數、`includeAdmin`、模型、結果（成功／停止／失敗：例外型別）；**不記問題全文**（全文在 AI 對話紀錄）。
  - 費用：每問一次都送約整份說明（原始檔約 56KB、兩萬多 token），依模型單價約每次新台幣幾毛到一、兩元。
- **標題 id 為 GitHub 風格（1.0.42）**：`MarkdownRenderer` 改用 `UseAutoIdentifiers(AutoIdentifierOptions.GitHub)`。原本預設的 `AllowOnlyAscii` 會把中文標題 id 刪到只剩 ASCII（例：「出現「已達 AI 用量上限」時」→ `ai`），說明裡的 `#錨點` 連結從來沒對上過。只影響標題的 `id` 屬性，AI 問答、會議紀錄草稿畫面無可見差異。
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
| AI 助理只讀說明（1.0.43） | 說明沒寫的功能 AI 很會自己編，編出來的按鈕與步驟照著找會找不到，比沒回答更糟；所以 system prompt 要求沒寫到就明說並建議聯絡管理員 |
| AI 助理送整份說明而非檢索片段（1.0.43） | 說明只有約兩萬多 token，整份送最簡單、也不會漏掉跨章節的答案；代價是每題都付整份的輸入 token |
| 非管理者不送「管理員」分類（1.0.43） | 使用者選擇：用不到、省約四分之一、AI 不會介紹他看不到的功能 |
| 不保存對話、不逐題確認費用（1.0.43） | 使用者選擇；用量仍受每月上限管制，且 Token 用量／AI 對話紀錄都查得到 |

## 五、權限與安全

- 頁面權限：`CheckAccessPage(MagicObjectHelper.角色_使用說明)`，沒有權限顯示「你沒有權限存取此頁面」並寫 Warning log；側邊欄依 `MenuPermissionMap[12]` 隱藏。⚠️ 新建自訂角色時要記得勾「使用說明」，否則該角色看不到本頁。
- 內容是純靜態文件，不讀任何業務資料，不受專案／團隊資料權限影響。AI 助理（1.0.43）同樣只送使用說明與本頁對話，不送任何業務資料；有頁面權限就能用，沒有另外的權限鍵；非管理者送出的說明不含「管理員」分類。
- 渲染安全：`DisableHtml()` 讓原始 HTML 一律被逸出；連結 scheme 白名單擋掉 `javascript:`、`data:`、`vbscript:` 等。

### 修改 `docs/guides/系統使用說明.md` 的紅線

1. 檔案開頭必須維持 `# 系統使用說明` 標題：`Manual_RenderedHtml_ShouldStartWithHeading` 把**未切表頭的全文**渲染後要求以 `<h1` 開頭，藉此驗證 BOM 已被吃掉（畫面上這個標題會隨表頭一起被切掉，正文從第一個分類標記與 `## ` 開始）。
2. **第一條 `---` 之後的第一個非空行必須是分類標記，下一行是 `## ` 開頭**（1.0.42 起）。切表頭是「第一條 `---` 之前全丟」，所以**不可以在正文更前面新增 `---`**，也不可以在那條 `---` 後面先放一段文字或 `###`。
3. 必須保留五個關鍵字：重新轉錄、AI 轉會議紀錄、抽出待辦、AI 問答、費用；渲染後至少要有一個 `<table>`。
4. 渲染管線不支援：原始 HTML（`<br>`、`<details>`、`<span style>` 會原樣印出）、單一換行斷行（要空一行另起段落）、emoji 短碼（直接貼 Unicode 字元）；新增 `---` 前務必空一行，否則會被當成上一段的 setext h2。
5. 檔案須維持 UTF-8 含 BOM（`scripts/Test-DocsEncoding.ps1`）；改完要**重新建置**，畫面才會更新。
6. **每個 `##` 前一行都要有 `<!-- 說明分類：XXX -->`**（1.0.42），XXX 只能是 6 類之一（新手入門、會議與逐字稿、專案與待辦、AI 與費用、管理員、常見問題）；這是唯一允許的 HTML，會在渲染前剝掉。FAQ 新題目一律寫成獨立一段的 `**Q：…**`，搜尋才會切成單獨卡片。
7. 站內連結 `[…](#錨點)` 用 **GitHub 風格 slug**（1.0.42 起渲染器與 GitHub 一致），目標標題必須存在，否則測試擋下。

## 六、錯誤與邊界

- **嵌入資源找不到**（`LogicalName` 與 `ResourceName` 不一致、或檔案沒被嵌入）：`ReadEmbeddedManual` 寫 Error log（列出目前所有內嵌資源名稱），畫面顯示「使用說明載入失敗，請聯絡系統管理員。」。
  ⚠️ csproj 註解、`HelpView.ResourceName` 註解與 `HelpManualTests` 類別註解都寫「會永遠停在『載入中…』」，與現行程式不符——現行是顯示載入失敗訊息。
- 原始檔沒有 `---`：整份原樣渲染（含表頭），不會空白。
- 章節缺分類標記或名稱打錯（1.0.42）：不壞畫面，該章歸到「新手入門」並寫 Warning log（列出章名）；`HelpManualSearchTests` 會擋。
- 錨點找不到所在分類或 JS interop 失敗（1.0.42）：只記 log，不切換也不拋錯。
- AI 助理（1.0.43）：未設定供應商時輸入停用並顯示原因；已達用量上限不呼叫模型，錯誤顯示在對話串（Warning log）；其他失敗顯示錯誤（Error log）；按「停止」或離開頁面會取消，已送出的部分照樣記帳（Cancelled）；失敗或停止的那一輪不當作後續追問的上下文。
- 原始檔內容空白：`StripDocumentFrontMatter` 原樣回傳，畫面停在「使用說明載入中…」。
- 未登入或身分無效：`AuthenticationStateHelper.Check` 導向 `/Auths/Logout`（再回登入頁），不讀資源。

## 七、驗收與測試

`src/MeetingRecord/MeetingRecord.Tests/HelpManualTests.cs`（7 筆）：

- `Manual_ShouldBeEmbeddedInWebAssembly`：資源名稱 `MeetingRecord.Web.系統使用說明.md` 存在於 Web 組件。
- `Manual_ShouldNotStartWithByteOrderMark`：讀出來的字串沒有 BOM。
- `Manual_RenderedHtml_ShouldStartWithHeading`：未切表頭的全文渲染後以 `<h1` 開頭（BOM 沒吃掉會變成 `<p>`）。
- `Manual_ShouldCoverThePaidActions`：五個付費相關關鍵字都在。
- `StripFrontMatter_ShouldRemoveMaintainerHeader`：畫面內容不含「文件版本」「最後核對日期」，且以分類標記開頭、下一行是 `## `（紅線 2，1.0.42 調整）。
- `StripFrontMatter_WithoutSeparator_ShouldReturnAsIs`：沒有分隔線時原樣回傳。
- `Manual_RenderedHtml_ShouldContainTables`：全文渲染後含 `<table>`（順帶守住全站共用管線的表格擴充）。

`src/MeetingRecord/MeetingRecord.Tests/HelpManualSearchTests.cs`（1.0.42，14 個方法／16 筆案例）：

- 真實說明檔：6 個分類依序且都不空；每個 `##` 都有合法標記；渲染後 HTML 不含標記；每個 `](#id)` 連結都能以 `FindCategoryOfAnchor` 找到分類；搜尋「轉錄失敗」找得到 FAQ 題目；`BuildAssistantContext_NonAdmin_ShouldExcludeAdminCategory`（1.0.43）。
- 範例文字：圍欄內的 `##` 不切章、章尾 `---` 去掉、FAQ 題目各自成卡、多關鍵字 AND 且不分大小寫、標題命中排前、空白查詢回空、麵包屑不重複章名、未知標記歸「新手入門」並回報。

`src/MeetingRecord/MeetingRecord.Tests/HelpAssistantTests.cs`（1.0.43，10 個方法／11 筆案例）：prompt 把說明包在 `<使用說明>`、只帶最近 3 輪、過長截斷、空白問題丟例外、system prompt 含必要規則；服務串流回傳並以功能別 `HelpAssistant` 記帳、帶 `ThreadKey`／`RelatedInfo`；達上限不呼叫模型；取消記成 Cancelled；未設定時回傳原因；功能別顯示文字與 token 計費。

其他：`MenuIconTests.AllowedIcons` 含 `help_outline`（缺了會擋住）。預設角色含「使用說明」目前只由 `RolePresets` 宣告保證，`RolePresetsTests` 沒有針對這個鍵斷言。人工驗收：實跑 `/help` 確認沒有表頭外洩、沒有原始 Markdown 符號、沒有被逸出的 HTML、沒有內嵌捲軸與水平捲軸、FAQ 的 Q 與 A 不黏在同一段；[上架前人工測試清單](../guides/上架前人工測試清單.md) 12-1（一般使用者選單含使用說明）與使用說明頁的分類、搜尋、錨點項目（1.0.42）、切分類不跳動與 AI 助理項目（1.0.43）。

## 八、相關程式與文件

### 版本沿革

- 0.4.78：新增 `/help`，內容來源 `docs/guides/系統使用說明.md`（嵌入資源），9 個章節，`HelpManualTests` 7 筆。
- 0.4.89：補「開始之前：什麼要先有什麼」依賴表、最短路徑擴成 6 步完整流程、「常見狀況」整併為分組 FAQ。
- 0.4.90：FAQ 移到最後一節，改為 Q／A 各自獨立段落。
- 之後各版功能異動直接改原始檔內容（例如 0.4.102 的主責＋協作團隊、分類、角色分工）。
- 1.0.42：使用者反映整份一頁太長、找不到答案 → 改為 6 分類瀏覽＋頁內搜尋（關鍵字高亮、`/` 與 `Esc` 快速鍵、跨分類錨點跳轉）；`MarkdownRenderer` 標題 id 改 GitHub 風格；新增 `HelpManualSearchTests`。
- 1.0.43：修正切換分類時頁面往上跳（只在內容頂端已捲出畫面時才捲回）；頁首新增「問 AI 助理」（`HelpAssistantService`／`HelpAssistantPromptBuilder`、`HelpManual.BuildAssistantContext`、功能別 `HelpAssistant = 9`、稽核 `Help.AiAsk`）；新增 `HelpAssistantTests`。

### 程式

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Helps/HelpPage.razor`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Helps/HelpView.razor`、`HelpView.razor.cs`（`ResourceName`、`OnInitializedAsync`、`StripDocumentFrontMatter`、`ReadEmbeddedManual`、`JumpToAnchorFromJs`、`ClearSearchFromJs`）、`HelpView.razor.css`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Helps/HelpManual.cs`（`Parse`、`Search`、`FindCategoryOfAnchor`，1.0.42；`BuildAssistantContext`，1.0.43）
- `src/MeetingRecord/MeetingRecord.Business/Services/HelpAssistant/HelpAssistantPromptBuilder.cs`、`HelpAssistantService.cs`（1.0.43，Scoped 註冊於 `ServiceCollectionExtensions`）
- `src/MeetingRecord/MeetingRecord.Share/Enums/AiUsageFeature.cs`（`HelpAssistant = 9`）、`src/MeetingRecord/MeetingRecord.Share/Helpers/AuditActions.cs`（`HelpAiAsk`）
- `src/MeetingRecord/MeetingRecord.Web/wwwroot/js/help-view.js`、`src/MeetingRecord/MeetingRecord.Web/Services/HelpViewInterop.cs`（1.0.42）
- `src/MeetingRecord/MeetingRecord.Web/MeetingRecord.Web.csproj`（`EmbeddedResource` 與 `LogicalName`）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/MarkdownRenderer.cs`（`ToHtml`、`AllowedSchemes`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`角色_使用說明`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 12）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs`（`GetRoleListPermissionAllName` 單元素群組）、`src/MeetingRecord/MeetingRecord.Business/Helpers/RolePresets.cs`
- `src/MeetingRecord/MeetingRecord.Tests/HelpManualTests.cs`、`HelpManualSearchTests.cs`、`HelpAssistantTests.cs`

### 文件

- 內容來源：[系統使用說明](../guides/系統使用說明.md)
- Changelog：[系統使用說明](../changelog/2026-09-16-系統使用說明.md)、[使用說明補上 FAQ 與流程順序](../changelog/2026-09-22-使用說明補上FAQ與流程順序.md)、[使用說明分類與搜尋](../changelog/2026-10-16-使用說明分類與搜尋.md)
- 交叉連結：[首頁與導覽 PRD](首頁與導覽-prd.md)、[角色管理 PRD](角色管理-prd.md)、[AI 問答 PRD](AI問答-prd.md)、[Token 用量 PRD](Token用量-prd.md)、[AI 對話紀錄 PRD](AI對話紀錄-prd.md)、[稽核紀錄 PRD](稽核紀錄-prd.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)

> 返回 [PRD 主控台](README.md)
