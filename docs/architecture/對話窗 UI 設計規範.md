# 對話窗 UI 設計規範

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.118
- 最後核對日期：2026/10/08

## 摘要

本文件收錄所有**浮層**的固定設計模式，範圍包括：

- 表單對話窗（新增與修改記錄）與唯讀明細窗
- 全螢幕工作窗（會議紀錄編修視窗、AI 問答）
- `ModalService.ConfirmAsync` 二次確認框
- `NotificationService` 通知與 `MessageService` 消息條
- 對話窗內的鍵盤送出、取消確認與拖放上傳

常駐介面另見 [介面視覺設計規範](介面視覺設計規範.md)，包括側邊欄、頁首、表格、按鈕、狀態徽章與燕麥奶茶色票。兩份文件的分工是：色票、外框 z-index 與按鈕規則以那一份為準，對話窗的尺寸、行為與確認流程以本文件為準。

本文件的內容都已對照 `src/MeetingRecord/MeetingRecord.Web/**` 的現行原始碼核對。速查表 [開發慣例與限制速查](開發慣例與限制速查.md) 的 §6.3（付費動作）、§6.5（對話框版面）、§6.8（輸入法與 Enter）與 §6.10（表單取消）仍是硬性紅線，本文件是它們的展開版。以下路徑都相對於 `src/MeetingRecord/MeetingRecord.Web/`。

---

## 一、浮層的種類

| 類型 | 判準 | 尺寸／版型 | 本文件適用章節 |
|---|---|---|---|
| **表單對話窗** | 內含 `EditForm`／`AntDesign.Form`，使用者要輸入或修改記錄 | `form-modal-compact`／`-standard`／`-large`／`meeting-view-modal` 四擇一 | 二～七 |
| 選取型對話窗 | 只有下拉選取、沒有自由輸入（AI 轉會議紀錄、歸屬到專案） | `form-modal-compact` | 二～四、六 |
| 唯讀明細窗 | 只是攤開來看（關於、專案附件、待辦詳情） | `Footer="null"`，掛尺寸 class | 二～四 |
| **全螢幕工作窗** | 長時間停留的工作面板（會議紀錄編修、AI 問答） | `meeting-view-modal`（＋`ai-chat-modal`） | 十一 |
| **二次確認框** | `ModalService.ConfirmAsync`，一句話、兩顆按鈕 | 吃基準線，不掛尺寸 class | 八 |
| 通知／消息條 | `NotificationService`（右下角）／`MessageService`（頂部） | AntDesign 預設 | 九 |

---

## 二、樣式寫在哪裡 ⚠️

**所有 Modal 外框樣式只寫在 `Components/Commons/FormModalHelper.razor` 的全域 `<style>`。**

- AntDesign 的 Modal 會被 portal 到 DOM 的其他位置，拿不到檢視的 scope 屬性。寫在檢視 `.razor.css` 裡的 `.ant-modal-*` 會被編成 `.ant-modal-*[b-xxxxx]`，**永遠選不到，而且不會報錯**。
- 檢視自己在 Modal 裡寫的 markup（例如 `.ai-chat-shell`、`.markdown-editor-split`）仍然帶得到 scope 屬性，可以留在該元件的 `.razor.css`。
- ⚠️ **`FormModalHelper` 只在 `Components/Layout/MainLayout.razor` 掛一次**，全站都吃得到，不要在頁面裡再掛一個。0.4.69 之前 layout 沒掛，各頁就自己補了 8 個，結果同一段 `<style>` 在 DOM 裡出現 9 次，而「變更密碼」的樣式從來沒生效過。0.4.82 把那 8 個移除了。
- 這段 `<style>` 是寫在 `.razor` 裡的，所以 at 規則要寫 `@@media`；它沒有任何 scope，新增選擇器時要取得夠獨特。

---

## 三、基準線：所有對話窗共用

`FormModalHelper.razor` 的基準線（0.4.82）套用到**每一個**對話窗，包括確認框：

```css
.ant-modal         { top: 4vh; padding-bottom: 0; max-width: 94vw; }
.ant-modal-content { display: flex; flex-direction: column; max-height: 92vh; overflow: hidden; }
.ant-modal-header  { flex-shrink: 0; padding: 12px 20px; }
.ant-modal-footer  { flex-shrink: 0; padding: 10px 20px; }
.ant-modal-body    { flex: 1 1 auto; min-height: 0; overflow-y: auto; padding: 16px 20px; }
```

