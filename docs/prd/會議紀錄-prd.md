# 會議紀錄 PRD

- 文件版本：1.7
- 文件狀態：已實作
- 現行系統版本：0.4.69
- 首次實作版本：0.4.27
- 最後核對日期：2026/09/11

## 一、目標與範圍

提供「會議紀錄」的維護能力：使用者在 `/meetings` 建立會議紀錄、上傳**一個**影音檔（音訊或視訊），系統以 FFmpeg 將其轉為 mp3 並切段後，逐段送 Azure OpenAI 的語音轉錄 API，串接成完整逐字稿存進檔案系統，並可在清單上直接預覽。

```
建立會議紀錄 ─► 上傳影音檔（≤1GB）─► 背景佇列
                                        └─► FFmpeg 抽音軌／轉 mp3／切 15 分鐘分段
                                              └─► 逐段 Azure OpenAI 轉錄 ─► 串接 ─► 逐字稿 .txt ─► 畫面預覽
```

**影音檔與逐字稿一律存放於檔案系統，不入資料庫**；資料表只保存相對路徑等中繼資料。存放根目錄與 AI 供應商（provider／endpoint／key／model）皆由 `appsettings.json` 決定。

非範圍（本版刻意不做）：
- 本頁**不含**「套用提示詞 → LLM 產生會議紀錄」。該能力已於 0.4.31 實作，但入口在 `/projects`（見 [專案項目 PRD](專案項目-prd.md)）而非本頁；流程全貌見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)，提示詞範本維護見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)。本頁只負責上傳、轉錄與逐字稿預覽與編修。
- 不做即時（會議進行中）轉錄與逐字稿串流。
- 不做說話者聲紋辨識與身分綁定。
- 不做逐字稿編修的版本歷程（0.4.55 起可線上編修，但為就地覆寫，改掉的舊內容不保留）。
- 不做影音檔／逐字稿的下載端點與線上播放。
- 不做一筆會議掛多個影音檔（一對一，替換即覆寫）。
- Web API 只開放中繼資料 CRUD，不開放上傳、轉錄與逐字稿讀取。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/meetings`（`MeetingPage.razor`，`MainLayout`） |
| REST API | `api/Meeting`、`api/v1/Meeting`（`GET {id}`／`POST search`／`POST`／`PUT {id}`／`DELETE {id}`） |
| 選單路徑 | 會議管理（id=6）> 會議紀錄（id=61，`url=/meetings`，icon `mic`；群組 icon `event`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[6] = 角色_會議管理`、`[61] = 角色_會議紀錄` |
| UI 頁面權限 | 頁面鍵「會議紀錄」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `會議紀錄:view` / `會議紀錄:create` / `會議紀錄:edit` / `會議紀錄:delete` |
| 主要使用者 | 具「會議紀錄」角色權限的後台使用者；系統管理員無條件可存取 |

選單以 `id` 對應權限鍵，重排 `Menu.json` 不會錯位。權限鍵常數另需登記於 `RolePermissionService` 的「會議管理」群組，才會出現在角色權限矩陣。

## 三、畫面與欄位

單頁清單 + Modal 表單（`MeetingViewView`）：

