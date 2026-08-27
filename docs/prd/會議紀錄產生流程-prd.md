# 會議紀錄產生流程 PRD

- 文件版本：2.0
- 文件狀態：部分實作
- 現行系統版本：0.4.27
- 首次實作版本：0.4.27（僅前半段：上傳 → 轉錄 → 逐字稿）
- 最後核對日期：2026/08/21

> ⚠️ 本文件描述的是**跨越兩個版本的完整流程**。0.4.27 已完成前半段（音檔上傳 → 轉錄 → 逐字稿），詳見 [會議紀錄 PRD](會議紀錄-prd.md)；**後半段（套用提示詞 → LLM → 會議紀錄草稿）仍未實作**，不屬於 0.4.27 驗收範圍。

## 一、目標與範圍

目標是讓使用者上傳會議語音檔後，套用一組事先維護好的提示詞，自動產出可編修的會議紀錄草稿。

```
音檔上傳 ─► 轉錄（STT）─► 逐字稿 ─► 套用提示詞範本 ─► LLM ─► 會議紀錄草稿 ─► 人工編修／保存
└──────────── 0.4.27 已實作 ────────────┘└──────────── 尚未實作 ────────────┘
```

非範圍（即使實作也不打算納入）：
- 不做即時（會議進行中）轉錄與逐字稿串流。
- 不做說話者聲紋辨識與身分綁定。
- 不做多語言翻譯輸出。

## 二、各階段現況界線

| 階段 | 現況 |
| --- | --- |
| 提示詞範本維護 | **已實作**（0.4.26，見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)） |
| `LlmSettings` provider-aware 強型別設定 | **已實作**（0.4.26 骨架；0.4.27 新增 `TranscriptionProvider`／`TranscriptionModel`／`TranscriptionApiVersion`，並由轉錄供應商實際使用） |
| 音檔上傳與保存 | **已實作**（0.4.27）。單檔 ≤1GB，存於 `SystemSettings.ExternalFileSystem.MeetingMediaPath` 的年／月 + GUID 目錄 |
| 音檔格式／大小的允收政策 | **已實作**（0.4.27）。`MeetingMediaPolicy` 提供副檔名白名單與 1GB 上限，前端與服務層共用 |
| 語音轉錄（STT） | **已實作**（0.4.27）。Azure OpenAI `audio/transcriptions`；供應商以 `ITranscriptionProvider` 抽象，可再加其他廠商 |
| 影音前處理 | **已實作**（0.4.27）。FFmpeg 抽音軌、轉 16kHz 單聲道 mp3、固定切成 15 分鐘分段 |
| 會議紀錄 Entity 與頁面 | **已實作**（0.4.27）。`Meeting` 實體 + `/meetings` 頁面 |
| 非同步作業、進度回報、失敗重試 | **已實作**（0.4.27）。行程內 `Channel<int>` 佇列 + 單一 worker `BackgroundService`；上傳有百分比進度列、轉錄狀態欄位、「重新轉錄」按鈕 |
| 變數代入與 LLM 呼叫 | **未實作**。`PromptVariableHelper` 的固定變數集尚無代入端 |
| 會議紀錄草稿的產生與編修 | **未實作**。逐字稿目前為唯讀預覽 |

## 三、0.4.27 已定案的決議

原本列為「未決議題」的項目，實作時的結論：

| 議題 | 結論 |
| --- | --- |
| 轉錄與生成的供應商選型 | 轉錄採 Azure OpenAI（`gpt-4o-transcribe`）。`LlmSettings.TranscriptionProvider` 與 `DefaultProvider` 分開，兩端可指向不同廠商；留空則沿用 `DefaultProvider` |
| 長音檔處理 | **一律**以 FFmpeg 切成 15 分鐘 mp3 分段，逐段轉錄後串接。不做「檔案夠小就不切」的分支——固定路徑同時解掉 25MB 單檔上限與長會議問題 |
| 逐字稿是否落庫 | **不落庫**，寫成 `.txt` 存於 `MeetingTranscriptPath`；資料表只存相對路徑。保留期限與自動清理政策仍未定 |
| 同步等待或背景作業 | 背景作業。行程內 `Channel<int>` + 單一 worker，不阻塞 Blazor circuit |
| 失敗語意 | 轉錄失敗時狀態轉「失敗」並寫入 `TranscriptionError`；影音檔保留，可按「重新轉錄」重跑。應用程式重啟會把殘留的「處理中」改判為「失敗」 |
| 產出物歸屬 | **獨立實體**，不掛在專案項目之下；以 `Categories`／`Teams` 多值標籤分群與控管可見性 |

## 四、後半段仍待決議

實作「套用提示詞 → LLM → 草稿」前必須先有結論：

- **逐字稿的 token 上限**：超長會議是否分段摘要後再合併，或改用長脈絡模型。
- **成本與速率限制**：單次產出的成本上限、每日配額、失敗重試是否重複計費。
- **部分成功的呈現**：轉錄完成但生成失敗時，畫面如何呈現與續跑。
- **草稿的保存形式**：與逐字稿一樣落檔，或改為可編輯欄位落庫（牽涉版本歷程）。
- **提示詞與會議的綁定**：每次產生時選擇，或在會議紀錄上記住上次使用的範本。
- **合規界線**：會議逐字稿外送第三方 LLM 的資料處理、留存與跨境政策（轉錄階段已有同樣的考量）。

## 五、後半段對現有設計的預期影響

- 需要新增文字生成的供應商抽象（可比照 `ITranscriptionProvider` 的形狀），並沿用既有的 `AddHttpClient` 註冊。
- 需要在 `Meeting` 上新增草稿相關欄位（或新的產出物實體）與對應 migration。
- 若採背景執行，可沿用既有的 `ITranscriptionQueue`／`BackgroundService` 模式，但需要區分工作種類。
- `PromptVariableHelper` 的 `{{transcript}}`／`{{meetingTitle}}`／`{{meetingDate}}` 屆時才會有實際代入端。
- 須同步更新 [會議紀錄 PRD](會議紀錄-prd.md)、[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md) 與 [日誌與設定檔說明](../operations/日誌與設定檔說明.md)。

## 六、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/ITranscriptionProvider.cs:1`（換廠商的擴充點）
- `src/MeetingRecord/MeetingRecord.Business/Services/Transcription/TranscriptionJobRunner.cs:1`（轉錄工作流程）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/PromptVariableHelper.cs:1`（產生時要代入的固定變數集，**尚無呼叫端**）
- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs:1`（provider-aware 設定）
- 交叉連結：[會議紀錄 PRD](會議紀錄-prd.md)、[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)、[../features/檔案上傳機制.md](../features/檔案上傳機制.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)、[../architecture/開發慣例與限制速查.md](../architecture/開發慣例與限制速查.md)