（原始碼每條都帶 `!important`。）

效果是：**外框不捲動，標題列與按鈕列永遠看得到，只有內容區會捲。**新的對話窗不必做任何事就已經符合這個行為。

- ⚠️ `min-height: 0` 不能省。flex 子項的預設最小高度是內容高度，少了它 `overflow-y` 不會生效。
- 橫向 padding 一律 20px，標題、第一個欄位與按鈕列的左緣才會對齊。
- ⚠️ **刻意不覆寫 `.ant-modal-wrap` 的 `overflow: auto`。**「4vh 加上最多 92vh」已經保證放得進視窗。萬一日後有人加大 `max-height`，`auto` 至少還能捲到底；改成 `hidden` 的話，按鈕會被永久裁掉，使用者也看不出來。

---

## 四、尺寸分級：只負責寬度 ⚠️

刻意不是一律滿版：3 個欄位的表單撐滿整個螢幕，欄位會漂在一片空白中間。

| class | 實際規則（`FormModalHelper.razor`） | 適用 |
|---|---|---|
| `.form-modal-compact` | `width: 680px; margin: 0 auto` | 3～4 個欄位、唯讀短清單 |
| `.form-modal-standard` | `width: 880px; margin: 0 auto` | 5～8 個欄位、清單型 |
| `.form-modal-large` | `width: 90vw; max-width: 90vw; margin: 0 auto` | 權限矩陣、多份清單 |
| `.meeting-view-modal` | `width: 96vw; max-width: 96vw; top: 2vh`；`.meeting-view-modal .ant-modal-content { max-height: 96vh }` | 影音上傳、左右分欄編輯器、AI 問答 |
| `.ai-chat-modal`（疊加用） | `.ai-chat-modal .ant-modal-content { height: 96vh }` | 只有 AI 問答，見第十一節 |
| `.markdown-editor-modal`（疊加用） | `.ant-modal-body` 改成 flex column、`gap: 12px`、`overflow: hidden` | 會議紀錄編修視窗，見第十一節 |

### 4.1 各對話窗使用的尺寸（現況）

| 對話窗 | 元件 | class |
|---|---|---|
| 變更密碼 | `Layout/MainLayout.razor` | `form-modal-compact` |
| 關於 | `Layout/MainLayout.razor` | `form-modal-compact` |
| 分類維護 | `Views/Categories/CategoryViewView.razor` | `form-modal-compact` |
| 團隊維護 | `Views/Teams/TeamViewView.razor` | `form-modal-compact` |
| AI 轉會議紀錄 | `Views/Meetings/MeetingViewView.razor` | `form-modal-compact` |
| 歸屬到專案 | `Views/Meetings/MeetingViewView.razor` | `form-modal-compact` |
| 使用者維護 | `Views/Admins/MyUserView.razor` | `form-modal-standard` |
| 提示詞範本維護 | `Views/PromptTemplates/PromptTemplateViewView.razor` | `form-modal-standard` |
| 待辦維護、待辦詳情 | `Views/Todos/TodoViewView.razor` | `form-modal-standard` |
| 專案附件 | `Views/Projects/ProjectViewView.razor` | `form-modal-standard` |
| 抽出待辦 | `Views/Projects/TodoExtractionModal.razor` | `form-modal-standard` |
| 會議紀錄**檢視**（唯讀） | `Commons/MarkdownEditorModal.razor` | `markdown-editor-modal form-modal-standard` |
| 角色維護 | `Views/Admins/RoleViewView.razor` | `form-modal-large` |
| 專案維護 | `Views/Projects/ProjectViewView.razor` | `form-modal-large` |
| 會議紀錄維護（含影音上傳） | `Views/Meetings/MeetingViewView.razor` | `meeting-view-modal` |
| 會議紀錄**編修** | `Commons/MarkdownEditorModal.razor` | `markdown-editor-modal meeting-view-modal` |
| AI 問答 | `Views/Projects/AiChatModal.razor` | `meeting-view-modal ai-chat-modal` |

`MarkdownEditorModal` 的 class 是在執行期由 `ModalClass` 屬性依 `CanEdit` 切換的：編修時左右分欄需要寬度；唯讀時只有一欄文章，880px 就夠了，一行太長反而難讀。

### 4.2 三條規則

