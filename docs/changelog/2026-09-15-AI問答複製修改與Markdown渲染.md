# AI 問答：Markdown 渲染、複製、修改與 PDF 下載（0.4.69 → 0.4.70）

使用者提出兩件事：

1. AI 問答的每則訊息要能**複製**、**修改**，整段對話與單則訊息都要能**下載 PDF**；刪除則維持整段（使用者明確說「只能刪除整個紀錄，不能單獨刪除一個問題」）
2. AI 回答本來就是 Markdown，畫面上卻直接印原文，「很多 ## ** 之類的符號」。會議紀錄草稿的檢視畫面有同樣問題，使用者同意一併改

「修改」兩種都做：**儲存**只更正文字（不呼叫模型、不產生費用），**重新產生答案**會用改過的問題重跑 AI 並覆蓋原答案（會產生費用，依 §6.3 跳二次確認）。

---

## 一、Markdown 渲染：一條管線，畫面與 PDF 共用

新增 `Business/Helpers/MarkdownRenderer.cs`，是全站唯一的 Markdown → HTML 轉換點。原本 Markdig 只服務 PDF 匯出（`MeetingDocumentExporter` 裡的 private pipeline），Blazor 端完全沒有渲染。

### ⚠️ 不要為了「畫面比較危險」另開一條安全管線

直覺會想：畫面塞 `MarkupString` 有 XSS 面，PDF 是離線輸出所以維持原狀就好。**這個判斷是錯的。**
`HeadlessBrowserPdfRenderer` 是叫無頭瀏覽器去讀 `file://` 的 HTML，**瀏覽器會執行 script**，而且是本機檔案來源。注入到會議紀錄草稿（使用者可自由編修）的 `<script>` 會在**伺服器上**跑起來——比畫面 XSS 嚴重。

兩條管線還必然走鐘。所以只留一條，兩邊共用。

### 原本的 `UseAdvancedExtensions()` 有兩個注入點

從 Markdig 1.3.2 的 XML 文件實查：

- 它的定義是「**除了** BootStrap、Emoji、SmartyPants、軟換行外全部啟用」→ **`GenericAttributes` 在裡面**。`## 標題 {onclick="alert(1)"}` 會直接產出 `onclick` 屬性，而 `DisableHtml()` **擋不到**（那是屬性，不是 HTML 區塊）。
- CommonMark autolink 接受任意通訊協定：`<javascript:alert(1)>` 走的是 `AutolinkInline`，**不是** `LinkInline`。

所以新管線**明列擴充**（表格、清單、註腳、自動標題 id、自動連結…）而不是 `UseAdvancedExtensions()`，加上 `DisableHtml()`，並用 **AST 走訪**淨化連結：

```csharp
var document = Markdown.Parse(markdown, Pipeline);
foreach (var node in document.Descendants())
{
    if (node is LinkInline link) link.Url = SanitizeUrl(link.Url);
    else if (node is AutolinkInline auto) auto.Url = SanitizeUrl(auto.Url);
}
return Markdown.ToHtml(document, Pipeline);
```

走 AST 而不是 `HtmlRenderer.LinkRewriter` 或自訂 extension：這樣不必知道 Markdig 內部是哪個 renderer 處理哪種節點，涵蓋範圍看得出來也測得到。

`SanitizeUrl` 先丟掉控制字元再判斷通訊協定——瀏覽器就是這樣做的，不先丟的話 `java<TAB>script:` 可以直接繞過白名單。白名單是 `http`／`https`／`mailto`，**相對位址與 `#錨點` 要放行**，否則註腳與自動標題 id 全斷。

### ⚠️ `DisableHtml()` 是逸出不是丟棄

`<img src=x onerror=...>` 的輸出是 `&lt;img src=x onerror=&quot;…`。斷言寫「輸出不含 `onerror`」會失敗，而且失敗得沒有道理——要斷言的是**有沒有產生元素**（`DoesNotContain("<img")`），不是文字裡有沒有那串字。第一版測試就是這樣寫錯的。

### CommonMark 對中文不友善，`**備註：**` 不會變粗體

實跑既有對話時才發現的：`**備註：**兩份紀錄…` 原樣印出來。原因是結尾的 `**` 後面直接接中文字，不符合 CommonMark 的 right-flanking 判定。Markdig 1.3.2 有 `UseCjkFriendlyEmphasis()` 專門處理這件事，已啟用——這正是使用者抱怨「看到 **」的其中一種。

**這是推理不出來的**，是實跑量測時掃到 `rawMarkerStillVisible: true` 才追出來。

---

## 二、訊息定位：用索引，不加 Id

對話是 JSONL、一行一則，訊息只有 `role`／`content`／`askedBy`／`createdAt`，**沒有 Id**。

因為**沒有單則刪除**、append 只加在尾端、編輯不改行數，所以「第 n 則」是穩定的——不必改檔案格式、不必 migration，既有 12 筆 `AiChatStoreTests` 一筆都沒動。附帶好處：LLM 生成要跑好幾秒，期間別人 append 不會位移既有索引，「讀歷史 → 呼叫 API → 寫回索引 N」這條長流程天生安全。

