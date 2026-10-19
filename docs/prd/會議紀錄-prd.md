# 會議紀錄 PRD

- 文件版本：2.15
- 文件狀態：已實作
- 現行系統版本：1.0.47
- 首次實作版本：0.4.27
- 最後核對日期：2026/10/19

> **0.4.103：舊團隊標籤整組刪除。** `Meeting.Teams` 欄位已從資料庫刪除（migration `RemoveLegacyTeamTags`，既有標籤資料一併刪除），連同 `MeetingAdapterModel.Teams`／`TeamsText`（含 `Clone()` 那行）、AutoMapper 的 `Teams` 對應、`MeetingCreateUpdateDto.Teams`（API 舊客戶端送 `teams` 會被忽略）、`DataRequest.TeamFilters` 與 `MeetingService` 的團隊過濾。下文提到 `Teams` 的段落都是歷史紀錄。
>
> **0.4.102：分類加回來（團隊沒有）。** 工具列恢復「分類」過濾、清單恢復「分類」欄、編輯表單恢復「分類」多選（啟用中的分類，以名稱存進 `Meeting.Categories`）。分類＝「什麼資料」，只用來篩選，**不影響誰看得到**；團隊欄位**刻意不放回會議頁**，可見性仍跟著專案走（已歸屬的看專案的主責＋協作團隊）。
>
> **0.4.99：會議改跟著專案走，團隊標籤不再作用。** 已歸屬專案的會議跟著專案的可見範圍（0.4.102 起看專案的主責＋協作團隊；0.4.101 看專案的資料群組；0.4.99～0.4.100 看專案成員）；**未歸屬的只有上傳者（新欄位 `Meeting.CreatedByUserId`）與管理者看得到**。`MeetingService` 原本 11 處團隊檢查全部換成 `ProjectAccess.CanViewMeeting`，另補上原本沒有檢查的 `UpdateAsync`／`DeleteAsync`；歸屬到專案、產生會議紀錄時，目標專案也要看得到。提示詞範本不再檢查團隊。API 的 `MeetingRepository` 套同一條規則，並修掉「API 更新一次就把 `ProjectId` 清成 null」的舊問題。第五節已改寫為現行規則，舊團隊權控只留歷史註記；「把團隊守門補進 `AiChatService`」這條規劃已由專案權控完成。

## 一、目標與範圍

提供「會議紀錄」的維護能力：使用者在 `/meetings` 建立會議紀錄、上傳**一個**影音檔（音訊或視訊），系統以 FFmpeg 將其轉為 mp3 並切段後，逐段送 Azure OpenAI 的語音轉錄 API，串接成完整逐字稿存進檔案系統，並可在清單上直接預覽。

```
建立會議紀錄 ─► 上傳影音檔（≤1GB）─► 背景佇列
                                        └─► FFmpeg 抽音軌／轉 mp3／切 15 分鐘分段
                                              └─► 逐段 Azure OpenAI 轉錄 ─► 串接 ─► 逐字稿 .txt ─► 畫面預覽
```

**影音檔與逐字稿一律存放於檔案系統，不入資料庫**；資料表只保存相對路徑等中繼資料。存放根目錄與 AI 供應商（provider／endpoint／key／model）皆由 `appsettings.json` 決定。

非範圍（本版刻意不做）：
- 本頁**自 0.4.73 起也包含**「套用提示詞 → LLM 產生會議紀錄」：每列有「AI 轉會議紀錄」，對話框裡的「所屬專案」是**選填**，不必先建立專案。專案頁 `/projects` 的入口同時保留（見 [專案項目 PRD](專案項目-prd.md)），兩者共用同一支服務；流程全貌見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)，提示詞範本維護見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)。0.4.73 起本頁涵蓋整條動線：上傳、轉錄，以及產生／檢視／編修／匯出會議紀錄、AI 問答與歸屬到專案。**0.4.82 起逐字稿的預覽與編修整個移除**（檔案仍在，只是人看不到），且編修與匯出改依「有沒有歸屬專案」分流，見下方「操作按鈕」。**1.0.40 起恢復逐字稿的唯讀檢視、複製與下載 .txt**（編修仍不提供）。
- 不做即時（會議進行中）轉錄與逐字稿串流。
- 不做說話者聲紋辨識與身分綁定。
- **不做逐字稿的編修**（0.4.55 曾提供，0.4.82 移除）。逐字稿檔案仍是生成會議紀錄與 AI 問答的輸入。0.4.82～1.0.39 連唯讀預覽都沒有；**1.0.40 起「會議紀錄與逐字稿」視窗有唯讀的「逐字稿」頁籤**，可複製全文、下載 .txt（使用者要拿去外部 AI 工具用），但仍不能改。
- 不做影音檔／逐字稿的 HTTP 下載端點與線上播放（1.0.40 的下載 .txt 是 Blazor 頁面經服務層讀檔後交給瀏覽器存檔，不是對外網址）。
- 不做一筆會議掛多個影音檔（一對一，替換即覆寫）。
- Web API 只開放中繼資料 CRUD，不開放上傳、轉錄與逐字稿讀取。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/meetings`（`MeetingPage.razor`，`MainLayout`） |
| REST API | `api/Meeting`、`api/v1/Meeting`（`GET {id}`／`POST search`／`POST`／`PUT {id}`／`DELETE {id}`） |
| 選單路徑 | 側邊欄「核心功能」區的頂層項目「會議紀錄」（id=61，`url=/meetings`，icon `mic`；0.4.33 起不再有「會議管理」群組） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[61] = 角色_會議紀錄` |
| UI 頁面權限 | 頁面鍵「會議紀錄」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `會議紀錄:view` / `會議紀錄:create` / `會議紀錄:edit` / `會議紀錄:delete` |
| 主要使用者 | 具「會議紀錄」角色權限的後台使用者；系統管理員無條件可存取 |

選單以 `id` 對應權限鍵，重排 `Menu.json` 不會錯位。權限鍵常數另需登記於 `RolePermissionService` 的「會議管理」群組，才會出現在角色權限矩陣。

## 三、畫面與欄位

單頁清單 + Modal 表單（`MeetingViewView`）：