1. **每個 Modal 剛好掛一個尺寸 class。**
2. ⚠️ **掛了尺寸 class 就不要再設 `Width`。** 尺寸 class 的 `width` 帶 `!important`，會贏過 `Width` 參數產生的 inline style。參數會靜默失效，留著只會讓下一個人以為調它有用。
3. **高度一律用 `max-height`，不用固定 `height`。** 固定高度會讓短內容硬撐成整個螢幕高。0.4.82 之前 `.form-modal-large` 是固定 90vh，新增專案時三個清單都空著，最明顯。唯一的例外是 AI 問答。

另外，⚠️ **自繪按鈕列（`Footer="null"`）的對話窗要自己處理按鈕被捲走的問題。**基準線的 `flex-shrink: 0` 只保護 `.ant-modal-footer`。按鈕放在 body 裡時，會被上方佔滿高度的內容擠出捲動區：視窗看起來正常，但「儲存」永遠按不到。作法照 `.markdown-editor-modal .ant-modal-body`：把 body 改成 flex column，內容 `flex: 1 1 auto`，按鈕列 `flex-shrink: 0`。

第 1、2 條由 `ModalSizeClassTests` 守門（見第十三節）。

---

## 五、欄位排版：短欄位一行兩欄

把 `FormItem` 包進 `<div class="form-modal-grid">`，要跨整列的欄位加 `Class="form-modal-full"`：

```razor
<AntDesign.Form ... Layout="FormLayout.Vertical">
    <div class="form-modal-grid">
        <FormItem Label="名稱" Required>…</FormItem>
        <FormItem Label="代號">…</FormItem>
        <FormItem Label="描述" Class="form-modal-full">…</FormItem>
    </div>
</AntDesign.Form>
```

| 規則 | 內容 |
|---|---|
| 格線 | `grid-template-columns: repeat(2, minmax(0, 1fr)); column-gap: 24px`；每個 `.ant-form-item` 的 `margin-bottom: 16px` |
| 窄螢幕 | `@@media (max-width: 768px)` 退回單欄 |
| 併排 | 單行輸入：Input、Select、DatePicker、Switch、Checkbox |
| 跨整列 | 多行或寬內容：TextArea、檔案清單、Slider、權限矩陣、很長的 placeholder |

- ⚠️ 子選擇器用的是 `>`，所以 `FormItem` 必須是 grid 的**直接子元素**。包在 `@if` 裡沒有問題（Razor 的條件式不產生 DOM 節點），但多包一層 `<div>` 就會壞掉。
- 全站**不使用** AntDesign 的 `<Row>`／`<Col>`，兩欄一律用這個 CSS grid。

---

## 六、鍵盤：Enter 送出與 Esc 取消 ⚠️

一律走 `Components/Commons/FormKeyboardHelper.cs`（0.4.77 起）。0.4.77 之前有 7 份各自為政的 handler，沒有一份做輸入法防護，使用者按 Enter 選注音候選字時，整個對話框就存檔關掉了。

| 方法 | 判斷 | 使用處 |
|---|---|---|
| `IsSubmit(args)` | `Key == "Enter"`，**且 `!IsComposing`**，且沒有任何修飾鍵 | 表單送出、AI 問答送出、對話改名 |
| `IsReverseSubmit(args)` | Shift+Enter，且非組字中 | **只能用在單行欄位**。目前只有編修視窗的搜尋框（上一筆） |
| `IsCancel(args)` | `Key is "Escape" or "Esc"`（兩種瀏覽器拼法都收） | 表單取消 |

- **`KeyboardEventArgs.IsComposing` 是唯一的輸入法判斷途徑。**`KeyboardEventArgs` 沒有 `KeyCode`，在 Blazor 端無法用「檢查 229」的老方法。
- **Shift+Enter 在多行欄位會換行**：`IsSubmit` 回 false，瀏覽器的原生行為就會換行，不必另寫程式碼。
- 表單對話窗把 `@onkeydown="OnModalKeyDownAsync"` 掛在 `EditForm` 上。⚠️ 送出前的 `await Task.Delay(200)` 不能省：AntDesign `Input` 預設在 change 或 blur 時才回寫繫結值，Enter 送出時焦點還在欄位裡，不等的話會拿到舊值。
- AntDesign Modal 沒有開放 keydown 掛載點（`Keyboard` 參數只管 Esc），沒有 `EditForm` 的對話窗要自己包一層原生 `<div @onkeydown>` 來接。範例：`MainLayout.razor` 的變更密碼、`MeetingViewView.razor` 的 `.meeting-draft-request-form`、`TodoExtractionModal.razor` 的 `.todo-extract`。
- 表單內「新增標籤」這類**按鈕級**的輸入區要加 `@onkeydown:stopPropagation="true"`，否則外層 `EditForm` 會把那一下 Enter 當成送出。範例：`ProjectViewView.razor` 的 `.project-view-tag-editor`。