- 搜尋：關鍵字比對 `Title`、`Description` 或 `MediaOriginalFileName`（`Contains`）。清空搜尋鈕在有輸入時出現。
- 工具列：新增、重新整理、關鍵字、清空搜尋、搜尋。**0.4.35 移除分類過濾與團隊過濾**。
- 排序：可排序欄位 `Title`、`MeetingDate`、`TranscriptionStatus`、`CreatedAt`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：會議標題、會議日期、影音檔（檔名＋大小，未上傳顯示「尚未上傳」）、轉錄狀態（彩色標籤；進行中附即時百分比，Tooltip 顯示目前段數；失敗時附 ⚠ 並以 Tooltip 顯示錯誤訊息）、更新時間、操作。**0.4.35 移除分類與團隊兩欄**。
- **轉錄進度通知器**（0.4.36）：右下角常駐面板（形式比照雲端硬碟的上傳進度），顯示每筆轉錄的階段與百分比，完成打勾、失敗顯示錯誤；掛在 `MainLayout`，切到其他頁面也看得到。轉錄結束時清單會自動重新載入，狀態欄自動翻成已完成／失敗，不必手動按 🔄。進度只存在記憶體（Singleton），**沒有資料庫欄位**。**0.4.55 起進行中的項目多一顆「取消」**（走 `IJobCancellationRegistry`，排隊中與執行中都能停），同時修正既有誤導——關閉鈕在工作進行中的文案改為「關閉通知（工作會繼續執行）」，先前它讀起來像取消，實際上只是關掉通知、轉錄照跑照計費。
- 新增／編輯表單欄位：
  - 會議標題 `Title`（必填，最長 200）
  - 會議日期 `MeetingDate`（選填，`DatePicker`）。**0.4.35 起：留空時於影音檔上傳成功當下自動帶入上傳當天**（`MeetingService.SaveMediaAsync` 以 `??=` 補值，已填的不覆蓋），要更正仍可從畫面編輯
  - 描述 `Description`（選填，最長 2000，3 列 `TextArea`）
  - ~~分類 `Categories`／團隊 `Teams`~~ —— **0.4.35 已從表單移除**，改由專案項目頁負責歸屬與分類。資料庫欄位與服務層權限判斷都保留，詳見下方「0.4.35 的權限副作用」
  - 影音檔（`<InputFile>` 單檔，`accept` 由 `MeetingMediaPolicy.AcceptAttribute` 產生）
- **Modal 版面**（0.4.28，0.4.69 改為全站共用機制）：`.meeting-view-modal` 近滿版——寬 `96vw`、`top: 2vh`、內容高 `96vh`，`ant-modal-body` 自行滾動，外層頁面與遮罩不出現滾動軸。表單以兩欄 grid 排列：會議標題／會議日期一列，描述與影音檔以 `.form-modal-full` 佔滿整列；視窗寬度 ≤768px 退回單欄（0.4.35 移除分類／團隊該列）。**0.4.69 起兩欄 grid 改用全站共用的 `.form-modal-grid` / `.form-modal-full`**（原本的 `.meeting-view-form-grid` / `.meeting-view-form-full` 已刪除），尺寸級別與分欄原則見 [開發慣例與限制速查 §6.5](../architecture/開發慣例與限制速查.md)。樣式一律寫在 `FormModalHelper.razor` 的全域 `<style>`——Blazor CSS 隔離的 `[b-xxxxx]` 屬性套不到由 `Modal` 元件自己渲染的外框元素。
- **上傳進度列**：儲存後開始複製檔案，Modal 內以 AntDesign `Progress` 顯示 0-100%；上傳期間 Modal 的確定鈕轉為 loading、取消鈕與移除鈕失效，避免中途關閉。
- 操作按鈕：
  - 預覽逐字稿（狀態為「已完成」且有逐字稿檔案時才出現）
  - 重新轉錄（有影音檔且狀態非「待處理」「處理中」時出現，受 `edit` 權限控制；**0.4.65 起「已取消」也會出現**——取消不保留進度，只能整個重跑，先前這個狀態沒有出口）。**按下去會先跳費用確認對話框**，文案依狀態分流，已完成才套紅色確認鈕（會刪掉現有逐字稿）
  - 修改（`edit`）、刪除（`delete`）
- 鍵盤行為：Esc 關閉 Modal。**與提示詞頁一致，Enter 不送出表單**——描述為多行輸入，Enter 必須留給換行。
- 刪除：`ConfirmAsync` 二次確認，明確告知影音檔與逐字稿會一併刪除且不可復原。
- 逐字稿預覽與編修（0.4.55）：另一個 Modal，內容由服務層直接讀檔回傳字串（**不開下載端點**，避免多一個檔案輸出的授權面）。0.4.55 起由唯讀 `<pre>` 改為可編輯的 `TextArea` ＋「儲存」，供人工修正 STT 聽錯的人名與專有名詞；儲存為**就地覆寫**（`MeetingFileStore.OverwriteTranscriptAsync`），不留版本歷程。**只有轉錄狀態為「已完成」時才允許儲存**——重新轉錄進行中存回去會被新逐字稿蓋掉，服務層會當場擋下並說明原因。另注意讀取時會經 `TranscriptionNoiseFilter` 濾掉供應商外漏的系統指令，因此使用者存回的是**過濾後**的內容，等於順手把那段雜訊從檔案永久清掉。

### 轉錄狀態

