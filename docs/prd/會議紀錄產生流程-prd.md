# 會議紀錄產生流程 PRD

- 文件版本：3.0
- 文件狀態：已實作
- 現行系統版本：0.4.31
- 首次實作版本：0.4.27（前半段：上傳 → 轉錄 → 逐字稿）／0.4.31（後半段：提示詞 → LLM → 會議紀錄）
- 最後核對日期：2026/08/31

> 本文件描述**跨越多個版本的完整流程**。0.4.27 完成前半段（音檔上傳 → 轉錄 → 逐字稿），詳見 [會議紀錄 PRD](會議紀錄-prd.md)；0.4.31 完成後半段（套用提示詞 → LLM → 會議紀錄），入口在 [專案項目 PRD](專案項目-prd.md) 描述的 `/projects` 頁面。

## 一、目標與範圍

目標是讓使用者上傳會議語音檔後，套用一組事先維護好的提示詞，自動產出可編修的會議紀錄。

```
音檔上傳 ─► 轉錄（STT）─► 逐字稿 ─► 歸屬專案 ─► 套用提示詞範本 ─► LLM ─► 會議紀錄 ─► 人工編修
└──────────── 0.4.27 ────────────┘└──────────────────── 0.4.31 ────────────────────┘
```

非範圍（即使實作也不打算納入）：
- 不做即時（會議進行中）轉錄與逐字稿串流。
- 不做說話者聲紋辨識與身分綁定（Azure OpenAI 的轉錄端點不支援 diarization）。
- 不做多語言翻譯輸出；生成一律輸出繁體中文。
- 不做 token 計量、成本上限與每日配額。
- 不做草稿的版本歷程；重新產生即覆蓋。

## 二、各階段現況界線

| 階段 | 現況 |
| --- | --- |
| 提示詞範本維護 | **已實作**（0.4.26，見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)） |
| `LlmSettings` provider-aware 強型別設定 | **已實作**（0.4.26 骨架；0.4.27 新增轉錄段設定；0.4.31 起 `DefaultProvider`／`Model`／`ApiVersion` 由文字生成端實際使用） |
| 音檔上傳與保存 | **已實作**（0.4.27）。單檔 ≤1GB，存於 `SystemSettings.ExternalFileSystem.MeetingMediaPath` 的年／月 + GUID 目錄 |
| 音檔格式／大小的允收政策 | **已實作**（0.4.27）。`MeetingMediaPolicy` 提供副檔名白名單與 1GB 上限，前端與服務層共用 |
| 語音轉錄（STT） | **已實作**（0.4.27）。Azure OpenAI `audio/transcriptions`；供應商以 `ITranscriptionProvider` 抽象 |
| 影音前處理 | **已實作**（0.4.27）。FFmpeg 抽音軌、轉 16kHz 單聲道 mp3、固定切成 15 分鐘分段 |
| 會議紀錄 Entity 與頁面 | **已實作**（0.4.27）。`Meeting` 實體 + `/meetings` 頁面 |
| 非同步作業、進度回報、失敗重試 | **已實作**（0.4.27）。行程內 `Channel<int>` 佇列 + 單一 worker `BackgroundService` |
| 逐字稿歸屬專案 | **已實作**（0.4.31）。`Meeting.ProjectId` 可空外鍵，一份逐字稿只屬一個專案 |
| 變數代入與 LLM 呼叫 | **已實作**（0.4.31）。`PromptVariableHelper.Render` 為代入端；`ITextGenerationProvider` 呼叫 `chat/completions` |
| 會議紀錄的產生與編修 | **已實作**（0.4.31）。`Meeting.DraftContent` 落庫，可於 `/projects` 檢視與編修 |

## 三、0.4.27 已定案的決議（前半段）

| 議題 | 結論 |
| --- | --- |
| 轉錄與生成的供應商選型 | 轉錄採 Azure OpenAI（`gpt-4o-transcribe`）。`LlmSettings.TranscriptionProvider` 與 `DefaultProvider` 分開，兩端可指向不同廠商；留空則沿用 `DefaultProvider` |
| 長音檔處理 | **一律**以 FFmpeg 切成 15 分鐘 mp3 分段，逐段轉錄後串接。不做「檔案夠小就不切」的分支——固定路徑同時解掉 25MB 單檔上限與長會議問題 |
| 逐字稿是否落庫 | **不落庫**，寫成 `.txt` 存於 `MeetingTranscriptPath`；資料表只存相對路徑。保留期限與自動清理政策仍未定 |
| 同步等待或背景作業 | 背景作業。行程內 `Channel<int>` + 單一 worker，不阻塞 Blazor circuit |
| 失敗語意 | 轉錄失敗時狀態轉「失敗」並寫入 `TranscriptionError`；影音檔保留，可按「重新轉錄」重跑。應用程式重啟會把殘留的「處理中」改判為「失敗」 |
| 產出物歸屬 | ~~獨立實體，不掛在專案項目之下~~ —— **此決議已於 0.4.31 推翻**，見下節 |