### 6.1 刻意不綁 Enter 的地方

| 對象 | 理由 |
|---|---|
| 所有 `ConfirmAsync` 確認框 | 確認框本來就是為付費與破壞性動作加的關卡；如果綁了 Enter，送出表單那一下 Enter 還沒放開，確認框就被按掉了 |
| 編修視窗的左欄 textarea | 那是在改一份多段落的長文件，不是輸入一句話就送出 |
| 多值輸入的「新增」鈕（`ProjectViewView` 常用名詞、常用與會人員） | 中文輸入法用 Enter 確認候選字，綁在按鈕上一定會誤觸 |
| 登入頁 | 靜態 SSR 的原生表單，Enter 本來就能送出，而且天生不受輸入法影響 |

### 6.2 AI 問答的 Enter 送出

`AiChatModal.OnKeyDownAsync` 只在 `FormKeyboardHelper.IsSubmit(args)` 成立時呼叫 `OnAskAsync`。輸入框是 `TextArea`，規格如下：

- `BindOnInput="true"`：不開的話，打字期間 `question` 一直是空字串，送出鈕一直停用。
- `DebounceMilliseconds="0"`：0.4.52 用過預設的 250ms，打完字立刻按 Enter 時讀到的是舊值，送出的問題會被截斷。
- ⚠️ AntDesign 的 `OnkeyDown` 是 `EventCallback`，拿不到 `preventDefault`。瀏覽器仍會插入一個換行，這個換行會在清空輸入框之後才寫回來，所以 `OnAskAsync` 的 `finally` 要**再清一次** `question`。

---

## 七、表單的取消要確認 ⚠️

判準只有一句：**這個動作會不會讓使用者失去自己輸入的東西？**（0.4.84 起，速查表 §6.10）

| | 會不會丟掉輸入 | 要不要確認 |
|---|---|---|
| 表單的「取消」（新增、修改視窗） | **會** | **要**，而且只有真的改過才問 |
| 背景工作進度面板的「取消」 | 不會，只是不做一件會花錢的事 | **不要**（見第八節） |

### 7.1 `FormDirtyHelper`

`Components/Commons/FormDirtyHelper.cs`：

- `Capture(model, params string?[] extras)`：開表單時拍一份 JSON 快照，`ReferenceHandler.IgnoreCycles`，以 `model.GetType()` 序列化。
- `IsDirty(snapshot, model, extras)`：取消時比對。**`snapshot` 為 null 時一律回 true**，所有不確定都往「多問一次」倒。
- `ConfirmDiscardAsync(modalService, subject, extraWarning)`：全站唯一的文案。標題「放棄編修」，內容是「{subject}已經改過但還沒儲存，關閉之後改動會消失。確定要放棄嗎？」，按鈕「放棄」（`Danger`）／「繼續編修」，`MaskClosable = false`。
- `extras` 用來放不在 model 上的暫存狀態，例如待上傳檔案。⚠️ 要傳「檔名:大小」字串，**不可以把 `IBrowserFile` 丟進去**，因為它帶著 Stream，序列化出來每個檔案都長得一樣。同一個呼叫點的 extras 個數也必須固定。
- ⚠️ **不要改用 `EditContext.IsModified()`。**`InputWatcher` 撈到的是外層 `EditForm` 的 EditContext，欄位卻住在內層 `AntDesign.Form` 底下，變更通知打不到外層，所以永遠回 false。

**已加上確認的視窗**：會議紀錄維護、專案維護、待辦、提示詞範本、使用者、角色、分類、團隊、變更密碼、抽出待辦，加上會議紀錄編修視窗（`MarkdownEditorModal` 直接比對原文字串，文案同樣走 `ConfirmDiscardAsync(ModalService, "這份會議紀錄")`）。抽出待辦多一句 extraWarning，提醒使用者關閉後重開會再計費一次。

**刻意不加確認的視窗**：

- 專案附件（只有下載鈕）
- 待辦詳情、關於（唯讀）
- AI 轉會議紀錄、歸屬到專案（只有選取，而且取消是不花錢的方向）

