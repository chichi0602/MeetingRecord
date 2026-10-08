# AI 問答 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.51
- 最後核對日期：2026/10/08

## 一、目標與範圍

讓使用者就「一個專案」或「一場會議」的既有資料向 AI 提問，不必自己翻會議紀錄、逐字稿與附件。回答只根據系統提供的參考資料，找不到就直說，不臆測。

- 範圍：
  - **兩種對象**（`AiChatScope`）：專案層級讀本專案**所有會議紀錄＋專案附件**；單一會議讀該會議的**會議紀錄＋逐字稿**。
  - **多段對話**：一個對象底下可以有多段對話，左側清單可切換、開新對話、改名、刪除。對話是**同一個對象底下所有人共用**的，不是每人一份。
  - 回答**邊生成邊顯示**（串流），完成後以 Markdown 渲染。
  - 每則訊息可**複製**、**編輯**（只更正文字，不呼叫模型）；使用者提問另可**重新產生答案**（會呼叫模型、覆蓋原答案）。
  - 整段對話與單則訊息都可**下載 PDF**。
  - 提問可**附加圖片與文件**（迴紋針、Ctrl+V 貼上、拖放）。
  - 對話存在**檔案系統**（`SystemSettings.ExternalFileSystem.AiChatPath`），不進資料庫。
  - 相鄰入口「**AI 抽出待辦**」：從已歸屬專案的會議紀錄抽出待辦候選，經人確認後才寫入（見第三節）。
- 非範圍：
  - 單則訊息刪除（只能刪整段對話，使用者明確要求）。
  - 個人私有對話、跨專案提問、RAG／向量索引（系統不含 RAG）。
  - 用量配額與成本上限（只記帳不設限，帳本見 [AI 用量分析 PRD](AI用量分析-prd.md)）。
  - 附件文字擷取結果的快取（每次提問都重新擷取，見第四節）。
  - 掃描檔 PDF 的 OCR（讀不到文字的會被明確列為「無法讀取」）。

## 二、使用者與入口

AI 問答**沒有自己的選單項目與路由**，是掛在兩個頁面上的共用視窗元件 `AiChatModal`（`Components/Views/Projects/`）。

| 入口 | 位置 | 對象 | 出現條件 |
|------|------|------|----------|
| 專案層級 | `/projects`（專案項目，`Menu.json` id=21）專案選擇列工具列的「AI 問答」 | `AiChatScope.Project`，讀本專案所有會議紀錄＋附件 | 有選中專案 |
| 單一會議（專案頁） | `/projects` 的「歷史會議紀錄」每列操作欄 `forum` 圖示 | `AiChatScope.Meeting` | 每列都有 |
| 單一會議（會議頁，0.4.73） | `/meetings`（會議紀錄，`Menu.json` id=61）每列操作欄 `forum` 圖示 | `AiChatScope.Meeting` | `HasDraft \|\| HasTranscript`（兩者都沒有就只會得到空脈絡） |
| AI 抽出待辦 | `/projects` 的「歷史會議紀錄」每列操作欄 `playlist_add` 圖示 | 該場會議的會議紀錄草稿 | `HasDraft` 且有 `待辦事項:create` |

權限鍵（不新增獨立的權限鍵，一律沿用宿主頁）：

| 動作 | 專案頁（`/projects`） | 會議頁（`/meetings`） |
|------|------|------|
| 開視窗、提問、開新對話、附加檔案、預覽／下載附件、複製 | 能進頁面即可（`專案項目` 頁面權限） | 能進頁面即可（`會議紀錄` 頁面權限） |
| 重新命名對話、編輯訊息、重新產生答案（0.4.109） | `專案項目:edit` | `會議紀錄:edit` |
| 刪除對話、清空這段對話（0.4.109） | `專案項目:delete` | `會議紀錄:delete` |
| 下載整段／單則 PDF（0.4.70） | `專案項目:export` | `會議紀錄:export` |
| AI 抽出待辦（0.4.61） | `待辦事項:create`（這顆鈕真正做的事是新增待辦） | —（0.4.83 起不在此頁） |