## 四、0.4.31 已定案的決議（後半段）

原本列為「未決議題」的六項，實作時的結論：

| 議題 | 結論 |
| --- | --- |
| 逐字稿的 token 上限 | **分段摘要再合併（map-reduce）**。`TranscriptChunker` 以 12000 字元為界切段，優先沿用轉錄本來的 `\n\n` 分段邊界。**只有一段時直接送原文**，不做「摘要後再整理」——對短會議而言那等於資訊被壓縮兩次 |
| 成本與速率限制 | **不做**。不計 token、不設單次上限或每日配額。內部工具、使用者數少，先不引入這層複雜度 |
| 部分成功的呈現 | 轉錄與生成各有獨立狀態欄位（`TranscriptionStatus`／`DraftStatus`）與獨立佇列。轉錄成功但生成失敗時，逐字稿保留、`DraftStatus` 轉「失敗」並寫入 `DraftError`，可換提示詞重跑 |
| 草稿的保存形式 | **落庫**（`Meeting.DraftContent`），可於畫面上人工編修。**不保留版本歷程**，重新產生即覆蓋——畫面上會先跳確認對話框 |
| 提示詞與會議的綁定 | **每次產生時選擇**，並把 `DraftPromptTemplateId` 與 `DraftPromptTemplateName`（名稱快照）寫在 `Meeting` 上。快照是為了讓範本日後被改名或刪除時，仍看得出當初用了什麼 |
| 合規界線 | 逐字稿送往組織自有的 Azure OpenAI 租戶，與轉錄階段同一個管道，不額外增加資料處理面。**不加送出前的確認對話框** |

### 推翻既有決議：產出物歸屬

0.4.27 決議「產出物＝獨立實體，不掛在專案項目之下」。0.4.31 **推翻此決議**：

- `Meeting` 新增 `ProjectId` 可空外鍵，關聯為一對多——**一份逐字稿只能屬於一個專案，一個專案可以有多份逐字稿**。
- 刪除專案時 `OnDelete(DeleteBehavior.SetNull)`：只解除歸屬，逐字稿與會議紀錄保留。與 `Project → ProjectFile` 的 `Cascade` 不同，因為會議紀錄不是專案的附件。
- `Categories`／`Teams` 標籤與團隊權控維持不變，歸屬是額外的一層而非取代。

理由：使用者要的流程是「在專案底下挑一份逐字稿產生會議紀錄」，需要一個明確的歸屬關係才能呈現「本專案的歷史會議紀錄」。

## 五、生成流程

1. 使用者在 `/projects` 選定專案，從下拉挑一份轉錄完成的逐字稿與一組提示詞。已被**其他**專案取用的逐字稿在下拉中呈現為不可選並標示歸屬。
2. `MeetingService.RequestDraftAsync` 檢查團隊權限、轉錄狀態、歸屬衝突與是否正在生成，通過後寫入歸屬與提示詞快照、狀態轉 `Pending`，再排入 `IMeetingDraftQueue`。
3. `MeetingDraftBackgroundService`（**與轉錄各自獨立的第二條佇列與 worker**）取件，在自己的 DI scope 內執行 `MeetingDraftJobRunner`。
4. Job runner 讀逐字稿 → `TranscriptChunker.Split` → 多段時逐段摘要（map）→ `PromptVariableHelper.Render` 代入提示詞 → 呼叫 LLM（reduce）→ 寫入 `DraftContent`、狀態轉 `Completed`。
5. 失敗一律轉 `Failed` 並寫入 `DraftError`（存失敗狀態時不帶已取消的 `CancellationToken`）。應用程式重啟會把殘留的「生成中」改判為「失敗」。

佇列刻意與轉錄分開：轉錄一筆可能跑數十分鐘，共用單一 worker 會讓草稿生成被長音檔堵住。

輸出語言在共用的 system prompt 中強制為繁體中文（台灣用語），提示詞範本不必自己交代。

## 六、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Business/Services/TextGeneration/ITextGenerationProvider.cs:1`（換廠商的擴充點）
- `src/MeetingRecord/MeetingRecord.Business/Services/TextGeneration/MeetingDraftJobRunner.cs:1`（生成工作流程與 map-reduce）
- `src/MeetingRecord/MeetingRecord.Business/Services/TextGeneration/TranscriptChunker.cs:1`（分段純函式）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/ITranscriptionProvider.cs:1`（轉錄端的對照組）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/PromptVariableHelper.cs:1`（變數偵測與代入）
- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs:1`（provider-aware 設定）
- 交叉連結：[會議紀錄 PRD](會議紀錄-prd.md)、[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)、[專案項目 PRD](專案項目-prd.md)、[../features/檔案上傳機制.md](../features/檔案上傳機制.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)、[../architecture/開發慣例與限制速查.md](../architecture/開發慣例與限制速查.md)