### 7.2 三個一定會踩到的坑

1. **`@bind-Visible` 是雙向的。**AntDesign 在呼叫 `OnCancel` **之前**就已經把視窗關掉了。使用者選「繼續編修」時，必須寫 `modalVisible = true` 把視窗開回來。用單向 `Visible=` 的元件（`TodoExtractionModal`、`MarkdownEditorModal`、`AiChatModal`）則**不可以**這樣寫，直接 `return` 就能留住視窗。
2. **Esc 會走兩條路。**Modal 的 `Keyboard="true"` 與表單的 `@onkeydown` 都會呼叫同一個 cancel handler，加了確認框之後會疊出兩個。每個表單都要一個 `isDiscardConfirming` 旗標擋重入，範例見 `CategoryViewView.OnModalCancelHandleAsync`。
3. **`TodoExtractionModal` 的快照要拍在 `ExtractAsync` 結尾**，不能拍在開窗時。開窗時候選清單還是空的，什麼都沒改也會被判成 dirty。

### 7.3 `Keyboard` 與 `MaskClosable`

- 一般表單：`Keyboard="true"`，讓 Esc 走上面的確認流程。
- 會議紀錄維護、AI 轉會議紀錄、歸屬到專案、抽出待辦、AI 問答：`MaskClosable="false"`，點到遮罩不會關閉。
- 會議紀錄編修視窗：`Keyboard` 與 `MaskClosable` 都是 `!CanEdit`。編修時 Esc 與點遮罩都關掉，因為一整篇打到一半的紀錄按錯一次鍵就沒了；唯讀時兩者放行。

---

## 八、二次確認框：`ModalService.ConfirmAsync`

全站約有 25 個呼叫點（含 `FormDirtyHelper` 內那一個共用點），一律用 `await modalService.ConfirmAsync(new ConfirmOptions { … })`，回傳 `bool`。確認框吃第三節的基準線，不掛尺寸 class。

### 8.1 共同格式

```csharp
var ok = await modalService.ConfirmAsync(new ConfirmOptions()
{
    Title = "確認刪除",
    Content = "確定要刪除這筆紀錄嗎？此操作無法復原。",
    OkText = "刪除",
    CancelText = "取消",
    OkButtonProps = new ButtonProps { Danger = true },
    MaskClosable = false
});
```

- `OkText` 寫**動作本身**（「刪除」「取代並開始轉錄」「覆蓋並重新產生」），不寫「確定」。
- 一律 `MaskClosable = false`。
- **`Danger` 跟著「覆蓋或刪除」走，不跟著「花錢」走。**會刪掉或覆蓋既有資料時才套紅色確認鈕。非破壞性的方向不套，例如啟用膠囊；停用則要套。

### 8.2 付費動作一律二次確認（速查表 §6.3）

全站只有兩個類別真的會呼叫付費 API：`AzureOpenAiTranscriptionProvider.TranscribeAsync` 與 `AzureOpenAiTextGenerationProvider.GenerateAsync`。任何最終會走到它們的 UI 入口，都要有 `ConfirmAsync`，**而且標題要寫出「（會產生費用）」**。

| 入口 | 位置 | 標題 | Danger |
|---|---|---|---|
| 上傳影音檔（存檔後自動轉錄） | `MeetingViewView.OnModalOKHandleAsync` | 確認上傳並開始轉錄（會產生費用） | 已有音檔時套（舊音檔與逐字稿會被刪除） |
| 執行／重新轉錄 | `MeetingViewView` | 確認執行轉錄／確認重新轉錄（會產生費用） | 已有逐字稿時套 |
| AI 轉會議紀錄 | `MeetingViewView`、`ProjectViewView` | 確認產生會議紀錄／確認重新產生（會產生費用） | 已有草稿時套 |
| 抽出待辦 | `ProjectViewView`（開窗**之前**的按鈕上） | 確認抽出待辦（會產生費用） | **永不**套（只產生候選） |
| AI 問答：修改提問後重新產生答案 | `AiChatModal` | 確認重新產生答案（會產生費用） | 套（會覆蓋原本的回答） |
| AI 問答：送出新問題 | `AiChatModal.OnAskAsync` | — | 刻意不確認，這是唯一沒有確認框的付費入口 |

三個位置陷阱：