管理員短路。權限由宿主頁以 `AuthenticationStateHelper.CheckAccessAction(...)` 算好後傳入 `AiChatModal` 的 `CanEdit`／`CanDelete`／`CanExport` 參數。

主要使用者：能看到該專案／會議的登入者。資料可見範圍見第五節。

## 三、畫面與欄位

### 問答視窗（`AiChatModal`）

- **版面**：滿版（0.4.94），掛 `.meeting-view-modal ai-chat-modal`，96vw 寬、固定 `height: 96vh`——全站唯一固定高度的對話框，聊天視窗吃滿螢幕才正常。行寬顧慮由訊息氣泡處理：`.ai-chat-row` 最大寬度 `min(82%, 960px)`。手機寬度時對話清單疊到上方。
- **左欄：對話紀錄清單**（0.4.79）：每列顯示標題、最後更新時間、開啟者；目前這段高亮。
  - 「開新對話」：**目前這段還沒問過問題時停用**，Tooltip 說明「目前已經是一段空白的新對話」——避免在共用清單上堆出一排「新對話」。
  - 改名（需修改權限）：Enter 送出、Esc 取消，最長 60 字。
  - 刪除（需刪除權限）：紅色二次確認，訊息會指名對話標題與則數，提醒「所有人都會看不到，且無法復原」。
  - 開啟時載入**最近更新的那一段**；完全沒有對話時自動建立一段空白對話，不逼使用者先按「開新對話」。
- **來源說明條**：每次回答後顯示「本次讀取了 N 份資料」，並列出「內容過長已截斷」與「無法讀取（掃描檔、不支援的格式或尚無內容）」的來源——**一定要講出來**，否則使用者會以為 AI 看過了全部內容。
- **訊息區**：
  - 提問以純文字（`pre-wrap`）顯示；AI 回答以 `MarkdownRenderer.ToHtml` 渲染（0.4.70）。串流中維持純文字，答完重載歷史才換成渲染版。
  - 每則訊息工具列：複製（全員）、編輯（修改權限）、下載這則 PDF（匯出權限）。工具列**不是 hover 才出現**，平時 `opacity: .35`，`:hover`／`:focus-within` 時完全顯現（觸控與鍵盤才點得到）。
  - 編輯提問時有「儲存」與「重新產生答案」兩顆，下方一行字說明差別；編輯回答只有「儲存」。
  - 提問下方列出附件：圖片點一下展開預覽、再點收起；文件點一下下載。
- **輸入區**：
  - 常駐費用提示：「每次送出都會呼叫 Azure OpenAI 並產生費用（一次提問一次呼叫）。附上的圖片與檔案也會計費，檔案越大越貴。」——提問**刻意不跳二次確認**。
  - Enter 送出、Shift+Enter 換行（`TextArea` `BindOnInput="true"`、`DebounceMilliseconds="0"`）。
  - 迴紋針（`InputFile`，可多選）＋ 待送附件列（圖片有縮圖、可逐一 ✕ 移除）。
- **底部動作**（有訊息時）：「下載整段對話 PDF」（匯出權限）、「清空這段對話」（刪除權限，走與清單刪除相同的二次確認）。
- **處理中不能關視窗**（0.4.115）：顯示「AI 正在處理中，請等它完成再關閉視窗。」關閉時清掉待送附件與編輯狀態。

### 附件規則（0.4.95，`AiChatAttachmentPolicy`）

| 種類 | 格式 | 單檔上限 |
|------|------|----------|
| 圖片 | png、jpg（jpeg）、gif、webp | 10 MB |
| 文件 | pdf、docx、txt、md、csv（與專案附件能擷取文字的清單相同） | 20 MB |