- 搜尋：關鍵字比對 `Title`、`Description` 或 `MediaOriginalFileName`（`Contains`）。清空搜尋鈕在有輸入時出現。
- 工具列：新增、重新整理、**分類過濾（0.4.102 恢復，多選，含任一分類即符合）**、關鍵字、清空搜尋、搜尋。**0.4.35 移除分類過濾與團隊過濾**，團隊過濾沒有恢復。
- 排序：可排序欄位 `Title`、`MeetingDate`、`TranscriptionStatus`、`CreatedAt`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：會議標題（**0.4.73 起第二行顯示所屬專案**，未歸屬顯示灰字「— 未歸屬」；刻意不另開一欄，因為操作欄最壞會有 11 顆圖示）、會議日期、影音檔（檔名＋大小，未上傳顯示「尚未上傳」）、轉錄狀態（彩色標籤；進行中附即時百分比，Tooltip 顯示目前段數；失敗時附 ⚠ 並以 Tooltip 顯示錯誤訊息）、**會議紀錄狀態（0.4.73 新增，含即時生成百分比與失敗 ⚠；不可排序，因為服務層的排序白名單沒有 DraftStatus）**、**分類（0.4.102 恢復）**、更新時間、操作。**0.4.35 移除分類與團隊兩欄**，團隊欄沒有恢復。
- **轉錄進度通知器**（0.4.36）：右下角常駐面板（形式比照雲端硬碟的上傳進度），顯示每筆轉錄的階段與百分比，完成打勾、失敗顯示錯誤；掛在 `MainLayout`，切到其他頁面也看得到。轉錄結束時清單會自動重新載入，狀態欄自動翻成已完成／失敗，不必手動按 🔄。進度只存在記憶體（Singleton），**沒有資料庫欄位**。**0.4.55 起進行中的項目多一顆「取消」**（走 `IJobCancellationRegistry`，排隊中與執行中都能停），同時修正既有誤導——關閉鈕在工作進行中的文案改為「關閉通知（工作會繼續執行）」，先前它讀起來像取消，實際上只是關掉通知、轉錄照跑照計費。**0.4.117 起上傳也進面板**：
  - 通知器新增「上傳中」階段（`TranscriptionPhase.Uploading`）。`MeetingService.SaveMediaAsync` 開始寫檔前呼叫 `StartUpload`，每個百分比呼叫 `ReportUpload`，入列時被 `Enqueued` 覆蓋成排隊中；上傳失敗（含上傳頁籤斷線）標成失敗。
  - 同一個帳號的其他頁籤、瀏覽器、電腦，以及看得到這筆會議的其他人，面板與清單都看得到「上傳中 xx%」。
  - 上傳中**不顯示「取消」**：上傳不在背景佇列裡，`RequestCancel` 會留下待取消標記，入列後那趟轉錄會被默默跳過。要停止上傳只能關掉上傳的那個頁籤。**1.0.37 起上傳中也有「取消」**：`SaveMediaAsync` 寫檔期間以 `BackgroundJobKind.Upload` 登記進 `IJobCancellationRegistry`（寫完即撤銷），面板依階段用 Upload 或 Transcription 種類取消，不會再誤殺入列後的轉錄。
  - 同一筆會議已經在別的視窗上傳中時，再上傳會被拒絕。
  - ⚠️ **0.4.84～1.0.24 面板其實沒有掛在 `MainLayout` 上**（改寫註解時誤刪，連同 `blazor-error-ui`），這段期間整站都看不到它；1.0.25 補回，並以 `LayoutMountTests` 釘住。
- 新增／編輯表單欄位：
  - 會議標題 `Title`（必填，最長 200）
  - 會議日期 `MeetingDate`（選填，`DatePicker`）。**0.4.35 起：留空時於影音檔上傳成功當下自動帶入上傳當天**（`MeetingService.SaveMediaAsync` 以 `??=` 補值，已填的不覆蓋），要更正仍可從畫面編輯
  - 描述 `Description`（選填，最長 2000，3 列 `TextArea`）
  - **分類 `Categories`**（0.4.102 恢復）：多選啟用中的分類，跨整列（`.form-modal-full`），只描述資料、不影響可見性。
  - ~~團隊 `Teams`~~ —— **0.4.35 已從表單移除**且不再恢復，可見性跟著專案走。資料庫欄位 **0.4.103 已刪除**；當時的影響（0.4.35～0.4.98 新建會議一律公開）見第五節的歷史註記
  - 影音檔（單檔，**0.4.77 起改用共用的 `FileDropZone`，可拖拉或點擊**；`accept` 由 `MeetingMediaPolicy.AcceptAttribute` 產生，⚠️ 但 `accept` 對「拖入」不可信，副檔名把關以服務層的 `IsAllowedFileName` 為準）