1. **確認必須放在真正觸發 API 的那一步之前。**`TodoExtractionModal` 一開窗就會呼叫 `ExtractAsync`，所以確認要掛在開窗按鈕上；開窗方法必須是 `async Task`，不能是 `void`，否則會變成 fire-and-forget。
2. **掛在 `OnOk` 上的確認被取消時，要寫 `modalVisible = true`**（AntDesign 會自己關掉視窗）。上傳影音檔的確認放在驗證通過之後、主資料存檔之前。
3. **防連點要靠方法開頭的早退與服務層擋 `Pending`**，不能只靠 `Loading`。確認框擋不住「使用者確認兩次」。

文案只寫免費而且精確的事實（檔名、檔案大小），其餘用質性描述（「音檔越長費用越高」），**不要講「幾次 API 呼叫」**。講錯數字比不講更糟。

### 8.3 刻意不加確認的動作

- 背景工作進度面板的「取消」：取消是省錢的方向。誤觸防護改用另一種方式：面板上的「取消」與「關閉」兩顆按鈕都保留可見文字。前提是取消之後真的能重跑，`MeetingAdapterModel.CanRetryTranscription` 因此寫成排除式。
- 只切換檢視的膠囊（待辦頁負責人篩選）。

---

## 九、通知與消息條

| 服務 | 位置 | 用在哪裡 |
|---|---|---|
| `NotificationService.Open(new NotificationConfig { … })` | **一律 `Placement = NotificationPlacement.BottomRight`**（現有 66 處，全部右下角） | 存檔、刪除、匯出 PDF、下載、狀態切換、錯誤等主要結果 |
| `MessageService.SuccessAsync`／`WarningAsync`／`ErrorAsync` | 頁面頂部置中 | 輕量、即時的回饋：「新增成功」、「已複製到剪貼簿」、「已取代 N 筆」、附件數量超限、「AI 正在處理中，請等它完成再關閉視窗」 |

- 0.4.84 補齊了下載、匯出 PDF 與「從專案移除」的右下角提示，並把「從專案移除」從 `messageService` 改成右下角，與同檔其他結果提示一致。
- 現況是 `NotificationType` 用了 `Error` 37 處、`Warning` 31 處、`Success` 4 處。成功結果大多仍用 `Warning` 型別，原因見第十四節。
- 新增成功時，各清單頁會同時跳出頂部的 `messageService.SuccessAsync("新增成功")` 與右下角通知，共 8 個檔案。0.4.84 的 changelog 判斷這是當初刻意的樣板。

---

## 十、拖放上傳

### 10.1 表單裡的拖放區：`Components/Commons/FileDropZone.razor`

用在會議紀錄維護的影音檔（`mediaDropZone`）與專案維護的附件（`attachmentDropZone`）。

- **拖放靠瀏覽器原生行為完成**：`<InputFile>` 以 `opacity: 0` 蓋滿整塊，檔案就會落在它身上，瀏覽器自己設定 `input.files` 並觸發 change。`@ondragenter`／`@ondragleave` 只切換 `.file-drop-zone-active` 的視覺狀態，**一律不可以 `preventDefault`**，否則原生的 drop 會被擋掉。
- `@key="resetToken"` 用來讓呼叫端在用完檔案後呼叫 `Reset()` 重建 input，否則重新拖同一個檔案時不會觸發 change。⚠️ **只能在讀完檔案之後才換 key**：0.4.77 曾在選完檔案當下就換，Blazor 立刻銷毀了 input，稍後讀檔全部失敗。
- ⚠️ `Accept` 只對「檔案挑選對話框」有效，瀏覽器不會用它過濾拖進來的檔案。呼叫端要在 `OnChange` 裡自己再擋一次（例如 `MeetingMediaPolicy.IsAllowedFileName`）。

### 10.2 全頁防誤拖

`MainLayout.razor` 的 `.page` 掛了 `@ondragover:preventDefault="true"` 與 `@ondrop:preventDefault="true"`，**只設 preventDefault，不接 handler**。

- 不擋的話，把檔案拖到頁面空白處時，瀏覽器會直接導航去開那個檔案，Blazor 的 circuit 跟著斷線，填到一半的表單整份消失。
- 不接 handler，是因為 dragover 每幾十毫秒觸發一次，接了就會一直往返伺服器。
- 拖放區本身不受影響，因為它的 input 才是 drop 的實際目標。

### 10.3 AI 問答的貼上與拖放：`wwwroot/js/chat-attach.js`