- 一次提問最多 **5** 個附件；空檔案、不支援格式、超過上限逐一提示後略過，其餘照收。
- 截圖貼上時瀏覽器給的檔名一律是 `image.png`，改名為「貼上的圖片-日期時間.png」（`chat-attach.js`）。純文字貼上照舊進輸入框。
- 送出成功才清掉待送附件；失敗時留著，改一下問題就能重送。

### AI 抽出待辦（`TodoExtractionModal`，0.4.61）

- 按鈕先跳**費用確認**（非紅色，「每次抽取固定一次呼叫」；關掉視窗再開會再計費一次），確認後才開視窗——視窗一開就會呼叫模型，所以確認必須放在按鈕上。
- 只讀 `Meeting.DraftContent`，不讀逐字稿。候選每列可改標題、負責人、截止日、優先度；描述唯讀。狀態一律「待辦」。
- 與本會議先前已加入的待辦**標題重複者標示「已加入過」並預設不勾選**（0.4.75）；不阻擋重複加入。
- 逐條 `TodoService.BeforeAddCheckAsync` → `AddAsync`，部分失敗時列出哪幾條沒加入，**成功的從清單拿掉**（0.4.115，避免再按一次重複建立）。
- 0.4.83 起只在專案項目頁：待辦必須有專案，未歸屬會議的入口只是「一顆騙人的按鈕」。

## 四、內部系統運作

### 資料流

- UI：`ProjectViewView`／`MeetingViewView` → `AiChatModal` →（注入）`AiChatService` → `AiChatStore`（檔案）＋ `BackendDBContext`（讀會議與附件清單）＋ `ITextGenerationProvider`。Blazor Server 直接呼叫服務，**沒有對外 Web API 端點**。
- 模型：複用會議紀錄生成同一個 `ITextGenerationProvider`（依 `LlmSettings.DefaultProvider` 挑選，目前是 Azure OpenAI），沒有第二條到 Azure OpenAI 的路徑。同步互動，沒有佇列與背景 worker。串流回呼 `onDelta` 傳增量文字，UI 以 `InvokeAsync` 切回 UI 執行緒。
- 系統提示詞要求全程繁體中文（台灣用語）、只根據參考資料回答、找不到就說明、盡量指出出處。

### 脈絡組裝（`AiChatService.GenerateAnswerAsync`、`ChatContextBuilder`）

- 來源順序即優先權：**使用者附件文件** → 專案層級（各會議紀錄，依完成時間新到舊 → 專案附件全文）／單一會議（會議紀錄 → 逐字稿）。
- 總預算 `ChatContextBuilder.DefaultMaxChars = 60000` 字元。空間不足時截尾並在脈絡裡插入「已截斷」提示，讓模型也知道看到的不是全文；回傳 `UsedLabels`／`TruncatedLabels`／`SkippedLabels`。
- 附件文字擷取（`AttachmentTextExtractor`）：`.pdf` 用 PdfPig、`.docx` 用 DocumentFormat.OpenXml、`.txt`／`.md`／`.csv` 直接讀（刻意避開 AGPL 的 iText）。掃描檔 PDF 擷取為空、或格式不支援時回 null，列入「無法讀取」。
- 先前對話：只帶**最近 6 輪**（`AiChatService.MaxHistoryTurns`）；更早的仍在檔案裡供畫面翻閱。
- 附件：這次的附件＋最近 6 輪提問的附件一起送（`CollectAttachments`），圖片單次最多 **8 張**（`AiChatAttachmentPolicy.MaxImagesPerRequest`，保留最新的），以 content parts（base64 data URL）隨訊息送出；沒有圖時 `content` 仍是純字串，會議紀錄生成與抽出待辦送出的內容不受影響。
- 沒有任何可用內容時直接拋錯（「這個專案目前沒有可供查詢的資料…」／「這場會議目前沒有可供查詢的資料…」），不花錢呼叫模型。
- 每次呼叫（含失敗、取消、重新產生）都記進用量帳本，功能別 `AiUsageFeature.AiChat`（`AiChatService.RecordUsageAsync`，經 `AiUsageRecorder`）。

