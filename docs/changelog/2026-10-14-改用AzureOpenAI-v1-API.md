# 文字生成改用 Azure OpenAI v1 API，移除 ApiVersion 設定（1.0.27 → 1.0.28）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.0.28
- 首次實作版本：1.0.28
- 最後核對日期：2026/10/14

## 需求

使用者要改用 Azure OpenAI 的 v1 API，設定上不要再指定日期型的 `api-version`。

v1 的呼叫方式（Microsoft Learn「Azure OpenAI v1 API」）：

- 網址為 `{endpoint}/openai/v1/...`，`openai.azure.com` 與 `services.ai.azure.com` 兩種端點都可以。
- 部署名稱放在 request 的 `model`。
- 不需要 `api-version`，`api-key` 標頭照用。

## 實測（用本機設定打實際的 Azure 資源）

| 項目 | v1 路徑 | 舊的 deployments 路徑 |
|---|---|---|
| 文字生成（gpt-5.6-sol，串流＋`include_usage`） | ✅ 200，有文字與用量 | ✅（原本的做法） |
| 語音轉錄（gpt-transcribe） | ❌ 404 DeploymentNotFound | ✅ 200，正確辨識 |

v1 轉錄試過四種寫法，結果都一樣：

- 換成 `openai.azure.com` 主機。
- 加上 `api-version=preview`。
- 前兩項一起用。
- 把 `model` 欄位放在音檔前面。

所以轉錄維持舊路徑，由使用者決定把版本內建在程式裡。

## 修正

- **文字生成走 v1**：
  - `AzureOpenAiTextGenerationProvider.BuildRequestUri(endpoint, deployment)` 組 `{endpoint}/openai/v1/chat/completions`。
  - body 多帶 `model`，`stream_options.include_usage` 照送，用量帳本不受影響。
- **轉錄維持舊路徑，版本內建**：
  - 常數 `LlmProviderSettings.DefaultTranscriptionApiVersion = "2025-03-01-preview"`。
  - `TranscriptionApiVersion` 改為選填，留空就用內建值，只有 Azure 淘汰這個版本時才需要填。
- **設定**：
  - 刪除 `LlmProviderSettings.ApiVersion`，以及 `LlmSettings.Validate()` 的「ApiVersion 不可為空白」檢查。
  - `appsettings.json` 拿掉 `ApiVersion`、`TranscriptionApiVersion` 兩行。
  - 舊設定檔或 user-secrets 若還留著這兩個鍵，會被直接忽略，不會報錯。
- **本機 user-secrets**（不在 repo 裡）：刪除 `ApiVersion`、`TranscriptionApiVersion` 兩個鍵，原檔備份為同目錄的 `secrets.json.bak-<時間>`。

## 測試

- `TextGenerationRequestTests`：v1 網址三種端點寫法、不帶 api-version、必填欄位空白時拋例外。
- `AzureOpenAiStreamingTests`：打 v1 網址、body 帶 `model`、仍送 `include_usage`。
- `TranscriptionRequestTests`：版本有設定時用設定值，留空時用內建值。
- `LlmSettingsTests`：
  - 留著舊的 `ApiVersion` 鍵時照常繫結、驗證通過。
  - 驗證錯誤從 3 筆變成 2 筆（只剩 Endpoint、Model）。
- 全部 1,383 則通過。
- 另外用實際程式碼（`AzureOpenAiTextGenerationProvider`、`AzureOpenAiTranscriptionProvider`）各打一次 Azure：文字生成回「測試」（用量 24／5 tokens），轉錄正確辨識整句。這只是一次性實測，測試檔已刪除，沒有進版控。

## 異動檔案

| 檔案 | 異動 |
|---|---|
| `Models/Systems/LlmSettings.cs` | 刪 `ApiVersion` 與驗證；轉錄版本內建 |
| `Business/Services/TextGeneration/AzureOpenAiTextGenerationProvider.cs` | v1 網址、body 帶 `model` |
| `Business/Services/Transcription/AzureOpenAiTranscriptionProvider.cs`、`AiUsage/AiUsagePricing.cs`、`TextGeneration/ITextGenerationProvider.cs` | 註解 |
| `Web/appsettings.json` | 刪兩個版本鍵；版本 1.0.28 |
| `Tests/*` | 上述測試 |
| `docs/operations/日誌與設定檔說明.md`、`正式部署與安全檢查清單.md`、`docs/prd/會議紀錄-prd.md`、`會議紀錄產生流程-prd.md`、`docs/guides/測試指南.md`、`readme.md` | 設定說明 |
