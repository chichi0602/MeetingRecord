# WASM 遷移 M5：AI 對話紀錄 API（1.1.37 → 1.1.38）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.38
- 首次實作版本：1.1.38
- 最後核對日期：2026/11/11

使用者要求：

> 持續完成 M5 里程碑，在此幫我自動 commit。

---

## 一、原因

AI 對話紀錄頁 `/ai-call-logs` 動作多：
- 篩選、明細（完整送出內容與回應）、`?callId=` 深連結；
- CSV、JSON、PDF；
- 刪除、清除。

照 Token 用量的做法拆兩版：這版先開 API，1.1.39 再搬頁面。這版頁面仍是 Server 頁，畫面不變。

## 二、修改

### 2.1 新 `AiCallLogController`（`api/v1/ai-call-logs`，整個 Controller `[AdminOnly]`）

| 端點 | 說明 |
|---|---|
| `GET meta` | 是否正在記錄、保留天數、CSV 上限 |
| `GET filter-options` | 篩選下拉：出現過的使用者與模型 |
| `POST search` | 清單一頁（`AiCallLogQuery`），回 `PagedResult` |
| `GET by-call-id/{callId}` | 以呼叫識別碼找紀錄編號（Token 用量跳過來用），找不到 404 |
| `GET {id}` | 明細：列、內容、用量、同一趟工作。**查看也寫稽核**（內容是逐字稿等敏感全文） |
| `GET {id}/json` | 內容檔原文，寫稽核 |
| `POST {id}/pdf` | 伺服器產生 PDF，匯出者是 API 呼叫者，寫稽核 |
| `POST export/csv` | 伺服器產生 CSV（只有中繼資料、不含內容，最多 10,000 筆），寫稽核 |
| `DELETE {id}` | 刪除一筆與內容檔，不存在 404，寫稽核 |
| `POST purge` | 清除某日之前（不含當天），回筆數，寫稽核 |
| `POST clear-all` | 清空全部與內容檔目錄，寫稽核 |

**稽核**：動作代碼與內容同畫面（只記編號與識別碼、不記內容），目標 `AiCallLog`，另加 `via=Api`。

### 2.2 型別搬到 Dtos

- `AiCallLogEntry`、`AiCallLogRequest`、`AiCallLogMessage`、`AiCallLogImage`、`AiCallLogResponse`、`AiCallLogDetail`、`AiCallLogQuery`、`AiCallLogFilterOptions`、`AiCallLogUsage`、`AiCallLogAdapterModel` 從 Models 搬到 `Dtos/AiCallLogs`。
- 計算屬性標 `[JsonIgnore]`。
- 篩選選項的使用者以前是 tuple 清單，改成 `AiCallLogUserOption`。
- 內容檔的格式不變：寫檔另用自己的 JSON 設定，屬性名稱沒改。

### 2.3 共用

- **`AiCallLogBlocks`**（Dtos）：明細的區塊組法（完整對話／送出內容／回應）、轉錄送出內容的說明、截斷、「用量與費用」一行。從 `AiCallLogView` 抽出，伺服器匯出 PDF 與頁面共用；`AiCallLogServiceTests` 改引用。
- **`AiCallLogCsv.Columns`**（`Web/Export`）：從 `AiCallLogView.CsvColumns` 搬來。

### 2.4 文件與版本

- 文件：
  - AI 對話紀錄 PRD、Web API 端點目錄、認證授權；
  - 09 號「十、之六」。
- 版本 1.1.37 → 1.1.38。

## 三、外部可見的行為變更

| 項目 | 以前 | 現在 |
|---|---|---|
| AI 對話紀錄 | 只有網頁畫面 | 另有 `api/v1/ai-call-logs` 11 支 API，只限管理者 |
| AI 對話紀錄頁 | — | 不變，仍是 Server 頁；1.1.39 搬到 WASM |

## 四、測試

- **L2 `AiCallLogApiTests`**（假 PDF 的測試主機，紀錄經真的 `AiCallLogService.RecordAsync` 寫入）：
  - 未登入 401，非管理者打設定、清單、明細、刪除、全部清除都是 403；
  - 清單依篩選、選項經 JSON 還原後使用者正確；
  - 以呼叫識別碼找得到、找不到 404；
  - 明細經 JSON 還原後內容完整，查看寫稽核 `via=Api`；
  - JSON、PDF、CSV 由伺服器產生並寫稽核，CSV 不含內容；
  - 刪除後再刪 404，清除某日之前會刪掉並寫稽核。
- **單元**：`AiCallLogServiceTests`（改引用 `AiCallLogBlocks`、`AiCallLogCsv`）、`AiCallGatewayTests` 照過。
- **抓錯檢查**：暫時拿掉查看明細的稽核 → L2 失敗；已還原。
- **全套**：單元／API 與端對端照跑，MON-01／02 的 `/ai-call-logs`（仍是 Server 頁）照過。