- **Modal 版面**（0.4.28，0.4.69 改為全站共用機制）：`.meeting-view-modal` 近滿版——寬 `96vw`、`top: 2vh`、內容高 `96vh`，`ant-modal-body` 自行滾動，外層頁面與遮罩不出現滾動軸。表單以兩欄 grid 排列：會議標題／會議日期一列，描述與影音檔以 `.form-modal-full` 佔滿整列；視窗寬度 ≤768px 退回單欄（0.4.35 移除分類／團隊該列）。**0.4.69 起兩欄 grid 改用全站共用的 `.form-modal-grid` / `.form-modal-full`**（原本的 `.meeting-view-form-grid` / `.meeting-view-form-full` 已刪除），尺寸級別與分欄原則見 [開發慣例與限制速查 §6.5](../architecture/開發慣例與限制速查.md)。樣式一律寫在 `FormModalHelper.razor` 的全域 `<style>`——Blazor CSS 隔離的 `[b-xxxxx]` 屬性套不到由 `Modal` 元件自己渲染的外框元素。
- **上傳進度列**：儲存後開始複製檔案，Modal 內以 AntDesign `Progress` 顯示 0-100%；上傳期間 Modal 的確定鈕轉為 loading、取消鈕與移除鈕失效，避免中途關閉。**0.4.116 起**主資料存好、開始上傳前，先把 Modal 留住並重新整理清單：先前 AntDesign 在確定鈕按下後就把 Modal 關掉，進度列跟著看不見，清單也要等上傳完才出現新會議，使用者只看到「新增成功」與一片沒變的畫面。**1.0.25 起**存檔到上傳結束前整張表單鎖住：欄位、移除檔案、取消鈕、右上角關閉、Esc 都不能用，唯一能按的是「取消上傳」（先確認）。主資料在上傳之前就存了，先前上傳期間還能改欄位，改的內容不會再存，標題改了也會默默消失。取消後服務層刪掉已傳的部分，會議資料保留，進度面板標成「已取消上傳。」；取消只作用在寫檔，寫完檔後的存檔與入列不受影響，避免留下已存檔卻沒入列的會議。不提供暫停：檔案經 Blazor 連線傳送，伺服器超過 1 分鐘沒收到資料就會切斷整條連線。**1.0.27 起不再在視窗裡顯示進度**（使用者要求）：主資料存好就關窗、重新整理清單，上傳進度直接顯示在該列的「轉錄狀態」（徽章「上傳中」＋百分比，來自 `TranscriptionPhase.Uploading`），上傳完接著是轉錄進度。上傳在背景跑（存檔流程不等它結束），**只鎖正在上傳的那一筆**：該列操作欄只剩「取消上傳」（只在發起上傳的頁面出現；其他頁籤的同一列只顯示「上傳中」，判斷依據是共用通知器），其他紀錄、工具列、排序、換頁照常可用。同一頁一次只上傳一個：上傳期間開其他視窗時選檔區停用並說明，開視窗也不會清掉上傳狀態或重建選檔區（背景還在讀那個檔案），上傳結束才重建並自行重載清單。離開頁面不取消上傳。上傳與頁面其他操作共用同一個 DbContext，因 Microsoft.Data.Sqlite 的非同步方法實為同步完成、Blazor 又逐段執行，兩者不會真正交錯；改用真正非同步的資料庫時要重新檢查。上傳失敗或取消時會議資料保留，提示改按「修改」再選檔重傳。**1.0.37 起**：取消上傳時，若這筆是這次存檔才新增的會議，連資料列一起刪（`SaveMediaAsync(deleteMeetingOnCancel: true)`，沿用 `DeleteAsync` 的檔案與 AI 對話清理），面板那一筆直接移除，清單該列消失；修改既有會議（含換檔）時取消仍保留會議與舊檔。上傳失敗（非取消）一律保留。取消結果改看服務層回傳的訊息（`UploadCancelledMessage`／`UploadCancelledAndMeetingDeletedMessage`），從面板取消時發起上傳的頁面也會跳通知；按取消時檔案已寫完（確認框開著時傳完、或已過寫檔階段）會跳「來不及取消」，不再無聲無息或誤報「已排入轉錄」。對話框只是隱藏（AntDesign Modal 預設不 DestroyOnClose），裡面的 InputFile 還在，檔案串流才讀得到。
- 操作按鈕：
  - 重新轉錄（有影音檔且狀態非「待處理」「處理中」時出現，受 `edit` 權限控制，**1.0.47 起只對上傳者與管理者顯示**；**0.4.65 起「已取消」也會出現**——取消不保留進度，只能整個重跑，先前這個狀態沒有出口）。**按下去會先跳費用確認對話框**，文案依狀態分流，已完成才套紅色確認鈕（會刪掉現有逐字稿）
  - **AI 轉會議紀錄（0.4.73，`edit`）**：開對話框選提示詞（必填）與所屬專案（**選填**）；選了專案才能從該專案名冊勾選與會人員。按確定會再跳一次費用確認
  - **會議紀錄與逐字稿（1.0.40，圖示 `article`，`HasDraft || HasTranscript` 時出現）**：取代原本的「檢視會議紀錄」與「編修會議紀錄」兩顆鈕（使用者反映操作欄太擠、兩顆長得像）。一律先以唯讀開啟共用的 `Components/Commons/MarkdownEditorModal`：
    - 有逐字稿時上方有「會議紀錄｜逐字稿」頁籤；還沒有會議紀錄就直接停在逐字稿頁籤（會議紀錄頁籤顯示「尚未產生會議紀錄。」）。逐字稿以純文字顯示（保留換行，不當 Markdown 渲染），附「複製全文」與「下載 .txt」（UTF-8 含 BOM，記事本開啟不亂碼；檔名 `逐字稿_{標題}_{yyyyMMdd}.txt`，日期取 `TranscriptionCompletedAt ?? UpdatedAt`，見 `MeetingDocumentExporter.BuildTranscriptFileName`）。看逐字稿只要看得到這筆會議；複製與下載要 `會議紀錄:export`。逐字稿**仍不能改**。
    - 視窗底部的「編修」只在**未歸屬專案、有 `會議紀錄:edit`、且已有會議紀錄**時出現，按下去在同一個視窗切到左改右看 ＋ 常駐搜尋／取代列；「取消」（改過會先確認）與儲存成功都回到唯讀檢視，不關窗。
    - 已歸屬專案時視窗底部會有一行指路說明，講清楚編修與下載要去專案項目頁——沒有這行，使用者只會覺得按鈕莫名其妙消失了
  - **匯出 PDF（`export`）**：0.4.82 起**不放在清單列上**。未歸屬的在「會議紀錄與逐字稿」視窗裡按「下載 PDF」（表頭「專案」欄顯示「未指定」）；已歸屬的到專案項目頁按，那裡的 PDF 表頭才帶得到專案名稱
  - **AI 問答（0.4.73）**：`HasDraft || HasTranscript` 時出現——⚠️ **不要簡化成只看 `HasDraft`**，「已轉錄完成但還沒生成會議紀錄」的會議會因此失去入口，而那正是「先問問看逐字稿講了什麼再決定要不要花錢生成」最有用的時機。0.4.82～1.0.39 這是使用者接觸逐字稿內容的唯一路徑（1.0.40 起另有唯讀的逐字稿頁籤）。0.4.95 起提問可以附加圖片與檔案（規則與專案層級相同，見 [專案項目 PRD](專案項目-prd.md)）
  - **抽出待辦：0.4.83 起本頁沒有這顆鈕**（0.4.73～0.4.82 曾有）。待辦在資料表層級就必須有專案，而這一頁有一半的會議是未歸屬的，按了只能跳訊息叫使用者先去歸屬——那不是功能，是一顆騙人的按鈕。歸屬過的會議本來就會出現在專案項目頁的清單裡，入口集中在那裡。
  - **歸屬到專案（0.4.73，`edit`，僅未歸屬時出現）**：只寫 `ProjectId`，不重新生成、不產生費用。不掛紅色——歸屬不是破壞性動作
  - **專案選單不列已完成專案（1.0.46）**：「歸屬到專案」與「AI 轉會議紀錄」的專案下拉走 `ProjectService.GetSelectableAsync()`，預設排除狀態「已完成」（封存）的專案；已歸屬到已完成專案的會議，清單上照常顯示原本的專案名稱。規則見 [專案項目 PRD](專案項目-prd.md)。
  - 修改（`edit`）、刪除（**1.0.47 起只對上傳者與管理者顯示**，角色要 `edit`；以前只看 `delete`）
  - 修改視窗（1.0.47）：不是上傳者時影音檔拖放區停用，下方顯示「只有上傳者或管理者可以更換影音檔；其他欄位可以照常修改。」；新增時一律可選檔。