### 對話儲存（`AiChatStore`，Singleton）

- 根目錄 `SystemSettings.ExternalFileSystem.AiChatPath`（`appsettings.json` 預設 `C:\temp\MeetingRecord\AiChat`），啟動時 `Program.cs` 建立目錄。
- 路徑由範圍＋對象 Id＋對話 Id 直接算出：`project/<專案Id>/<對話Id>.jsonl`、`meeting/<會議Id>/<對話Id>.jsonl`；**資料庫連相對路徑都不存，也沒有任何資料表**。對話 Id 前段是 `yyyyMMddHHmmssfff`，檔名即可排序。
- 格式 **JSONL**、UTF-8 含 BOM、中文不逃脫；每輪 append 兩行（`user`＋`assistant`）。第一行是 `role: "meta"` 的標題／建立者紀錄，**不是訊息**——`ReadHistoryAsync` 與索引映射 `MapValidLineIndexes` 必須用同一套排除條件。標題沒改過時取第一句提問（超過 20 字截斷）。
- 附件存在對話檔旁的 `<對話Id>.files/`，磁碟檔名隨機、只保留副檔名；讀取時一律只取檔名部分防路徑跳脫（`GetAttachmentFullPath`）。附件先存檔再生成，生成或寫入失敗時刪掉（`TryDeleteAttachments`）。
- 併發：寫入以完整路徑為鍵的 `SemaphoreSlim` 序列化（刪除時刻意不移除鎖）；讀取以共用模式開檔（`ReadLinesSharedAsync`，0.4.115），寫入遇短暫鎖定最多重試 5 次。編輯／重新產生走 `UpdateMessagesAsync`：樂觀鎖帶 `ExpectedRole`＋`ExpectedContent`，寫暫存檔再 `File.Move` 原子替換；對話已刪時回 `NotFound`，**絕不重建檔案**。
- 提問前（`ConversationExists`）與寫入時（`AppendTurnAsync(mustExist: true)`）都確認對話還在，被刪就提示「這段對話已經被刪除，請開新對話再提問。」，不替已刪除的對話付費，也不會讓對話復活或重建孤兒目錄（0.4.115）。
- 舊資料相容：0.4.79 之前的單檔 `project/3.jsonl` 在第一次列清單時自動搬進 `project/3/<新Id>.jsonl`（先寫新檔、成功才刪舊檔，冪等）。
- 清理：刪專案／會議時由 `ProjectService.DeleteAsync`／`MeetingService.DeleteAsync` 呼叫 `AiChatStore.TryDelete` 刪整個對象資料夾（含附件、含未轉檔的舊單檔）。API 刪會議／專案也走同一支服務（0.4.115）。`CountQuestions` 遞迴計算所有提問行，供儀表板「AI 問答次數」。

### 設計決策