| 狀態（enum 值）| 意義 | 進入條件 |
| --- | --- | --- |
| 未上傳（0）| 尚未掛上影音檔 | 新建立的會議紀錄 |
| 待處理（1）| 已落檔，等待背景 worker | 上傳成功後、或按下「重新轉錄」 |
| 處理中（2）| 背景轉錄執行中 | worker 取件後 |
| 已完成（3）| 逐字稿已產生 | 全部分段轉錄完成並寫檔成功 |
| 失敗（4）| 轉錄中止 | 轉檔或 API 失敗；或應用程式重啟中斷 |
| 已取消（5）| 使用者主動取消 | 排隊中或執行中按下進度面板的「取消」（0.4.55）。**刻意與失敗分開**，並以中性灰顯示——把主動取消畫成紅色的「失敗」是說謊 |

## 四、內部系統運作

- UI 路徑：`MeetingViewView` →（注入）`MeetingService` → `BackendDBContext` / `MeetingFileStore` / `ITranscriptionQueue`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`MeetingController` → `MeetingRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Meeting`（DbSet 為 `context.Meeting`）。**刻意不叫 `MeetingRecord`**——會與根命名空間 `MeetingRecord` 衝突；`Meeting` 也與 0.4.24 移除前的舊表同名。
  因為「一筆會議只有一個影音檔」，媒體欄位直接內嵌，**不另開附件子表**，省掉 Cascade 與附件集合的整套機制。
- 標籤欄位 `Categories`／`Teams` 以 `TagStringHelper` 的「換行包夾」格式儲存；AutoMapper 以 `ForMember` 搭配 `ToList`／`ToStored` 轉換。
- 查詢一律 `AsNoTracking()`；寫入前後以 `CleanTrackingHelper.Clean<Meeting>` 清追蹤。
- 編輯前於 UI 以 `CurrentRecord = model.Clone()` 複製；`Clone()` 為淺複製後另建 `Categories`／`Teams` 新清單。
- **`UpdateAsync` 一律沿用資料庫既有的媒體與轉錄欄位**，只寫回標題／日期／描述／標籤。使用者開著 Modal 時背景轉錄若剛好完成，畫面上的舊複本不會把新狀態蓋掉；`MeetingRepository.UpdateAsync` 對 API 路徑做同樣保護。

### 檔案存放

| 內容 | 設定欄位（`SystemSettings.ExternalFileSystem`）| 路徑格式 |
| --- | --- | --- |
| 影音檔（原始上傳檔）| `MeetingMediaPath` | `{年}/{月}/{GUID}{原副檔名}` |
| 逐字稿 | `MeetingTranscriptPath` | `{年}/{月}/{GUID}.txt` |

年／月取自**主表的 `CreatedAt`**，與專案附件一致（見 [檔案上傳機制](../features/檔案上傳機制.md)）。逐字稿以 **UTF-8 含 BOM** 寫入，避免使用者用記事本開啟時出現亂碼。實體檔案的建立與刪除集中在 `MeetingFileStore`。

### 允收政策

`MeetingMediaPolicy`（前端與服務層共用同一份判斷）：

- 單檔上限 **1GB**。
- 副檔名白名單：`.mp3`、`.wma`、`.wav`、`.m4a`、`.aac`、`.flac`、`.ogg`、`.opus`、`.amr`、`.mp4`、`.m4v`、`.mov`、`.avi`、`.wmv`、`.mkv`、`.webm`。
- 白名單只需涵蓋「FFmpeg 讀得懂」的格式，**不受轉錄 API 的格式限制約束**——所有格式送出前都已轉成 mp3。

### 轉錄管線

1. `MeetingFileStore.SaveMediaAsync` 以 80KB 緩衝區逐段複製串流，每當百分比變動就回報一次 `IProgress<int>`（進度列的資料來源）。
2. 落檔成功 → 狀態設為「待處理」→ `ITranscriptionQueue.EnqueueAsync(meetingId)`。
3. `TranscriptionBackgroundService`（單一 worker）取件，為每筆工作建立獨立 DI scope 執行 `TranscriptionJobRunner`。
4. `FfmpegMediaConverter` 以外部 FFmpeg 執行檔轉檔：
   `-vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 32k -f segment -segment_time 900 -segment_format mp3`
   輸出到系統暫存目錄，轉錄結束即整個目錄刪除。