- 鍵盤行為：Esc 關閉 Modal。**0.4.77 起 Enter 送出表單**（先前刻意排除，理由是描述為多行輸入）——判斷走 `FormKeyboardHelper.IsSubmit`，組字中的 Enter 不算送出，**Shift+Enter 在描述欄換行**。
- 刪除：`ConfirmAsync` 二次確認，明確告知影音檔與逐字稿會一併刪除且不可復原。0.4.115 起依結果顯示成功或失敗原因（以前一律顯示「刪除成功」）。**1.0.47 起確認文字**：「確定要刪除這筆會議紀錄嗎？影音檔、逐字稿、會議紀錄與 AI 問答會一併刪除。從這場會議抽出的 N 筆待辦會保留在專案裡（不再連到這場會議）。此操作無法復原。」——N 取自 `MeetingService.CountLinkedTodosAsync`，N 為 0 時不顯示待辦那句。
- **背景工作進行中不能刪、不能換檔**（0.4.115）：轉錄或會議紀錄生成在排隊或執行中時，`DeleteAsync` 與 `SaveMediaAsync` 一律拒絕，提示等它完成或從進度面板取消。原因：工作不會因此停下，跑完會把舊錄音的逐字稿寫到已換新檔的會議上，或寫出沒有資料列可掛、永遠留在磁碟上的逐字稿。轉錄存不進資料庫時刪掉剛寫出的逐字稿，上傳存不進資料庫時刪掉剛落地的影音檔。
- 進度面板的「取消」要有 `會議紀錄:edit`（0.4.115）。只看得到那筆會議的人仍看得到進度，但沒有取消鈕。
- **會議紀錄編修器（0.4.82）**：檢視與編修共用 `Components/Commons/MarkdownEditorModal`，差別只在 `CanEdit`。1.0.40 起同一個視窗先唯讀、按「編修」才切到編修模式；視窗一律 `markdown-editor-modal meeting-view-modal`（寬 96vw、內容固定高 96vh），檢視與編修切換時大小不跳動。
  版面是左邊 `<textarea>`、右邊 Markdown 即時預覽，上方一列常駐的搜尋／取代（搜尋、上一個／下一個、取代、全部取代，顯示「第 n / 共 m 筆」）。
  - 搜尋取代的邏輯全在 `Business/Helpers/TextSearchHelper`（純函式，可單元測試）；JS 只負責 `setSelectionRange` 與捲動。**不做**大小寫選項與整詞比對（中文沒有詞邊界，`\b` 對它無效）。
  - ⚠️ **只反白當前那一筆**。`<textarea>` 裡唯一能做的反白就是 selection；要同時標記全部 m 筆得做逐像素對齊的 overlay，中文字型 fallback 一有差異就整段錯位。用「第 n / 共 m 筆」補足資訊。
  - ⚠️ **刻意不綁 Ctrl+F**。Blazor 的 `@onkeydown:preventDefault` 是編譯期決定的，攔 Ctrl+F 就得對整個容器無條件攔，連右邊預覽區的瀏覽器原生搜尋都會被吃掉。
  - ⚠️ **編修時 `Keyboard="false"`**（Esc 不關窗），改過後按「取消」會先跳確認——一整篇打到一半的會議紀錄，按錯一次鍵就沒了。
  - ⚠️ **文字繫結不 debounce（0ms），只有右邊預覽 debounce 250ms**。搜尋、取代、存檔都必須讀到當下的文字（0.4.52 在 AI 問答上踩過「讀到舊值」）；預覽是 `MarkupString`，一變就整串重送，不 debounce 每秒會往下推 100KB。
  - ⚠️ **視窗裡的「下載 PDF」拿的是編輯區當下的內容**，不是資料庫那份——改了字還沒存就按下載，用舊內容產 PDF 會給出一份與畫面不符的檔案且毫無徵兆。

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
- API 路徑：`MeetingController` → `MeetingRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。**刪除例外，走 `MeetingService.DeleteAsync`**（0.4.115）：和畫面同一套，一併清影音、逐字稿與 AI 問答對話（1.0.29 起也刪這場會議的 AI 對話紀錄，用量帳本不動），背景工作進行中回 400。以前 API 只刪資料列與兩個實體檔、對話檔留在磁碟上；新增又照抄用戶端的 `Id`，別人用同一個編號就能讀到已刪會議的問答。現在新增一律由資料庫配號。
- Entity `Meeting`（DbSet 為 `context.Meeting`）。**刻意不叫 `MeetingRecord`**——會與根命名空間 `MeetingRecord` 衝突；`Meeting` 也與 0.4.24 移除前的舊表同名。
  因為「一筆會議只有一個影音檔」，媒體欄位直接內嵌，**不另開附件子表**，省掉 Cascade 與附件集合的整套機制。
- 標籤欄位 `Categories` 以 `TagStringHelper` 的「換行包夾」格式儲存；AutoMapper 以 `ForMember` 搭配 `ToList`／`ToStored` 轉換（`Teams` 0.4.103 已刪除）。
- 查詢一律 `AsNoTracking()`；寫入前後以 `CleanTrackingHelper.Clean<Meeting>` 清追蹤。
- 編輯前於 UI 以 `CurrentRecord = model.Clone()` 複製；`Clone()` 為淺複製後另建 `Categories` 新清單。
- **`UpdateAsync` 一律沿用資料庫既有的媒體與轉錄欄位**（1.0.29 起含 `MediaDurationSeconds`），只寫回標題／日期／描述／標籤。使用者開著 Modal 時背景轉錄若剛好完成，畫面上的舊複本不會把新狀態蓋掉；`MeetingRepository.UpdateAsync` 對 API 路徑做同樣保護。

### 檔案存放

| 內容 | 設定欄位（`SystemSettings.ExternalFileSystem`）| 路徑格式 |
| --- | --- | --- |
| 影音檔（原始上傳檔）| `MeetingMediaPath` | `{年}/{月}/{GUID}{原副檔名}` |
| 逐字稿 | `MeetingTranscriptPath` | `{年}/{月}/{GUID}.txt` |

年／月取自**主表的 `CreatedAt`**，與專案附件一致（見 [檔案上傳機制](../features/檔案上傳機制.md)）。逐字稿以 **UTF-8 含 BOM** 寫入，避免使用者用記事本開啟時出現亂碼。實體檔案的建立與刪除集中在 `MeetingFileStore`。

### 逐字稿一律繁體（1.0.41）

Azure 的 gpt-transcribe 直接輸出**簡體字**，提示詞管不到；使用者要求系統裡不要出現簡體字。逐字稿因此一律經 `Business/Helpers/ChineseTextConverter.ToTraditional`（OpenCCNET 1.1.0 的 `HansToTW(text, false)`）轉成繁體：

- **只轉字形（台灣字形，如「里面」→「裡面」），不轉用詞**（使用者決定）。逐字稿要忠於講者說的話：「软件」轉成「軟件」而不是「軟體」，「文件」也不會被換成「檔案」。
- **寫入時轉**：`TranscriptionJobRunner` 在 `MeetingFileStore.SaveTranscriptAsync` 之前轉，新產生的逐字稿檔存的就是繁體。
- **讀取時也轉**：`MeetingFileStore.ReadTranscriptAsync` 在 `TranscriptionNoiseFilter.Strip` 之後轉，所以 1.0.40 以前存下來的簡體檔**不必重新轉錄**，一律顯示繁體。逐字稿頁籤、複製全文、下載 .txt、生成會議紀錄與 AI 問答讀到的都是這個版本（磁碟上的舊檔不會被改寫）。
- **刻意不轉 LLM 的輸出**（會議紀錄草稿、AI 問答、待辦）：提示詞本來就要求繁體（台灣用語），輸入的逐字稿也已是繁體，對已是繁體的長文再轉一次只會增加誤換的機會。

### 允收政策

`MeetingMediaPolicy`（前端與服務層共用同一份判斷）：

- 單檔上限 **1GB**。
- 副檔名白名單：`.mp3`、`.wma`、`.wav`、`.m4a`、`.aac`、`.flac`、`.ogg`、`.opus`、`.amr`、`.mp4`、`.m4v`、`.mov`、`.avi`、`.wmv`、`.mkv`、`.webm`。
- 白名單只需涵蓋「FFmpeg 讀得懂」的格式，**不受轉錄 API 的格式限制約束**——所有格式送出前都已轉成 mp3。

### 轉錄管線

1. `MeetingFileStore.SaveMediaAsync` 以 80KB 緩衝區逐段複製串流，每當百分比變動就回報一次 `IProgress<int>`（進度列的資料來源）。
2. 落檔成功 → 狀態設為「待處理」→ `ITranscriptionQueue.EnqueueAsync(meetingId)`。
3. `TranscriptionBackgroundService`（單一 worker）取件，為每筆工作建立獨立 DI scope 執行 `TranscriptionJobRunner`。**1.0.29 起**工作開始時（轉檔之前）先以 `AiCallGateway.AdmitAsync` 檢查一次 AI 用量上限：被擋就標為「失敗」，`TranscriptionError` 寫「已達 AI 用量上限（…），M/d 起恢復；如需提高上限請聯絡系統管理員。」，不轉檔、不送出；之後可按「重新轉錄」。通過後各段不再檢查，一旦開始就做完。
4. `FfmpegMediaConverter` 以外部 FFmpeg 執行檔轉檔：
   `-vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 32k -f segment -segment_time 900 -segment_format mp3`
   輸出到系統暫存目錄，轉錄結束即整個目錄刪除。
5. 經 `AiCallGateway.TranscribeAsync`（1.0.29 起；記用量帳本與 AI 對話紀錄，關聯說明「會議 #N〈標題〉語音轉錄 i/n」，同一個音檔共用 ThreadKey；對話紀錄只記檔名、時長、分段與轉出的文字，不存音訊）由 `AzureOpenAiTranscriptionProvider` 逐段 `POST {Endpoint}/openai/deployments/{TranscriptionModel}/audio/transcriptions?api-version={...}`（`api-key` 標頭、`multipart/form-data`、`response_format=text`）。api-version 未設定時用內建的 `2025-03-01-preview`（1.0.37）。轉錄刻意不走 v1：實測 v1 轉錄路徑打 `gpt-transcribe` 部署回 404。
6. 各段文字以空行接合、轉成繁體（1.0.41，見上方「逐字稿一律繁體」）後寫入逐字稿檔，狀態轉為「已完成」；重跑時**新檔寫入成功後才刪舊檔**。1.0.29 起同時寫入 `Meeting.MediaDurationSeconds`（各段時長加總，儀表板「會議時數」讀這一欄）；重新上傳影音時清空。

> **為什麼一律切段**：Azure OpenAI 的音訊轉錄有單檔 25MB 上限，而上傳端允許到 1GB。固定切段讓長會議與短錄音走同一條路徑，不必為「檔案夠小就不切」多維護一個分支；15 分鐘 @32kbps 約 3.6MB，遠低於上限，同時順帶解決長會議的時長問題。

### 背景佇列與失敗語意

- `ITranscriptionQueue` 是行程內 `Channel<int>`（Singleton），**不做持久化**——本專案是單一實例假設（SQLite 檔案資料庫 + 本機磁碟儲存）。
- 單一 worker：轉錄同時受外部 API 速率限制與 FFmpeg 的 CPU 佔用影響，併發只會互相拖慢。
- 應用程式重啟時，`Program.cs` 的 `#region 轉錄狀態修復` 會把殘留在「處理中」**與「待處理」**的紀錄一律改為「失敗」（訊息「應用程式重啟導致轉錄中斷，請重新執行轉錄。」），避免永久卡住——佇列不持久化，已入列但還沒被 worker 撿走的同樣沒有人會接手。
- 重啟後不會自動重跑，由使用者按「重新轉錄」重新入列。
- 「重新轉錄」入列前 1.0.29 起先預檢 AI 用量上限（`MeetingService` 的 `CheckQuotaAsync`）：已達上限直接回「已達 AI 用量上限…」訊息，不改任何狀態也不入列。上傳後自動入列的轉錄不預檢，由工作開始時那一次檢查擋下。
- `TranscriptionJobRunner` 全程 try/catch，失敗寫入 `TranscriptionError` 供畫面顯示；背景服務再加一層保險，單筆例外不會讓整個 worker 停掉。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`會議紀錄:view`、`會議紀錄:create`、`會議紀錄:edit`、`會議紀錄:delete`。裸鍵「會議紀錄」代表該頁全部動作（向後相容）。
- **上傳影音檔與重新轉錄歸在 `edit`**，不新增動作類型。
- **上傳者專屬動作（1.0.47）**：同專案看得到的人都能看、改欄位（標題、說明、分類）、編修會議紀錄、重新產生、AI 問答、下載；**刪除、從專案移除、換影音檔、重新轉錄只限上傳者（`Meeting.CreatedByUserId`）與管理者**。判斷一律用 `ProjectAccess.IsMeetingOwner`，在 `MeetingService` 的 `DeleteAsync`／`DetachFromProjectAsync`／`SaveMediaAsync`／`RequeueTranscriptionAsync` 擋下，訊息由 `MeetingService.OwnerOnlyMessage` 組成，例如「只有上傳者或管理者可以刪除這筆會議紀錄。」；上傳者為 null 的舊會議只有管理者。
  - 上傳者本人只要有 `會議紀錄:edit` 就能刪自己上傳的（「一般使用者」角色沒有 `delete` 也能刪）；API `DELETE /api/Meeting/{id}` 因此改要求 `edit`，被上傳者規則擋下回 400 `ValidationError`。
  - 「主管」角色有 `delete`，但從此**不能刪別人上傳的會議**。
  - 歸屬到專案沒有另外加上傳者檢查：未歸屬的會議本來就只有上傳者（與管理者）看得到。
  - 原因：刪除與換檔會讓原始資料消失；從專案移除後只剩上傳者看得到，等於把會議從同事手上拿走。