| 議題 | 結論 |
|------|------|
| 對話放哪裡 | **檔案系統，不放資料庫**（0.4.60 移除 `AiChatMessage` 資料表，migration `DropAiChatMessage`，既有紀錄未搬移）。對話文字從不需要查詢或索引，只會整段讀出，放資料庫只會膨脹 |
| 對話共用還是私有 | **同對象底下所有人共用**，清單標示是誰開的；可接續別人的對話、避免重複詢問 |
| 訊息怎麼定位 | **用「第 n 則」索引，不加 Id**。沒有單則刪除、append 只加在尾端、編輯不改行數，索引是穩定的 |
| 編輯 vs 重新產生 | 「儲存」只改文字、不呼叫模型、不動 `createdAt`；「重新產生答案」用改過的問題重跑並覆蓋原答案，提問與回答在同一次原子重寫中一起換掉，`askedBy` 換成實際操作的人，餵給模型的歷史只取這一輪之前（`TakeHistoryBefore`），沿用原提問的附件 |
| 付費確認 | 提問**不確認**、改常駐提示（一次提問一次呼叫，每句都確認會讓功能不能用）；**重新產生答案**跳紅色確認（會覆蓋原答案並計費）；**抽出待辦**跳非紅色確認（只產生候選、不覆蓋資料） |
| Markdown 安全 | 全站唯一的 `MarkdownRenderer`，畫面與 PDF 共用：明列擴充（不用 `UseAdvancedExtensions()`，避開 `GenericAttributes` 注入）、`DisableHtml()`、AST 走訪淨化連結（只放行 `http`／`https`／`mailto`、相對位址與錨點）、`UseCjkFriendlyEmphasis()` 讓 `**中文：**` 正確變粗體 |
| PDF 權限 | 沿用宿主頁「匯出」，不新增權限鍵——避免「不能匯出會議紀錄、卻能匯出 AI 答案」 |
| 附件文字快取 | **不做**。`ICacheService` 在 Web 層，Business 層取用不到；若成瓶頸，正解是上傳時擷取存進資料庫 |
| 資料外送 | 專案附件全文、使用者附件與圖片會送到 Azure OpenAI（與轉錄／生成同一個租戶） |

## 五、權限與安全

- **資料可見範圍跟著專案走**（0.4.99 起，0.4.102 起為主責＋協作團隊）：`AiChatService` 每個公開方法第一行呼叫 `EnsureAccessAsync`，經 `ProjectAccessService.GetAsync()` 判斷——專案問答要 `CanViewProject`，會議問答要 `CanViewMeeting`（已歸屬看專案的團隊；**未歸屬的會議只有上傳者看得到**）。管理者看全部。看不到時當成不存在，訊息「找不到這個專案或會議，可能已被刪除或沒有權限。」。這一層必須在服務層：對話檔與附件路徑由 Id 直接算出，沒擋的話知道 Id 就讀得到別人的對話。
- 讀附件（預覽、下載）也重新檢查權限（`ReadAttachmentAsync`，0.4.114），開著的視窗在權限被收回後讀不到。
- **AI 抽出待辦**：`TodoExtractionService.ExtractAsync` 以 `CanViewMeeting` 守門，重複標題查詢以 `ProjectAccess.Filter` 限縮；寫入走 `TodoService`，套用待辦本身的專案可見規則。
- 動作權限（第二節表格）：沒有權限時不顯示按鈕；改名、編輯、重新產生、刪除的處理函式開頭會再檢查一次 `CanEdit`／`CanDelete`。AI 問答本身只讀資料，不會改專案、會議或待辦。
- 附件規則畫面與服務層各擋一次（`AiChatService.ValidateAttachments`），畫面擋是即時提示，服務層擋是因為畫面擋不住所有呼叫路徑。
- 回答渲染：只經 `MarkdownRenderer`（見第四節設計決策），無原始 HTML、無危險連結通訊協定。
- 刪除專案／會議（UI 與 API）一律清掉對話與附件，避免「同一個編號被重新配給後讀到舊對話」（0.4.115 修正的高嚴重度問題；API 新增一律由資料庫配號）。

## 六、錯誤與邊界