- `AiChatModal.OnAfterRenderAsync` 呼叫 `meetingRecordChatAttach.attach` 掛上 paste、dragover、drop 事件；JS 端以旗標擋掉重複掛載，掛不上時只記 warning（迴紋針仍可用）。
- 與 `FileDropZone` 同一個思路：不自己把檔案傳給 .NET，而是塞進容器內 `<InputFile>` 的 `input.files` 再觸發 change。迴紋針挑選、貼上、拖放三條路共用 `OnAttachmentsSelectedAsync` 這一個入口。
- 截圖貼上時，瀏覽器給的檔名一律是 `image.png`，所以會改名為「貼上的圖片-時間戳.副檔名」。純文字貼上照常進輸入框。

---

## 十一、全螢幕工作窗

### 11.1 會議紀錄編修視窗：`Components/Commons/MarkdownEditorModal.razor`

檢視與編修共用同一個視窗（0.4.82 起），差別只在 `CanEdit`。

| 項目 | 編修（`CanEdit = true`） | 唯讀 |
|---|---|---|
| 尺寸 | `markdown-editor-modal meeting-view-modal`（96vw） | `markdown-editor-modal form-modal-standard`（880px） |
| 版面 | `.markdown-editor-split`：左邊原文、右邊預覽，上方常駐搜尋取代列 | `.markdown-editor-readonly`：只有預覽 |
| Esc／點遮罩 | 都關閉 | 放行 |
| 取消 | 有改動時跳 `ConfirmDiscardAsync` | 直接關 |

- `Footer="null"`，按鈕列自己畫（多了一顆「下載 PDF」），靠 `.markdown-editor-modal .ant-modal-body` 的 flex 規則把按鈕固定在底部，捲動交給左右兩欄各自負責。
- 搜尋框：Enter 跳下一筆（`IsSubmit`）、Shift+Enter 跳上一筆（`IsReverseSubmit`），兩者都擋組字中的 Enter。**刻意不綁 Ctrl+F**，因為 `@onkeydown:preventDefault` 在編譯期就決定了，攔了會連右邊預覽的瀏覽器原生搜尋都吃掉。
- **左右同步捲動**（0.4.100）：預覽用 `MarkdownRenderer.ToHtml(editingText, includeSourceLines: true)` 產生，每個區塊都帶 `data-source-line`。`TextEditorInterop.BindScrollSyncAsync` 呼叫 `wwwroot/js/text-editor.js` 的 `meetingRecordTextEditor.bindScrollSync`，兩邊各量出「這一行或這個區塊在第幾個像素」組成對應點，捲動時在兩個對應點之間內插，**對齊到段落，而不是按比例**。程式設定捲動位置也會觸發 scroll 事件，所以用「預期的值」擋掉回彈，不用旗標。視窗剛打開時內容可能還沒進 DOM，`bindScrollSync` 會回傳 false，讓 C# 端下一輪再試。`includeSourceLines` 預設關閉，PDF、AI 問答與使用說明都不要打開。

### 11.2 AI 問答：`Components/Views/Projects/AiChatModal.razor`

- class 是 `meeting-view-modal ai-chat-modal`，**全站唯一固定高度**（`height: 96vh`，0.4.94）。聊天視窗吃滿螢幕才正常；0.4.93 之前高度隨內容縮，剛開新對話時只有四百多 px 高。
- 版面 `.ai-chat-shell` 是 `grid-template-columns: 240px minmax(0, 1fr)`，左欄對話紀錄、右欄訊息；900px 以下退回單欄。
- 行太長的問題由訊息氣泡的 `max-width: min(82%, 960px)` 處理，**不要再用縮小整個視窗來解**。
- 單向 `Visible=`、`Keyboard="true"`、`MaskClosable="false"`、`Footer="null"`。
- **處理中不讓關**（0.4.115）：`IsBusy` 時，`OnCancelAsync` 跳 `MessageService.WarningAsync("AI 正在處理中，請等它完成再關閉視窗。")` 並 `return`。否則關掉再開另一個對象時，前一題的串流答案會出現在新視窗裡。
- 關閉時清掉待送附件，因為下次開啟可能是另一個專案。

---

## 十二、堆疊層級（z-index）

浮層一律使用 AntDesign 自己的層級，**不要覆寫**：

| 層 | z-index |
|---|---|
| `.ant-modal-mask`／`.ant-modal-wrap`（含確認框） | 1000 |
| `.ant-message`／`.ant-notification` | 1010 |
| `.ant-dropdown`／`.ant-select-dropdown`／`.ant-picker-dropdown` | 1050 |
| 我方的背景工作進度面板（`.job-progress-panel`）、`#blazor-error-ui` | 1000（刻意與 modal 同層） |