- 刪除會帶走什麼：影音檔、逐字稿、會議紀錄草稿、會議的 AI 問答對話、會議的 AI 對話紀錄（`AiCallLog` 與內容檔）；**從這場會議抽出的待辦保留**（`Todo.MeetingId` 設為 null，待辦異動紀錄保有來源會議標題快照），通知、稽核、Token 用量帳本也保留。沒有軟刪除或資源回收桶。整理表見 [權限與資料可見範圍總覽](../security/權限與資料可見範圍總覽.md)。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路。
- **資料可見範圍（現行規則，0.4.99 起；0.4.102 起為主責＋協作團隊）**：會議**跟著專案走**，一律經 `ProjectAccessService.GetAsync()` 取得的 `ProjectAccess` 判斷（記憶體版 `ProjectAccess.CanViewMeeting`、SQL 版 `ProjectAccess.Filter(IQueryable<Meeting>)`，兩者是同一條規則，要一起改）：
  - **已歸屬專案**：使用者所屬團隊（`UserTeam`）與該專案的主責或協作團隊有交集才看得到，沒有公開專案。
  - **未歸屬專案**：只有上傳者（`Meeting.CreatedByUserId`）看得到；上傳者為 null（0.4.80 以前、啟動回填也推不回來的舊會議）時只有管理者看得到。
  - **管理者**（`MyUser.IsAdmin`）看全部。
  - 看不到的當成不存在：UI 找不到、API 回 404。
  - 套用範圍：`MeetingService` 的清單、單筆、修改、刪除、上傳影音檔、重新轉錄、歸屬到專案、產生會議紀錄（目標專案也要看得到）、讀逐字稿（`ReadTranscriptAsync`，1.0.40），API 的 `MeetingRepository`，以及讀逐字稿原文的 **AI 問答**（`AiChatService.EnsureAccessAsync`，見 [AI 問答 PRD](AI問答-prd.md)）。分類只是標籤，不影響可見性。