| 狀況 | 行為 |
|------|------|
| 尚未設定文字生成供應商 | 「尚未設定文字生成供應商，請在 `LlmSettings:DefaultProvider` 指定。」 |
| 對象沒有任何可用資料 | 拋錯說明「沒有可供查詢的資料」，不呼叫模型 |
| 附件是掃描檔 PDF 或擷取失敗 | 列入來源說明條的「無法讀取」，不中斷提問 |
| 圖片檔被手動刪掉 | 列入「無法讀取」，其餘照送 |
| 生成失敗或寫入失敗 | 顯示「回答失敗：…」，收回樂觀顯示的提問、刪掉剛存的附件；帳本記 `Failed`／`Cancelled` |
| 模型不支援影像輸入 | Azure 回 400，顯示「回答失敗：文字生成 API 回應 400…」；純文字與文件附件不受影響（**尚未在正式模型實測**） |
| 對話在提問期間被別人刪除 | 「這段對話已經被刪除，請開新對話再提問。」，不寫入、不重建 |
| 重新產生時原提問已不在／已被改 | 送 API 前就擋：「這則提問已經不在對話裡了…」「這則提問已被其他人改過…」 |
| 重新產生期間對話被清空或更動 | 新答案不寫入（錢已花、已記帳）：「這段對話已經被清空，新的回答沒有寫入。」／「…在產生期間被其他人更動…」 |
| 編輯時樂觀鎖不符 | `UpdateOutcome.Conflict`，提示重新整理 |
| 檔案裡有壞掉的行 | 跳過並記 Warning，其餘照讀；重寫時壞行原樣保留 |
| 複製到剪貼簿失敗 | `clipboard.js` 的 `copyText` 回傳 false，顯示「請直接選取訊息內容後按 Ctrl+C」，不假裝成功 |
| 處理中按關閉 | 擋下並提示等完成 |
| 抽出待辦：模型輸出非 JSON／空 | `TodoExtractionParser` 回空清單，顯示中性提示而非錯誤；相對日期留空、不認得的優先度給「中」 |

## 七、驗收與測試

自動化測試（`src/MeetingRecord/MeetingRecord.Tests/`）：

- `AiChatStoreTests`：一輪問答往返、第一行帶 BOM 也讀得出、append 後 BOM 只有一個（看原始位元組）、特殊字元不失真、專案與會議同 Id 不互相污染、壞行跳過、`CountQuestions` 只算提問；0.4.115 的 `AppendTurn_MustExist_OnDeletedConversation_ShouldThrow_AndNotRecreateIt`、`AppendTurn_WhileAnotherReaderHasFileOpen_ShouldSucceed`。
- `AiChatStoreUpdateTests`：就地編輯、壞行與 meta 行的索引映射、原子重寫、樂觀鎖衝突、`Update_ShouldReturnNotFoundAndNotRecreateDeletedConversation`。
- `AiChatConversationTests`：多段對話（新對話沒有訊息、立即出現在清單、meta 行不位移索引、`CountQuestions_ShouldIgnoreMetaLinesAndReachNestedFolders`、標題取第一句與截斷、改名、舊檔轉檔與冪等、`TryDelete_ShouldRemoveEveryConversation`）。
- `AiChatAttachmentTests`：格式與大小規則、存檔讀回、舊格式相容、清單不把 `.files/` 當對話、刪對話連附件、失敗清附件、路徑跳脫、附件收集順序與歷史視窗、圖片張數上限、提示詞標註附件、不帶圖時 content 仍是字串、帶圖時的 content parts、PDF 列出附件檔名。
- `AiChatServiceTests`：`TakeRecentHistory`、`BuildUserPrompt`、`TakeHistoryBefore`、可擷取格式判斷。
- `ChatContextBuilderTests`：預算、截斷提示與三份標籤清單。
- `MarkdownRendererTests`：渲染與安全（HTML 逸出、`GenericAttributes`、`javascript:` autolink、控制字元繞過、錨點放行、CJK 粗體）。
- `AiChatDocumentExporterTests`：整段／單則 HTML 與檔名（含對話標題）。
- `TodoExtractionParserTests`：抽出待辦的 LLM 輸出解析。
- `GoLiveReviewApiTests.MeetingApi_Delete_ShouldGoThroughServiceRules_AndRemoveAiChat`：API 刪會議會清對話。

⚠️ 目前**沒有**直接呼叫 `AiChatService` 公開方法、驗證 `EnsureAccessAsync` 擋下看不到的專案／會議的單元測試；可見規則本身由 `ProjectAccessTests`（`CanViewMeeting_UnassignedMeeting_ShouldOnlyBeVisibleToUploader` 等）守住。

人工驗收（會產生費用的步驟需自行決定）：