`AiChatStore` 只新增一支方法（就地編輯與重新產生的差別只有「改一行還是改兩行」）：

```csharp
public readonly record struct MessageEdit(
    int Index, string ExpectedRole, string ExpectedContent, string NewContent, string? NewAskedBy = null);

public enum UpdateOutcome { Updated, Conflict, NotFound }

public Task<UpdateOutcome> UpdateMessagesAsync(
    AiChatScope scope, int targetId, IReadOnlyList<MessageEdit> edits, CancellationToken ct = default);
```

### ⚠️ 四個地雷

**整檔重寫會打爆無鎖的讀取。** `ReadHistoryAsync` 不進 `writeLocks`。原本 append 是單次 `AppendAllTextAsync`，最壞只是最後一行讀到半截（壞行本來就跳過）。改成整檔重寫之後，併發讀者會讀到被 truncate 的檔案，**整段對話會在別人畫面上憑空消失**。所以一律寫暫存檔再 `File.Move(tmp, path, overwrite: true)`。**這不能省。**

**「第 n 則有效訊息」≠「檔案第 n 行」。** `TryParseLine` 回 null 的行（壞 JSON、空行）會被跳過，但重寫時**必須原樣保留**——那可能是使用者自己手動編輯過的內容。多一支 private `MapValidLineIndexes` 做映射，並有兩筆測試守住。

**BOM 只能有一個。** `File.ReadAllLinesAsync` 讀進來時已經剝掉 BOM，用 `FileEncoding` 寫回去剛好一個。測試一定要**看原始位元組**——`File.ReadAllText` 會自動吃掉開頭的 BOM，用字串去數永遠是 0。另外重寫的結尾一定要留換行，否則下一次 append 會黏在最後一行後面。

**與「清空」的競態。** 檔案已被刪時編輯要回 `NotFound`，**絕不可重建檔案**（那會讓一則已刪的訊息憑空復活）。順手改掉一個既有缺陷：`TryDelete` 原本會 `writeLocks.TryRemove`，此時已持鎖的寫入者與下一個 `GetOrAdd` 會拿到**兩把不同的號誌**，互斥直接失效。現在不移除鎖——多留一個 `SemaphoreSlim` 遠比那個競態便宜。

樂觀鎖帶 `ExpectedRole` 而不只 `ExpectedContent`：重新產生時假設「提問的下一則是回答」，但檔案可能被手改過。

---

## 三、服務層

`AskAsync` 拆出 `GenerateAnswerAsync`（建脈絡 → 組提示 → 呼叫模型），`RegenerateAsync` 重用它，`ChatContextBuilder` 與 `BuildUserPrompt` 完全不動。差別只在餵進去的歷史與事後怎麼落庫。

`TakeHistoryBefore(history, index)` 只取「這一輪之前」的訊息——把後面也帶進去，模型會看到自己還沒被改寫的舊答案，等於拿未來解釋過去。

兩個決定：
- **重新產生會把 `askedBy` 換成實際操作的人**，否則「王小明問的」其實是李小華改的
- **就地編輯不動 `createdAt`**，那是「這則訊息何時發生」不是「何時被編輯」；也不新增 `editedAt` 欄位（沒被要求，加欄位就要面對舊檔沒有該欄位的問題）

提問與回答**一定要一起換掉**，兩筆 edit 送進同一次原子重寫——問題改了、答案還是舊的是最糟的中間態。

---

## 四、PDF

`MeetingDocumentExporter` 裡的 `PrintStyle`、`Sanitize`、`StripMediaExtension` 抽成共用的 `ExportPrintStyles.Base` 與 `ExportFileNameBuilder.SafeTitle`，新增 `AiChatDocumentExporter`（整段／單則、HTML 與檔名，純函式）。聊天版自己串一段版面樣式，**不套用會議紀錄的 `.doc-*` 版面**——那份是給單一篇文件用的。

`.chat-speaker` 用 `break-after: avoid` 而不是整則 `break-inside: avoid`：發話者不要落在頁尾、內容翻到下一頁，但長答案本身仍允許跨頁，硬擋只會讓整頁空一大片。

⚠️ **每則一顆下載鈕 = 每按一次起一個無頭瀏覽器**（數秒、上百 MB）。所有按鈕共用同一個 `IsBusy` 閘門。

PDF 鈕沿用既有的「匯出 PDF」權限（`角色_專案項目` ＋ `Export`），不新增權限鍵——否則同一個畫面上會出現「不能匯出會議紀錄、卻能匯出 AI 答案」。複製與編輯不設限（依使用者指示）。

---

## 五、剪貼簿：會失敗，而且不能假裝成功

⚠️ 真正的風險不是安全來源（localhost 算），是 **user activation**：Blazor Server 的點擊要先送到伺服器、處理完再回頭呼叫 JS，此時瀏覽器認定的使用者手勢可能已經過期。

