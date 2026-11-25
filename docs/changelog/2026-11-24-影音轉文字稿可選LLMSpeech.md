# 影音轉文字稿可選 Azure LLM Speech 與分講者（1.1.72 → 1.1.73）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.73
- 首次實作版本：1.1.73
- 最後核對日期：2026/11/24

## 為什麼做

- **diarize 要下線**：`gpt-4o-transcribe-diarize` 被反映逐字稿太精簡、夾英文，而且 2027/02/26 下線；替代的 `gpt-transcribe` 不分講者。
- **使用者要能比較**：使用者想實測兩種語音轉文字技術在品質與效率上的差異，所以每次上傳或重新轉錄都可以選：
  - 用 Azure OpenAI 還是 **Azure LLM Speech**；
  - 要不要分講者。

## 查到的官方資訊（2026/11/24）

| 項目 | 內容 |
|---|---|
| 呼叫方式 | LLM Speech 是 Fast Transcription API 開 `enhancedMode`：`POST {endpoint}/speechtotext/transcriptions:transcribe?api-version=2025-10-15`，標頭 `Ocp-Apim-Subscription-Key`，multipart 送 `audio` 與 `definition`。 |
| 分講者 | `diarization: { enabled, maxSpeakers }`，講者數 2～35；可和 `enhancedMode` 一起用（translate 任務除外）。 |
| 語言與片語 | 支援 `zh-TW`；`phraseList` 可提示人名與專有名詞；prompt 官方建議用英文、只保證格式類指令，本系統不送。 |
| 單檔上限 | 配額頁寫 < 5 小時、< 500 MB；REST 參考寫 < 2 小時、< 250 MB。本系統保守取 **2 小時**。 |
| 其他限制 | 每分鐘 600 次請求；免費層 F0 不支援。 |
| 區域 | 只在 centralindia、eastus、northeurope、southeastasia、westeurope、westus、westus2 提供。 |
| 價格 | Azure 價格 API（eastus）：Fast Transcription **US$0.36／小時**，按秒計費。LLM Speech 和它共用同一個計價項目。2026/9/1 起另有 US$0.10／小時的促銷價，沒寫結束日，本系統不採用。分講者沒有另外的計價項目，要等帳單確認。 |