1. 專案頁工具列與歷史會議紀錄列、會議頁列都能開啟問答；會議頁只有有草稿或逐字稿的列才有按鈕。
2. 提問後回答串流顯示、完成後為 Markdown 渲染；來源說明條正確列出截斷與無法讀取的來源。
3. 開新對話（空白對話時停用）、切換、改名、刪除；關掉再開歷史還在。
4. 編輯 → 儲存不呼叫模型；重新產生跳紅色確認、取消後沒有任何呼叫。
5. 附加圖片與文件（迴紋針、貼上、拖放），超過 5 個或不支援格式會提示。
6. 只有檢視權限的角色看不到改名、編輯、刪除鈕；沒有匯出權限看不到 PDF 鈕。
7. 不同團隊的使用者看不到彼此專案的對話；未歸屬會議只有上傳者能問。
8. 刪除會議／專案後，`AiChatPath` 下對應資料夾消失。

## 八、相關程式與文件

### 版本沿革

- 0.4.51：專案層級與單一會議 AI 問答上線，對話存 `AiChatMessage` 資料表；0.4.52～0.4.53 修正送出鈕與輸入框清空（`BindOnInput`、debounce 0）。
- 0.4.60：對話改存檔案系統（JSONL），移除 `AiChatMessage` 資料表。
- 0.4.61：AI 抽出待辦。
- 0.4.65：付費動作二次確認；問答改為常駐費用提示。
- 0.4.70：Markdown 渲染、複製、編輯、重新產生答案、整段／單則 PDF、清空補上確認。
- 0.4.73：會議紀錄頁也有 AI 問答入口。
- 0.4.75～0.4.76：抽出待辦標示重複。
- 0.4.79：多段對話、對話清單與開新對話，舊單檔自動轉檔。
- 0.4.83：抽出待辦入口收斂到專案項目頁。
- 0.4.94：視窗滿版。
- 0.4.95：可附加圖片與檔案。
- 0.4.99／0.4.102：問答跟著專案的可見範圍（0.4.102 起為主責＋協作團隊）。
- 0.4.109：改名／編輯／重新產生需修改權限，刪除需刪除權限。
- 0.4.114～0.4.115：讀附件重新檢查權限；共用模式讀檔、已刪對話不復活、處理中不能關視窗、API 刪會議清對話。

### 程式