所以 `copyText` 回傳布林（`clipboard.writeText` → catch → `execCommand` → catch → `false`），UI 依結果顯示成功 toast 或「請直接選取訊息內容後按 Ctrl+C」。**不要 fire-and-forget 假裝成功。**

實跑時 headless Chrome 就是失敗的，而畫面正確顯示了退路訊息——這條路徑有被走到。**真實瀏覽器（尤其 Firefox）仍需使用者自行確認。**

---

## 六、UI

- 每則訊息的工具列：複製／編輯／下載這則 PDF。⚠️ **不是 hover 才出現**——純 hover 的按鈕觸控裝置點不到、鍵盤也 tab 不進去（§6.1）。平時 `opacity: .35`，`:hover` 與 `:focus-within` 時才完全顯現
- 編輯使用者提問時，除了「儲存」另有「重新產生答案」，旁邊一行字說明兩者差別
- ⚠️ **`.ai-chat-bubble` 的 `white-space: pre-wrap` 必須拿掉**。渲染成 HTML 後，Markdig 產出的標籤之間本來就有換行，保留下來每個 `<p>`／`<li>` 之間會多一行、整個氣泡爆開。拆成 `.ai-chat-bubble-plain`（提問與串流中的暫時氣泡）與 `.ai-chat-bubble-markdown`
- **串流中維持純文字**，不逐 token 跑 Markdig（浪費，半截語法還會一直閃）。答完重載歷史才換成渲染版
- ⚠️ **內文樣式一定要 `::deep`**：`MarkupString` 產生的子節點拿不到 `[b-xxxxx]`。已從編譯後的 bundle 確認是 `.ai-chat-bubble-markdown[b-fg41wzovo4] h2`（父層有屬性、子選擇器沒有）
- ⚠️ **`LoadHistoryAsync` 每次都要重設編輯狀態**。別人清空對話後，編輯框會停在一個不存在的索引上
- 「清空這段對話」**補上二次確認**（`Danger`）——它直接刪檔、不可復原、而且對話是所有人共用的，先前完全沒有確認
- 會議紀錄草稿檢視從 `<pre>` 改成 `<div>`（`<pre>` 自帶等寬字型與 `white-space: pre`，渲染後兩者都是錯的）
- Bootstrap 的預設 `code` 是粉紅色，要用 `color: inherit` 蓋掉

---

## 七、驗證

測試 **579 → 649 全綠**（+70）。既有 `MeetingDocumentExporterTests` 與 `AiChatStoreTests` **一筆都沒改**，它們是這次重構的驗收條件。

新增：`MarkdownRendererTests`（32，一半是安全測試）、`AiChatStoreUpdateTests`（17）、`AiChatDocumentExporterTests`（17）、`AiChatServiceTests` +4。

### 實跑量測（另編一份開在 5289 埠）

| 項目 | 結果 |
| --- | --- |
| Markdown 渲染 | 6 則 → 3 則 markdown ＋ 3 則 plain；`**` → `<strong>`、清單 → `<ol>`、反引號 → `<code>`；`white-space: normal` |
| CJK emphasis | 修正前草稿檢視 `rawMarkerStillVisible: true`，啟用 `UseCjkFriendlyEmphasis()` 後為 `false` |
| `::deep` | 編譯後為 `.ai-chat-bubble-markdown[b-fg41wzovo4] h2`，父層帶屬性、子選擇器不帶 |
| 工具列 | 每則 3 顆、28×28、`opacity: .35` 下 `elementFromPoint` 仍落在按鈕內（點得到） |
| 重新產生的確認框 | 標題／內文正確、紅色確認鈕、**疊在問答視窗之上**（z-index 1000）；**按取消後沒有任何串流氣泡** |
| 清空的確認框 | 「將刪除這段對話的全部 6 則訊息…」；取消後仍是 6 則 |
| 編輯 → 儲存 | 畫面與檔案都更新，**檔案仍是 6 行、BOM 仍只有一個**，`createdAt` 與 `askedBy` 保留 |
| 複製 | headless 下失敗，畫面正確顯示退路訊息（沒有假裝成功） |
| PDF | 整段 280KB／3 頁、單則 106KB；檔名 `AI問答_客戶訪談與需求盤點_20260915.pdf`、`…_第2則_…`；內含 `JhengHei` 子集 ＋ `FontFile2` ＋ `Identity-H` ＋ `ToUnicode`（中文是真字型、可複製可搜尋） |

全程**沒有產生任何 API 費用**：兩次執行的日誌裡 `AI chat answered` 與 `answer regenerated` 都是 0 次，重新產生一律按取消。測試用的對話檔已還原成原本的 6 行。

### 仍需使用者自行確認

**真實瀏覽器的剪貼簿**——Chromium 對作用中分頁通常放行，**Firefox 很可能擋掉**；非 localhost 的 HTTP 部署則連 `navigator.clipboard` 都沒有。失敗時的畫面已經驗過，但成功路徑只能在真實瀏覽器上看。

> 返回 [changelog 索引](README.md)