全站外框（頁首 100、側邊欄 101、收合鈕 102）都在 99 到 1000 之間，所以對話窗、通知與下拉選單永遠蓋在外框之上。外框規則與「父層困住子層」的陷阱見 [介面視覺設計規範](介面視覺設計規範.md) 第八節。

---

## 十三、守門測試

| 測試 | 守的是什麼 |
|---|---|
| `MeetingRecord.Tests/ModalSizeClassTests.cs` | 掃描 `Components/**/*.razor` 的每個 `<Modal `，有三條規則。`EveryModal_ShouldDeclareExactlyOneSizeClass`：每個 Modal 剛好掛一個尺寸 class（`form-modal-compact`／`-standard`／`-large`／`meeting-view-modal`）；`Class="@…"` 屬於執行期決定，跳過檢查（目前只有 `MarkdownEditorModal`），完全沒有 `Class` 則判失敗。`ModalWithSizeClass_ShouldNotAlsoSetWidth`：不得同時設 `Width`。`Scanner_ShouldActuallyFindTheModals`：至少掃到 15 個，而且包含 `MarkdownEditorModal.razor`，防止掃描器壞掉時全綠。排除未被引用的範例檔 `RoleViewViewSample.razor` |
| `MeetingRecord.Tests/FormKeyboardHelperTests.cs` | Enter／組字中／Shift／Ctrl／Alt／Meta／Esc 兩種拼法；`IsReverseSubmit` 的對稱規則 |
| `MeetingRecord.Tests/FormDirtyHelperTests.cs` | 快照穩定性（`Capture(m)` 等於 `Capture(m.Clone())`，涵蓋 8 個型別）；欄位、清單、字典與 extras 的變更偵測；null 快照回 true；extras 個數固定；`BuildDiscardOptions` 的文案與 `MarkdownEditorModal` 已出貨版本一字不差 |
| `MeetingRecord.Tests/RazorMarkupTests.cs` | Razor 註解不得包含自己的結束符號，避免開發者註解外洩到畫面 |

⚠️ **以下無法用單元測試驗證**（本專案沒有 bUnit，也測不了 overlay 與 JS interop），只能開畫面實測：

- 留白與捲動
- `@bind-Visible` 重開
- Esc 疊出兩個確認框
- 輸入法組字
- 拖放
- 同步捲動

---

## 十四、與本規範不一致的現況（已知、未修）

| 位置 | 現況 | 備註 |
|---|---|---|
| 各 View 的 `NotifySuccess`／inline 通知 | 成功結果大多用 `NotificationType.Warning`（橘色圖示），只有使用者／團隊／分類的啟用停用切換與角色「建立預設角色」用 `Success` | 0.4.84 changelog 記錄為「刻意不做」，建議日後抽 `ToastNotifier` 統一處理，並加一支釘住 `BottomRight` 的測試 |
| 8 個清單頁的新增分支 | 頂部 `messageService` 與右下角 notification 同時出現 | 同上，留給下一版 |
| 速查表 §6.3 | 寫的是「五條路徑，四條加了確認」 | AI 問答後來加了「重新產生答案」，而且有確認（見 8.2），計數已經不準 |
| 速查表 §6.8「刻意不綁 Enter」表 | 寫的是「19 個 `ConfirmAsync` 二次確認框」 | 現在 `.razor`／`.razor.cs` 的呼叫點約 24 個，加上 `FormDirtyHelper` 共用點共約 25 個 |
| `Components/Commons/BackgroundJobProgressPanel.razor.css` | 藍灰系字面色碼 | 見介面視覺設計規範第十節 |

---

## 延伸閱讀

- [介面視覺設計規範](介面視覺設計規範.md)：色票、側邊欄、按鈕、狀態膠囊、外框 z-index
- [開發慣例與限制速查](開發慣例與限制速查.md)：§6.3 付費動作、§6.5 對話框版面、§6.8 輸入法與 Enter、§6.10 表單取消
- [付費動作二次確認（0.4.65）](../changelog/2026-09-10-付費動作二次確認.md)
- [表單取消確認與下載提示（0.4.84）](../changelog/2026-09-18-表單取消確認與下載提示.md)

> 返回 [文件總索引](../README.md)