參考資料：[LLM Speech](https://learn.microsoft.com/zh-tw/azure/ai-services/speech-service/llm-speech)、[Fast Transcription](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/fast-transcription-create)、[配額與限制](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-services-quotas-and-limits)、[價格頁](https://azure.microsoft.com/en-us/pricing/details/cognitive-services/speech-services/)、[Azure 價格 API](https://prices.azure.com/api/retail/prices)。

## 做了什麼

### 使用者看得到的

- **上傳表單**：選好影音檔或錄好音後，在「轉錄完自動產生會議紀錄」下方多三個選項：
  - **轉錄引擎**：Azure OpenAI／Azure LLM Speech。
  - **分講者**：開或關。
  - **講者上限**：只有 LLM Speech 開分講者時出現；預設是專案與會人員名冊的人數，沒有專案時用 10。
- **確認框**：多一行「轉錄方式」，預估費用照選的引擎與部署的單價算。
- **重新轉錄**：原本的確認框改成一個視窗，裡面可以選同樣三個選項，預設帶這場上次的選擇；換選項時預估費用跟著重算。
- **會議清單**：「已完成」下面多一行小字，寫用了哪個引擎、有沒有分講者、轉錄花了多久，方便比較效率。
- **標示講者**：LLM Speech 分出來的講者全部都能命名、都有試聽片段，不再只有講者 1～4。
- **轉錄比較頁**：每一組可以選 Azure LLM Speech（分講者、講者上限），預設多帶一組 LLM Speech。

### 規則

- **不能用的組合當場擋**：建立上傳工作階段、重新轉錄時就驗證，例如沒有設定 LLM Speech、Azure OpenAI 沒有不分講者的部署。
- **失敗不自動換引擎**：會議標成轉錄失敗並寫原因，使用者用「重新轉錄」自己換。
- **Azure OpenAI 不分講者**：要另外設定 `TranscriptionModelNoSpeakers`（例如 `gpt-transcribe`）；沒設時不能選。
- **LLM Speech 一次送整個檔案**：超過 2 小時就在停頓處切段，每段講者各自編號，逐字稿標成「講者 2-1」（第 2 段的講者 1），可分段命名。
- **片語清單**：專案名稱＋專案與會人員名冊。
- **預設**：確認框預設 **LLM Speech＋分講者**；沒設定 Speech 時退回 Azure OpenAI。
- **建議設定（停用 diarize）**：Azure OpenAI 的轉錄部署改填 `gpt-transcribe`。部署名稱不含 diarize，系統就把它當不分講者，不必另填 `TranscriptionModelNoSpeakers`；分講者交給 LLM Speech。
- **API 沒帶選項**：照設定的部署轉錄，和 1.1.72 一樣。重新轉錄沒帶選項時，沿用這場上次的選擇。

## 實作

- **設定**：新區段 `TranscriptionSettings`。
  - 預設值：`DefaultEngine`、`DefaultDiarize`、`DefaultMaxSpeakers`。
  - 連線：`AzureSpeech` 底下的 `Endpoint`、`ApiKey`、`Locale`、`ApiVersion`、`MaxFileSeconds`、`TimeoutMinutes`。
  - 另外：`LlmProviderSettings.TranscriptionModelNoSpeakers`；Pricing 加 `azure-llm-speech`，每分鐘 0.006 美元。
- **會議欄位**：`TranscriptionEngine`、`TranscriptionDiarize`、`TranscriptionMaxSpeakers`、`TranscriptionElapsedSeconds`（migration `AddMeetingTranscriptionEngine`）。
- **`AzureLlmSpeechTranscriptionProvider`**：
  - 不註冊成 `ITranscriptionProvider`，因為引擎是每場會議自己選的。
  - `WithOptions(LlmSpeechOptions)` 帶分講者、講者上限、片語。
  - `FormatTranscript` 依每句時間**每 5 分鐘分一個段落（`\n\n`）**：LLM Speech 一次回整場，而 `TranscriptChunker` 與雜訊過濾都靠 `\n\n` 切。
- **`TranscriptionOptionsResolver`**：
  - 畫面用的選項（`GET api/v1/Meeting/transcription-options`）。
  - 驗證使用者選的組合。
  - 決定 Azure OpenAI 實際打哪個部署（diarize 或不分講者），以及用哪個單價。
- **`TranscriptionJobRunner`**：
  - LLM Speech 用 `IMediaConverter.ConvertToMp3SegmentsAsync(source, maxSegmentSeconds)`（預設 7200 秒）。
  - 不做聲音參考對齊，每位講者剪最長的一句當試聽片段。
  - Azure OpenAI 依分講者換部署（`WithOverrides`）。
  - 成本閘門用實際引擎的單價，完成時記耗時。
- **`TranscriptSpeakerLabels`**：
  - 數字代號也認「2-1」，排序依段落再依講者。
  - 產生會議紀錄時給模型的說明（`SpeakerLabelNote`）也補上這種代號的意思。
- **API 變更**：
  - `POST api/v1/uploads` 多 `Transcription`。
  - `POST api/v1/Meeting/{id}/retranscribe` 可帶 `TranscriptionOptionsDto`。
  - `GET transcription-estimate` 可帶 `engine`、`diarize`。

## 管理者要做的

1. 在 Azure 建一個 **Speech 資源**，區域要在上面列的 LLM Speech 區域。音檔會送到那個區域處理。
2. 在設定檔填 `TranscriptionSettings:AzureSpeech:Endpoint`。`ApiKey` 正式環境用環境變數 `TranscriptionSettings__AzureSpeech__ApiKey` 填。
3. 要讓 Azure OpenAI 可以不分講者，在 `LlmSettings:Providers:AzureOpenAI:TranscriptionModelNoSpeakers` 填 `gpt-transcribe` 這類部署，並確認 Pricing 有它的單價。
4. 第一張帳單出來後，核對 LLM Speech 的實際單價，以及分講者有沒有加價，再調整 `azure-llm-speech` 的單價。

## 測試

- `LlmSpeechTranscriptionTests`：
  - 請求的 definition（分講者、講者上限夾在 2～35、片語去重）。
  - 回應解析（講者、秒數、沒有講者的句子）。
  - 每 5 分鐘分段。
  - 切段時的「2-1」標示、試聽片段挑選。
  - 選項驗證、Azure OpenAI 部署切換。
  - 轉錄工作走 LLM Speech 的整條路：送整檔、帶片語、存講者與試聽、帳本模型；以及沒設定時失敗並寫原因。
- `TranscriptionOptionsApiTests`：
  - 選項 API 未登入 401、只能檢視 403、可編輯 200。
  - 上傳與重新轉錄選了沒設定的 LLM Speech 時回 400。
- 既有的轉錄、講者對齊、比較工具、會議與上傳 API 測試照常通過。
- **實機測（2026/11/25）**：轉錄部署改 `gpt-transcribe`、分講者用 LLM Speech，效果良好。