5. `AzureOpenAiTranscriptionProvider` 逐段 `POST {Endpoint}/openai/deployments/{TranscriptionModel}/audio/transcriptions?api-version={...}`（`api-key` 標頭、`multipart/form-data`、`response_format=text`）。
6. 各段文字以空行接合寫入逐字稿檔，狀態轉為「已完成」；重跑時**新檔寫入成功後才刪舊檔**。

> **為什麼一律切段**：Azure OpenAI 的音訊轉錄有單檔 25MB 上限，而上傳端允許到 1GB。固定切段讓長會議與短錄音走同一條路徑，不必為「檔案夠小就不切」多維護一個分支；15 分鐘 @32kbps 約 3.6MB，遠低於上限，同時順帶解決長會議的時長問題。

### 背景佇列與失敗語意

- `ITranscriptionQueue` 是行程內 `Channel<int>`（Singleton），**不做持久化**——本專案是單一實例假設（SQLite 檔案資料庫 + 本機磁碟儲存）。
- 單一 worker：轉錄同時受外部 API 速率限制與 FFmpeg 的 CPU 佔用影響，併發只會互相拖慢。
- 應用程式重啟時，`Program.cs` 的啟動修復區塊會把殘留在「處理中」的紀錄一律改為「失敗」（訊息「應用程式重啟導致轉錄中斷，請重新執行轉錄。」），避免永久卡住。
- 停留在「待處理」的紀錄重啟後不會自動重跑，由使用者按「重新轉錄」重新入列。
- `TranscriptionJobRunner` 全程 try/catch，失敗寫入 `TranscriptionError` 供畫面顯示；背景服務再加一層保險，單筆例外不會讓整個 worker 停掉。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`會議紀錄:view`、`會議紀錄:create`、`會議紀錄:edit`、`會議紀錄:delete`。裸鍵「會議紀錄」代表該頁全部動作（向後相容）。
- **上傳影音檔與重新轉錄歸在 `edit`**，不新增動作類型。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路。
- **團隊列級權控只在 Blazor Service 層生效**：非管理員於 `MeetingService` 以 `TagStringHelper.BuildTeamAccessPredicate` 只能看到公開（無團隊）或與自身有效團隊有交集的會議；單筆讀取、上傳影音檔、重新轉錄、逐字稿預覽皆以 `TagStringHelper.IsTeamAccessible` 守門。**Web API 的 repository 路徑不做列級過濾**，與 `ProjectController`／`PromptTemplateController` 一致（見 [開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §4.1）。
- 逐字稿內容為高敏感資料：預覽走 Blazor 服務層（Cookie 驗證 + 團隊守門），**沒有任何可直接下載檔案的 HTTP 端點**。

### ⚠️ 0.4.35 的權限副作用（刻意為之，非 bug）

0.4.35 依使用者要求把「分類／團隊」從清單欄位、工具列過濾與新增/編輯表單**全部移除**，理由是會議紀錄頁只負責「上傳音檔 → 產出逐字稿」，歸屬與分類改到專案項目頁處理。

副作用是 **`Teams` 是會議唯一的列權限來源**（沒有 owner 欄位，`ProjectId` 可為空無法替代），而 `TagStringHelper.ToStored([])` 回傳 `null`、predicate 把 `null` 視為公開，因此：

- **0.4.35 之後新建的會議一律是公開的**，任何有「會議紀錄」頁權限的人都看得到，**包含逐字稿預覽**。
- 0.4.35 之前已標團隊的舊資料**維持原本的可見範圍**——DB 欄位與服務層 11 處權限判斷都沒有動。

要把團隊控管收回來，只需要把表單那個團隊 `Select` 加回 `MeetingViewView.razor`，服務層不必改。
- 新增權限鍵後，掛「預設角色」的使用者需重啟一次應用程式才會生效；掛自訂角色者需由管理員到 `/roleviews` 手動勾選。
- 會議逐字稿會外送第三方 LLM 供應商，導入前應確認資料處理、留存與跨境政策符合組織要求。

## 六、錯誤與邊界

- 不支援的副檔名／超過 1GB：前端選檔時即擋下並通知，`MeetingService.SaveMediaAsync` 再擋一次（前端可被繞過）。
- 主資料已存檔但上傳失敗：Modal **不關閉**，清單先重新整理，使用者可直接重試；不會產生「有紀錄沒檔案」以外的中間狀態。
- 替換影音檔：新檔登錄成功後才刪除舊影音檔與舊逐字稿；狀態重設為「待處理」，`TranscriptRelativePath` 清空。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的會議紀錄」；API 回 404 NotFound。
- 刪除時實體檔案已遺失：只寫 `Warning` 日誌，不阻斷資料列刪除。
- 未設定 `MediaSettings:FfmpegPath` 或路徑錯誤：0.4.29 起在**啟動時**就會被指出（Production 中止啟動、其他環境記 WARN）。若仍在此狀態下執行轉錄，狀態轉「失敗」，錯誤訊息明確指出設定鍵。
- 未設定轉錄供應商或 `TranscriptionModel`：`TranscriptionJobRunner` 擲出並記錄「尚未設定語音轉錄供應商」。
- 找不到對應的 `ITranscriptionProvider` 實作：錯誤訊息列出目前支援的供應商名稱。
- 轉錄 API 非 2xx：錯誤訊息帶入狀態碼與回應內容前 500 字。
- 來源檔沒有音軌：FFmpeg 不會產生任何分段，回「FFmpeg 未產生任何音訊分段，請確認來源檔是否含有可用的音軌。」
- 對「處理中」或「待處理」的紀錄按重新轉錄：拒絕並提示已排入轉錄（**0.4.65 起也擋「待處理」**——只擋「處理中」會讓連按兩下入列兩次、跑兩趟並重複計費）。
- 對沒有影音檔的紀錄按重新轉錄：拒絕並提示尚未上傳。
- 非管理員且無任何有效團隊：僅能看到公開（無團隊）的會議紀錄。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service 以 try/catch 記錄並回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

### 已知限制

- 1GB 檔案經 Blazor Server 的 SignalR 傳輸相當慢，進度列會如實反映；這是 Blazor Server 的架構天花板，不在本版處理範圍。
- 清單上的轉錄狀態**不會自動更新**，需要按「重新整理」才會看到背景轉錄的最新進度。
- `gpt-4o-transcribe` 需要比文字生成更新的 API 版本，因此 `TranscriptionApiVersion` 獨立於 `ApiVersion`；實際可用版本以目標 Azure 資源為準。

## 七、設定

見 [日誌與設定檔說明](../operations/日誌與設定檔說明.md)。摘要：

```json
"MediaSettings": {
  "FfmpegPath": "ffmpeg"
},
"LlmSettings": {
  "DefaultProvider": "AzureOpenAI",
  "TranscriptionProvider": "AzureOpenAI",
  "Providers": {
    "AzureOpenAI": {
      "Endpoint": "https://your-resource.openai.azure.com/",
      "ApiKey": "DevelopmentOnly-ChangeThisLlmApiKey",
      "Model": "gpt-4o-mini",
      "ApiVersion": "2024-10-21",
      "TranscriptionModel": "gpt-4o-transcribe",
      "TranscriptionApiVersion": "2025-03-01-preview"
    }
  }
}
```

- `TranscriptionProvider` 留空 → 沿用 `DefaultProvider`（轉錄端與生成端可指向不同廠商）。
- `TranscriptionApiVersion` 留空 → 沿用 `ApiVersion`。
- **只設 `DefaultProvider`、不填 `TranscriptionModel` 的部署視為未啟用轉錄**，啟動驗證不會擋。
- `FfmpegPath` 可填完整路徑，也可只填 `ffmpeg` 走 PATH（0.4.29 起為預設值）。啟動時會驗證存在性：Production 找不到就中止，其他環境記 WARN 後照常啟動。
- 服務一律以 `IOptions<LlmSettings>` / `IOptions<MediaSettings>` 取設定，**禁止**直接讀 `IConfiguration`。

### 未來支援其他 AI 廠商

擴充點是 `ITranscriptionProvider`（`ProviderName` + `TranscribeAsync`）。新增廠商只需要：

1. 新增一個實作類別並在 `AddTranscriptionServices` 註冊為 `ITranscriptionProvider`。
2. 在 `LlmSettings:Providers` 加一組設定，把 `LlmSettings:TranscriptionProvider` 指向它。

`TranscriptionJobRunner` 由 `IEnumerable<ITranscriptionProvider>` 依名稱挑選，既有程式不需變動。

## 八、驗收與測試

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/MeetingServiceTests.cs`：

- `AddAsync_ShouldPersistFieldsAndTagStrings`：新增後保留欄位，且標籤確實轉為 `TagStringHelper` 儲存格式（漏接轉換器會使團隊權控全面失效）。
- `AddAsync_ShouldWriteBackGeneratedId`：新增後回填 Id（UI 靠這個把影音檔掛上去）。
- `AddAsync_ShouldStartWithNotUploadedStatus`：初始狀態為「未上傳」。
- `GetAsync_ById_ShouldRoundTripTagsToList`：儲存字串可還原為標籤清單。
- `UpdateAsync_ShouldReplaceTagsAndKeepCreatedAt`：更新換掉標籤、保留 `CreatedAt`、推進 `UpdatedAt`。
- `UpdateAsync_ShouldNotOverwriteMediaAndTranscriptionFields`：畫面舊複本不得蓋掉背景轉錄寫入的狀態。
- `DeleteAsync_ShouldRemoveRecordAndPhysicalFiles`：刪除連同影音檔與逐字稿實體檔案。
- `DeleteAsync_ShouldSucceed_WhenPhysicalFilesAreAlreadyGone`：實體檔案遺失時資料列仍刪得掉。
- `SaveMediaAsync_ShouldStoreFileMarkPendingAndEnqueue`：落檔、標記待處理、入列，並回報 100% 進度。
- `SaveMediaAsync_ShouldRejectUnsupportedExtension`／`SaveMediaAsync_ShouldRejectOversizedFile`：允收政策在服務層生效。
- `SaveMediaAsync_ShouldReplacePreviousMediaAndTranscript`：替換檔案時清掉舊檔與舊逐字稿。
- `SaveMediaAsync_NonAdmin_ShouldDenyRecordOutsideTeamScope`：越權上傳被拒。
- `RequeueTranscriptionAsync_*`：重設狀態並入列；沒有影音檔或處理中時拒絕。
- `ReadTranscriptAsync_ShouldReturnFileContent`／`ReadTranscriptAsync_NonAdmin_ShouldDenyRecordOutsideTeamScope`：預覽內容與越權守門。
- `GetAsync_Admin_ShouldSeeAllRecords`、`GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords`、`GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords`、`GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope`、`GetAsync_WithTeamFilter_ShouldFilterByTeam`、`GetAsync_WithKeyword_ShouldMatchMediaFileName`：團隊可見性與查詢條件。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/TranscriptionRequestTests.cs`（只測純函式，**不打真實 API、不跑真實 ffmpeg**）：

- `BuildRequestUri_*`：Azure OpenAI 轉錄端點組裝、無雙斜線、必填欄位空白時擲出。
- `EffectiveTranscriptionApiVersion_*`：轉錄 API 版本的優先序與回退。
- `BuildSegmentArguments_*`：FFmpeg 參數含 `-vn`／單聲道／16kHz／libmp3lame／32kbps、固定使用 segment muxer、路徑加引號。
- `IsAllowedFileName_*`、`MaxUploadFileSize_ShouldBeOneGigabyte`：允收政策。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/MeetingRegistrationTests.cs`（守住宣告式權限註冊三件組）：

- `RolePermissionCatalog_ShouldContainMeetingPage`、`RolePermissionCatalog_ShouldPlaceMeetingUnderMeetingManagementGroup`：權限鍵登記於「會議管理」群組。
- `MenuJson_ShouldContainMeetingGroupNode`、`MenuJson_ShouldContainMeetingNode`：`Menu.json` 有 id=6 群組與 id=61 子項、名稱與 url 正確。

`src/MeetingRecord/MeetingRecord.Tests/MenuIconTests.cs` 的 `AllowedIcons` 已加入 `event` 與 `mic`；未加入會使整套測試失敗。

轉錄設定另有 `LlmSettingsTests.cs`（`TranscriptionProvider` / `TranscriptionModel` / `TranscriptionApiVersion` 的回退與驗證）與 `StartupSafetyValidatorTests.cs`（Production 對轉錄金鑰、`TranscriptionModel`、`FfmpegPath` 的檢查）。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，檔案操作導向系統暫存目錄下的隨機資料夾，轉錄佇列以假的 `ITranscriptionQueue` 注入。注意 `EnsureCreatedAsync` 直接由模型建表、**繞過 migration**，所以測試全綠不代表 migration 存在。

### 手動驗收（需先安裝 FFmpeg 並填好 Azure 金鑰）

1. 啟動 → 確認 `MeetingMediaPath` / `MeetingTranscriptPath` 自動建立。
2. 登入 → 側邊欄出現「會議管理 > 會議紀錄」→ 進 `/meetings`。
3. 新增會議並上傳 mp3：進度列 0%→100%，狀態依序為「待處理」→「處理中」→「已完成」。
4. 點「預覽逐字稿」，中文內容正常無亂碼。
5. 上傳 mp4（含影像軌）與 wma，確認 FFmpeg 抽音軌成功、同樣產出逐字稿。
6. 故意填錯 `ApiKey` → 狀態「失敗」並可看到錯誤訊息 → 按「重新轉錄」可重跑。
7. 轉錄進行中重啟應用程式 → 該筆自動變成「失敗」（不卡在「處理中」）。
8. 刪除該筆 → 資料列消失，且影音檔與逐字稿實體檔一併移除。
9. 以非管理員帳號驗證團隊可見性（公開／團隊交集／無團隊）。

## 九、規劃中需求

以下**尚未實作**，不屬於 0.4.27 驗收範圍：

- 套用提示詞範本 → 呼叫 LLM 產生會議紀錄草稿（見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)）。
- 逐字稿編修的版本歷程與還原（0.4.55 起可編修，但為就地覆寫）。
- 影音檔／逐字稿的下載端點與線上播放。
- 清單轉錄狀態的自動輪詢或即時推播。
- 逐字稿保留期限與自動清理政策。
- 轉錄成本上限、每日配額與失敗重試計費控管。

## 十、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Meetings/MeetingPage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Meetings/MeetingViewView.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Meetings/MeetingViewView.razor.cs:1`（頁面權限、上傳進度、逐字稿預覽）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/MeetingController.cs:1`（`[HasPermission]` 動作鍵）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/MeetingService.cs:1`（CRUD、團隊權控、上傳與重新轉錄）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/MeetingFileStore.cs:1`（實體檔案存取與上傳進度回報）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/ITranscriptionProvider.cs:1`（換廠商的擴充點）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/AzureOpenAiTranscriptionProvider.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/FfmpegMediaConverter.cs:1`（轉檔與切段）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/TranscriptionJobRunner.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/TranscriptionQueue.cs:1`
- `src/MeetingRecord/MeetingRecord.Web/BackgroundServices/TranscriptionBackgroundService.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Helpers/MeetingMediaPolicy.cs:1`（允收政策）
- `src/MeetingRecord/MeetingRecord.Business/Repositories/MeetingRepository.cs:1`（API 路徑，不做列級過濾）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/Meeting.cs:1`、`src/MeetingRecord/MeetingRecord.AccessDatas/BackendDBContext.cs:1`（DbSet）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Migrations/20260821090356_AddMeeting.cs:1`
- `src/MeetingRecord/MeetingRecord.Share/Enums/TranscriptionStatus.cs:1`
- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs:1`、`MediaSettings.cs:1`、`MeetingMediaUploadInput.cs:1`
- `src/MeetingRecord/MeetingRecord.Models/AdapterModel/MeetingAdapterModel.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Models/AutoMapping.cs:1`（標籤欄位轉換）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs:1`、`PermissionKeys.cs:1`
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs:1`（權限矩陣登記）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs:1`、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json:1`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs:1`（`AddApplicationServices`／`AddTranscriptionServices`／`AddConfiguredOptions`）
- `src/MeetingRecord/MeetingRecord.Web/Program.cs:1`（目錄準備、Options 綁定、啟動時的轉錄狀態修復）
- `src/MeetingRecord/MeetingRecord.Web/Configuration/StartupSafetyValidator.cs:1`（Production 檢查）
- 交叉連結：[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)、[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)、[../features/檔案上傳機制.md](../features/檔案上傳機制.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../architecture/Web API 端點目錄.md](../architecture/Web%20API%20端點目錄.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)、[../operations/正式部署與安全檢查清單.md](../operations/正式部署與安全檢查清單.md)