- `src/MeetingRecord/MeetingRecord.Business/Services/AiChat/AiChatService.cs`（`AiChatService`：`EnsureAccessAsync`、`ListConversationsAsync`、`CreateConversationAsync`、`RenameConversationAsync`、`GetHistoryAsync`、`ClearHistoryAsync`、`AskAsync`、`UpdateMessageAsync`、`RegenerateAsync`、`GenerateAnswerAsync`、`BuildUserPrompt`、`CollectAttachments`、`ReadAttachmentAsync`、`RecordUsageAsync`；`AiChatScope`、`AiChatMessageItem`、`AiChatAnswer`）
- `src/MeetingRecord/MeetingRecord.Business/Services/AiChat/AiChatStore.cs`（`AiChatStore`：`ListConversationsAsync`、`AppendTurnAsync`、`ReadHistoryAsync`、`UpdateMessagesAsync`、`TryDelete`、`TryDeleteConversation`、`SaveAttachmentsAsync`、`GetAttachmentFullPath`、`ConversationExists`、`CountQuestions`、`MigrateLegacyIfNeeded`）
- `src/MeetingRecord/MeetingRecord.Business/Services/AiChat/AiChatAttachment.cs`（`AiChatAttachmentPolicy`）、`ChatContextBuilder.cs`（`ChatContextBuilder.Build`）、`AttachmentTextExtractor.cs`
- `src/MeetingRecord/MeetingRecord.Business/Helpers/MarkdownRenderer.cs`（`MarkdownRenderer.ToHtml`、`SanitizeUrl`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Export/AiChatDocumentExporter.cs`（`BuildConversationHtml`、`BuildMessageHtml`、`BuildConversationFileName`、`BuildMessageFileName`）
- `src/MeetingRecord/MeetingRecord.Business/Services/TodoExtraction/TodoExtractionService.cs`（`ExtractAsync`、`GetExistingTitlesAsync`）、`TodoExtractionParser.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/ProjectAccessService.cs`（`ProjectAccessService.GetAsync`；`ProjectAccess.CanViewProject`、`ProjectAccess.CanViewMeeting`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Projects/AiChatModal.razor`、`AiChatModal.razor.cs`（`OnAskAsync`、`OnRegenerateAsync`、`OnDeleteConversationAsync`、`OnAttachmentsSelectedAsync`、`OnCopyAsync`、`OnCancelAsync`）、`AiChatModal.razor.css`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Projects/TodoExtractionModal.razor`、`TodoExtractionModal.razor.cs`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Projects/ProjectViewView.razor`、`ProjectViewView.razor.cs`（`OpenProjectChat`、`OpenMeetingChat`、`OpenTodoExtractionAsync`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Meetings/MeetingViewView.razor`、`MeetingViewView.razor.cs`（會議頁入口）
- `src/MeetingRecord/MeetingRecord.Web/wwwroot/js/chat-attach.js`（貼上與拖放）、`wwwroot/js/clipboard.js`（`copyText`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Commons/FormModalHelper.razor`（`.ai-chat-modal` 固定高度）
- `src/MeetingRecord/MeetingRecord.Models/Systems/SystemSettings.cs`（`ExternalFileSystem.AiChatPath`）、`src/MeetingRecord/MeetingRecord.Web/appsettings.json`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`（`AiChatStore` Singleton、`AiChatService` Scoped）
- `src/MeetingRecord/MeetingRecord.Share/Enums/AiUsageFeature.cs`（`AiUsageFeature.AiChat`、`TodoExtraction`）
- 測試：`AiChatStoreTests.cs`、`AiChatStoreUpdateTests.cs`、`AiChatConversationTests.cs`、`AiChatAttachmentTests.cs`、`AiChatServiceTests.cs`、`ChatContextBuilderTests.cs`、`MarkdownRendererTests.cs`、`AiChatDocumentExporterTests.cs`、`TodoExtractionParserTests.cs`、`GoLiveReviewApiTests.cs`

### 交叉連結

- [專案項目 PRD](專案項目-prd.md)、[會議紀錄 PRD](會議紀錄-prd.md)、[待辦事項 PRD](待辦事項-prd.md)、[AI 用量分析 PRD](AI用量分析-prd.md)、[紀錄分類與團隊權控 PRD](紀錄分類與團隊權控-prd.md)
- [開發慣例與限制速查](../architecture/開發慣例與限制速查.md)、[檔案上傳機制](../features/檔案上傳機制.md)
- changelog：[AI 問答](../changelog/2026-09-08-AI問答.md)、[改存檔案系統](../changelog/2026-09-08-AI問答改存檔案系統.md)、[AI 抽出待辦事項](../changelog/2026-09-10-AI抽出待辦事項.md)、[付費動作二次確認](../changelog/2026-09-10-付費動作二次確認.md)、[複製修改與 Markdown 渲染](../changelog/2026-09-15-AI問答複製修改與Markdown渲染.md)、[多段對話](../changelog/2026-09-16-AI問答多段對話.md)、[抽出待辦入口收斂](../changelog/2026-09-18-註解外洩修復與抽出待辦入口收斂.md)、[視窗滿版](../changelog/2026-09-22-AI問答視窗滿版.md)、[附加圖片與檔案](../changelog/2026-09-22-AI問答附加圖片與檔案.md)、[角色權限實際生效修正](../changelog/2026-10-01-角色權限實際生效修正.md)、[上線前全面審查與修正](../changelog/2026-10-02-上線前全面審查與修正.md)

> 返回 [PRD 主控台](README.md)