- **已完成（封存）專案的會議唯讀（1.0.46）**：會議歸屬的專案狀態是「已完成」時，一般使用者的修改、刪除、上傳影音檔、重新轉錄、從專案移除、編修會議紀錄都會被擋，歸屬到已完成專案、產生會議紀錄時指定已完成專案也一樣；訊息「這個專案已完成（封存），請先重新開啟再修改。」。管理者放行。檢視、下載 PDF、AI 問答不受影響。服務層一律經 `ProjectArchiveRules.CheckWritableAsync`。
- 逐字稿內容為高敏感資料：0.4.82～1.0.39 沒有逐字稿預覽，讀逐字稿原文的唯一路徑是 AI 問答。1.0.40 起多了唯讀的逐字稿頁籤，經 `MeetingService.ReadTranscriptAsync(meetingId)` 讀取——與清單同樣經 `ProjectAccessService` 過濾，看不到的會議回 null（當成不存在），內容是 `MeetingFileStore.ReadTranscriptAsync` 濾過雜訊的版本；複製與下載另要匯出權限，成功後寫稽核。兩條路徑都在 Blazor 服務層、Cookie 驗證、經上述專案權控守門；**沒有任何可直接下載檔案的 HTTP 端點**。
- 歷史（已失效，僅供對照）：
  - ~~0.4.98 以前以 `Meeting.Teams` 做團隊列級權控（`TagStringHelper.BuildTeamAccessPredicate`／`IsTeamAccessible`，無團隊＝公開）~~；0.4.35 從表單移除團隊欄位後，~~0.4.35～0.4.98 新建的會議一律公開~~，0.4.82 移除逐字稿預覽後 AI 問答一度沒有列級守門。0.4.99 改為專案權控解決，**0.4.103 `Meeting.Teams` 與上述兩個方法已刪除**。
- 新增權限鍵要開給非管理者時，由管理者到角色管理（`/roleviews`）在對應角色勾選（0.4.98～0.4.100 只有「一般使用者」一個角色，0.4.101 起恢復多角色）。
- 會議逐字稿會外送第三方 LLM 供應商，導入前應確認資料處理、留存與跨境政策符合組織要求。

### 稽核（1.0.34）

- `MeetingService`（網頁）與 `MeetingRepository`（API）：`Meeting.Create`／`Update`／`Delete`（摘要帶 `via`；API 刪除經 `MeetingService.DeleteAsync(id, via)`）。
- 流程動作：`Meeting.MediaUpload`（檔名與大小；使用者取消不記）、`Meeting.Retranscribe`、`Meeting.AttachProject`／`DetachProject`（`projectId`）、`Meeting.DraftRequest`（專案、提示詞名稱、與會者人數）、`Meeting.DraftUpdate`（**只記字數**，不記會議紀錄內容）、`Meeting.ExportPdf`（會議頁與專案頁的匯出）、`Meeting.TranscriptDownload`「下載逐字稿」與 `Meeting.TranscriptCopy`「複製逐字稿」（1.0.40，頁面在成功後寫入，detail 帶 `page=Meeting`／`Project`）。
- 看不到的會議被嘗試修改、刪除、上傳、重新轉錄、歸屬、移出、產生或編修草稿時寫 `Permission.Denied`（`reason=DataScope; op=…`）；歸屬或產生草稿時指定了看不到的專案，目標是那個專案。用量上限擋下由 `AiCallGateway` 另記 `Ai.QuotaBlocked`。見 [稽核紀錄 PRD](稽核紀錄-prd.md)。

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
- 非管理員且不屬於任何團隊：看不到任何已歸屬專案的會議，只看得到自己上傳的未歸屬會議（0.4.99 起；0.4.98 以前是「只看得到公開（無團隊標籤）的會議」）。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service 以 try/catch 記錄並回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

### 已知限制

