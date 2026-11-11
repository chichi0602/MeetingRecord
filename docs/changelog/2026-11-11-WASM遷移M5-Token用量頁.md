# WASM 遷移 M5：Token 用量頁搬到 WASM（1.1.36 → 1.1.37）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.37
- 首次實作版本：1.1.37
- 最後核對日期：2026/11/11

使用者要求：

> 持續完成 M5 里程碑，在此幫我自動 commit。

---

## 一、原因

1.1.36 開好 Token 用量 API，這版把 `/token-usage`（舊網址 `/ai-usage`）搬到 WASM，畫面不變。

## 二、修改

### 2.1 頁面

- **搬到 Client**：
  - `TokenUsageView.razor(.cs/.css)` 搬到 `Web.Client/Pages/AiManagement/TokenUsagePage.*`，`@page "/token-usage"` 與 `"/ai-usage"`；
  - `TokenUsageGroupTable` 也搬到 Client；
  - 刪掉 Server 的頁面。
- **資料**：走 `AiUsageApi`（`api/v1/ai-usage`）。管理者判斷用 `PermissionView.IsAdmin`。
- **CSV、PDF**：由伺服器產生，畫面下載後存檔。
- **刪除、清除**：呼叫 API；批次刪除逐筆呼叫。稽核都由伺服器寫，頁面不再寫稽核。
- **連到 AI 對話紀錄**：明細的「查看 AI 對話紀錄」照舊連 `/ai-call-logs?callId=…`。那頁還是 Server 頁，會整頁載入。
- `WasmRoutes` 加 `/token-usage`、`/ai-usage`。

### 2.2 共用

- **格式化**：`AiUsageMetrics.FormatAmount／FormatConverted／FormatTokens／FormatAudio` 的實作搬到 `Share/Helpers/AiUsageFormatter`，Business 的同名方法轉呼叫它，既有呼叫端與測試不用改。
- **上限值**：`MaxFilledDays`、`MaxExportRows` 移到 `Dtos/AiUsage/AiUsageConstants`，伺服器與頁面共用一份。

### 2.3 途中發現並修正：JSON 原始碼產生器的別名撞名

第一次跑端對端，MON-01 的 `/token-usage` 整頁變成錯誤畫面，診斷抓到 `NotSupportedException: NoMetadataForType, AiUsagePagedResult`。

**原因**：`ApiResult<AiUsagePagedResult>` 取了別名 `TypeInfoPropertyName = "AiUsagePagedResult"`，跟產生器替內層型別自動取的名字撞名，內層型別就沒有中繼資料。
- 建置照樣成功，只發警告 SYSLIB1031；
- 要等實際讀到那支 API 才炸。

**修正**：
- 別名改成 `AiUsageRowsResult`；
- `MeetingRecord.Web.Client.csproj` 把 SYSLIB1031 升級成錯誤，撞名直接建置失敗；
- 新增單元測試 `ClientJsonContextTests`：每個註冊型別與它的屬性型別都要拿得到中繼資料。暫時改回撞名時測試會失敗。

### 2.4 文件與版本

- 文件：
  - Token 用量 PRD、畫面與欄位字典、對話窗規範；
  - 開發慣例與限制速查（JSON 別名撞名）、測試指南；
  - 09 號「十、之六」。
- 版本 1.1.36 → 1.1.37。

## 三、外部可見的行為變更

| 項目 | 以前 | 現在 |
|---|---|---|
| Token 用量頁 | Blazor Server 頁 | WASM 頁，畫面不變；從還沒搬的頁切過來會整頁載入一次 |
| 匯出、刪除、清除的稽核 | 由畫面寫 | 由伺服器寫，內容另加 `via=Api`（1.1.36 起 API 就是這樣） |

## 四、測試

- **端對端新增 AIM-01**：在 WASM 版 Token 用量頁打開一筆明細看得到內容；匯出 CSV 下載成功，伺服器寫了稽核。
- **端對端**：MON-01、MON-02 的 `/token-usage` 一行不改照過，全套照跑。
- **單元**：
  - 新增 `ClientJsonContextTests`；
  - `AiUsageAnalysisServiceTests`、`TokenUsageReportExporterTests` 與原始碼掃描照過。
- **抓錯檢查**：別名改回撞名 → `ClientJsonContextTests` 失敗；已還原。