- 1GB 檔案經 Blazor Server 的 SignalR 傳輸相當慢，進度列會如實反映；這是 Blazor Server 的架構天花板，不在本版處理範圍。
- **大檔上傳可能整條連線中斷**（0.4.116 記錄）：IIS Production 上 127MB 的 mp4 傳到第 11 秒，Blazor 讀檔串流判定逾時並終止連線，整頁空白，會議已建立但沒有影音檔（可重新整理後編輯該筆再上傳）。根本原因尚未確認，先檢查 IIS 是否啟用 WebSocket；若啟用後仍中斷，改走 HTTP 上傳 API。見 [2026-10-08 changelog](../changelog/2026-10-08-會議上傳進度與連線中斷.md)。
- 清單上的轉錄狀態**不會自動更新**，需要按「重新整理」才會看到背景轉錄的最新進度。
- 1.0.37 起文字生成走 v1 API、不需要版本；轉錄的 api-version 內建 `2025-03-01-preview`，`TranscriptionApiVersion` 只在需要換版本時填。

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
      "TranscriptionModel": "gpt-4o-transcribe"
    }
  }
}
```

- `TranscriptionProvider` 留空 → 沿用 `DefaultProvider`（轉錄端與生成端可指向不同廠商）。
- `TranscriptionApiVersion` 選填，留空 → 用內建的 `2025-03-01-preview`（1.0.37 起不再有 `ApiVersion`）。
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

- `AddAsync_ShouldPersistFieldsAndTagStrings`：新增後保留欄位，且標籤確實轉為 `TagStringHelper` 儲存格式（0.4.103 起以 `Categories` 驗證；漏接轉換器會讓分類過濾失效）。
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
- `SaveMediaAsync_NonMember_ShouldDenyMeetingOfOtherProject`：越權上傳被拒。
- `RequeueTranscriptionAsync_*`：重設狀態並入列；沒有影音檔或處理中時拒絕。
- `TextSearchHelperTests`（0.4.82，30 筆）：搜尋取代的純函式。重點在**不重疊掃描**（寫成 `at + 1` 會多算一筆）、**`OrdinalIgnoreCase` 保證比對到的片段長度等於搜尋字串長度**（換成 culture-sensitive 比對，索引就會歪）、**換行正規化**（`<textarea>` 的 value 一律是 LF，含 CRLF 的文字索引會愈往後偏愈多，只有長文件的後半段才看得出來）、以及**取代字串含搜尋字串時不連鎖**（把「a」換成「aa」）。以突變測試確認三條規則都會變紅。
- `ModalSizeClassTests`（0.4.82）：掃描所有 `.razor` 的 `<Modal>`，每個都要剛好一個尺寸 class，且不可以同時留下已失效的 `Width`（尺寸 class 的 `width` 是 author-important，贏過 `Width` 產生的 inline style）。
- `ReadTranscriptAsync_*`（1.0.40，3 筆）：回傳逐字稿內容、沒有逐字稿回 null、看不到的會議（非成員）回 null。1.0.41 加 `ReadTranscriptAsync_ShouldConvertLegacySimplifiedTranscript`：舊的簡體逐字稿讀出來是繁體。
- 簡轉繁另有 `ChineseTextConverterTests`（1.0.41，用真的 OpenCC 字典跑，順便驗證字典有複製到輸出目錄）：簡體字轉換、台灣字形（里面→裡面）、不轉用詞（软件→軟件）、繁體與中英混合原樣、空白原樣；`TranscriptionJobRunnerTests.Run_ShouldSaveTranscriptInTraditionalChinese` 直接讀存下來的原始檔，確認寫入的就是繁體。
- `GetAsync_Admin_ShouldSeeAllRecords`、`GetAsync_Member_ShouldSeeOwnProjectsAndOwnUnassignedMeetings`、`GetAsync_UserWithoutProjects_ShouldSeeNothingOfOthers`、`GetById_NonMember_ShouldDenyMeetingOfOtherProject`、`GetAsync_WithKeyword_ShouldMatchMediaFileName`：可見性（專案權控）與查詢條件。舊的團隊標籤可見性與 `TeamFilter` 測試已於 0.4.99／0.4.103 移除。
- `ArchivedProject_MemberCannotAttachOrEditDrafts_AdminCan`（1.0.46）：已完成（封存）專案，一般成員不能把會議歸屬進去、不能編修會議紀錄，管理者可以。
- 1.0.47：`SameProjectMember_CanEditOthersMeeting_ButOnlyUploaderCanDeleteDetachReplaceOrRetranscribe`（同專案成員可以改別人上傳的會議，但刪除、從專案移除、換影音檔、重新轉錄只限上傳者）、`LegacyMeetingWithoutUploader_OnlyAdminCanDelete`（沒有上傳者的舊會議只有管理者能刪）、`DeleteMeeting_KeepsExtractedTodos_AndCountLinkedTodos`（刪會議保留抽出的待辦、`CountLinkedTodosAsync` 筆數正確）。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/TranscriptionRequestTests.cs`（只測純函式，**不打真實 API、不跑真實 ffmpeg**）：

- `BuildRequestUri_*`：Azure OpenAI 轉錄端點組裝、無雙斜線、必填欄位空白時擲出。
- `EffectiveTranscriptionApiVersion_*`：有設定時用設定值，留空時用內建版本。
- `BuildSegmentArguments_*`：FFmpeg 參數含 `-vn`／單聲道／16kHz／libmp3lame／32kbps、固定使用 segment muxer、路徑加引號。
- `IsAllowedFileName_*`、`MaxUploadFileSize_ShouldBeOneGigabyte`：允收政策。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/MeetingRegistrationTests.cs`（守住宣告式權限註冊三件組）：

- `RolePermissionCatalog_ShouldContainMeetingPage`、`RolePermissionCatalog_ShouldPlaceMeetingUnderMeetingManagementGroup`：權限鍵登記於「會議管理」群組。
- `MenuJson_ShouldContainMeetingNode`、`MenuJson_ShouldPlaceMeetingAtTopLevelInCoreSection`：`Menu.json` 有 id=61 節點、名稱與 url 正確，且位於「核心功能」區的頂層。

`src/MeetingRecord/MeetingRecord.Tests/MenuIconTests.cs` 的 `AllowedIcons` 已加入 `event` 與 `mic`；未加入會使整套測試失敗。

轉錄設定另有 `LlmSettingsTests.cs`（`TranscriptionProvider` / `TranscriptionModel` 的回退與驗證、`TranscriptionApiVersion` 內建值、舊 `ApiVersion` 鍵被忽略）與 `StartupSafetyValidatorTests.cs`（Production 對轉錄金鑰、`TranscriptionModel`、`FfmpegPath` 的檢查）。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，檔案操作導向系統暫存目錄下的隨機資料夾，轉錄佇列以假的 `ITranscriptionQueue` 注入。注意 `EnsureCreatedAsync` 直接由模型建表、**繞過 migration**，所以測試全綠不代表 migration 存在。

### 手動驗收（需先安裝 FFmpeg 並填好 Azure 金鑰）

1. 啟動 → 確認 `MeetingMediaPath` / `MeetingTranscriptPath` 自動建立。
2. 登入 → 側邊欄出現「會議管理 > 會議紀錄」→ 進 `/meetings`。
3. 新增會議並上傳 mp3：進度列 0%→100%，狀態依序為「待處理」→「處理中」→「已完成」。
4. 開「AI 問答」提問，確認回答引用的逐字稿片段中文正常無亂碼。
   1.0.40 起另開「會議紀錄與逐字稿」的逐字稿頁籤，確認中文正常，再下載 .txt 用記事本開啟確認不亂碼。
   1.0.41 起逐字稿頁籤與下載的 .txt 都應是繁體；開一筆 1.0.40 以前轉錄的會議，確認不重新轉錄也顯示繁體。
   ⚠️ 0.4.82～1.0.39 這是唯一還會碰到逐字稿檔案編碼的驗收點——`MeetingFileStore` 的讀寫編碼一旦有人動了，別處只會表現成「AI 寫出來的內容怪怪的」。
5. 上傳 mp4（含影像軌）與 wma，確認 FFmpeg 抽音軌成功、同樣產出逐字稿。
6. 故意填錯 `ApiKey` → 狀態「失敗」並可看到錯誤訊息 → 按「重新轉錄」可重跑。
7. 轉錄進行中重啟應用程式 → 該筆自動變成「失敗」（不卡在「處理中」）。
8. 刪除該筆 → 資料列消失，且影音檔與逐字稿實體檔一併移除。
9. 以非管理員帳號驗證可見性（所屬團隊是專案主責／協作的會議看得到、其他專案的看不到、未歸屬的只有上傳者看得到）。

## 九、規劃中需求

以下**尚未實作**，不屬於 0.4.27 驗收範圍：

- 套用提示詞範本 → 呼叫 LLM 產生會議紀錄草稿（見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)）。
- ~~**把團隊守門補進 `AiChatService`**（0.4.82 起它是逐字稿原文的唯一出口，且無列級權控）~~——0.4.99 已由專案權控完成。
- 逐字稿的編修（0.4.55 曾有，0.4.82 移除；1.0.40 只恢復唯讀檢視、複製與下載 .txt）。若要復原編修，連同版本歷程一起想清楚——先前是就地覆寫，不留舊內容。
- 影音檔／逐字稿的 HTTP 下載端點與線上播放。
- 清單轉錄狀態的自動輪詢或即時推播。
- 逐字稿保留期限與自動清理政策。
- 轉錄成本上限、每日配額與失敗重試計費控管。

## 十、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Meetings/MeetingPage.razor`（`@page "/meetings"`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Meetings/MeetingViewView.razor`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Meetings/MeetingViewView.razor.cs`（`MeetingViewView`：`OnInitializedAsync` 頁面權限、上傳進度、會議紀錄編修入口）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/MeetingController.cs`（`MeetingController` 各端點的 `[HasPermission]` 動作鍵）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/MeetingService.cs`（`MeetingService`：CRUD、專案權控、`SaveMediaAsync` 上傳、`RequeueTranscriptionAsync` 重新轉錄、`AttachToProjectAsync`／`DetachFromProjectAsync`、`RequestDraftAsync`、`ReadTranscriptAsync` 讀逐字稿）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/MeetingFileStore.cs`（`MeetingFileStore`：實體檔案存取與上傳進度回報；`ReadTranscriptAsync` 讀取時簡轉繁）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/ChineseTextConverter.cs`（`ChineseTextConverter`：逐字稿簡轉繁，只轉字形，1.0.41）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/ProjectAccessService.cs`（`ProjectAccess.CanViewMeeting`、`ProjectAccess.Filter(IQueryable<Meeting>)`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/ITranscriptionProvider.cs`（`ITranscriptionProvider`，換廠商的擴充點）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/AzureOpenAiTranscriptionProvider.cs`（`AzureOpenAiTranscriptionProvider`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/FfmpegMediaConverter.cs`（`FfmpegMediaConverter`：轉檔與切段）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/TranscriptionJobRunner.cs`（`TranscriptionJobRunner`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/TranscriptionQueue.cs`（`ITranscriptionQueue`／`TranscriptionQueue`）
- `src/MeetingRecord/MeetingRecord.Web/BackgroundServices/TranscriptionBackgroundService.cs`（`TranscriptionBackgroundService`）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/MeetingMediaPolicy.cs`（`MeetingMediaPolicy`：允收政策）
- `src/MeetingRecord/MeetingRecord.Business/Repositories/MeetingRepository.cs`（`MeetingRepository`：API 路徑，0.4.99 起同樣經 `ProjectAccessService` 過濾）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/Meeting.cs`（`Meeting`）、`src/MeetingRecord/MeetingRecord.AccessDatas/BackendDBContext.cs`（`BackendDBContext.Meeting` DbSet）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Migrations/20260821090356_AddMeeting.cs`（`AddMeeting`）
- `src/MeetingRecord/MeetingRecord.Share/Enums/TranscriptionStatus.cs`（`TranscriptionStatus`、`TranscriptionStatusText`）
- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs`（`LlmSettings`）、`MediaSettings.cs`（`MediaSettings`）、`MeetingMediaUploadInput.cs`（`MeetingMediaUploadInput`）
- `src/MeetingRecord/MeetingRecord.Models/AdapterModel/MeetingAdapterModel.cs`（`MeetingAdapterModel`）
- `src/MeetingRecord/MeetingRecord.Business/Models/AutoMapping.cs`（`AutoMapping` 的 `#region Meeting`：標籤欄位轉換）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs`（`#region 角色`：`角色_會議管理`、`角色_會議紀錄`）、`PermissionKeys.cs`（`PermissionActions`／`PermissionKey`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs`（`GetRoleListPermissionAllName` 權限矩陣登記）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`MenuPermissionMap[61]`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 61）
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`（`AddApplicationServices`／`AddTranscriptionServices`／`AddConfiguredOptions`）
- `src/MeetingRecord/MeetingRecord.Web/Program.cs`（`#region 系統使用的目錄準備`、`#region 加入設定強型別注入宣告`、`#region 轉錄狀態修復`）
- `src/MeetingRecord/MeetingRecord.Web/Configuration/StartupSafetyValidator.cs`（`StartupSafetyValidator`：Production 檢查）
- 交叉連結：[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)、[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)、[../features/檔案上傳機制.md](../features/檔案上傳機制.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../architecture/Web API 端點目錄.md](../architecture/Web%20API%20端點目錄.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)、[../operations/正式部署與安全檢查清單.md](../operations/正式部署與安全檢查清單.md)
