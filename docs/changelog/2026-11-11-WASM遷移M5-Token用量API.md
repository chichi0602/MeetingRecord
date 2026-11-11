# WASM 遷移 M5：Token 用量 API（1.1.35 → 1.1.36）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.36
- 首次實作版本：1.1.36
- 最後核對日期：2026/11/11

使用者要求：

> 持續完成 M5 里程碑，在此幫我自動 commit。

---

## 一、原因

Token 用量頁 `/token-usage` 動作多：
- 本月概況、上限列、篩選、統計、明細；
- CSV、PDF 匯出；
- 刪除、清除。

所以拆兩版：這版先開 API，1.1.37 再把頁面搬到 WASM。這版頁面仍是 Server 頁，畫面不變。

## 二、修改

### 2.1 新 `AiUsageController`（`api/v1/ai-usage`，整個 Controller `[AdminOnly]`）

| 端點 | 說明 |
|---|---|
| `GET overview` | 本月概況（固定全部功能、本月至今）、全系統上限、每人上限前五名、每人上限金額、保留天數、CSV 匯出上限 |
| `GET filter-options` | 篩選下拉：帳本裡出現過的使用者（最近一次的名稱）與模型 |
| `POST report` | 內容是篩選條件，回篩選範圍的統計：合計、每日、依使用者／功能／模型、圖表切片 |
| `POST rows` | 明細一頁（只能依時間排序） |
| `GET {id}` | 單筆明細，連同同一次呼叫的 AI 對話紀錄編號；不存在回 404 |
| `POST export/csv` | 伺服器產生 CSV（最多 10,000 筆） |
| `POST export/pdf` | 伺服器產生 PDF 報表；匯出者是 API 呼叫者帳號 |
| `DELETE {id}` | 刪除一筆；批次刪除由畫面逐筆呼叫；不存在回 404 |
| `POST purge` | 清除某日之前（不含當天），回筆數 |
| `POST clear-all` | 清空全部，回筆數 |

- 非管理者回 403 並留稽核，未登入回 401。
- **稽核改由伺服器寫**：`AiUsage.Export`／`ExportPdf`／`Delete`／`Purge`／`ClearAll`，目標 `AiUsageLog`，內容同畫面，另加 `via=Api`。
- 上限列讀不到時只記錯誤、不擋整頁，同畫面。

### 2.2 型別搬到 Dtos

- **用量型別**：`AiUsageModels.cs` 從 Business 搬到 `Dtos/AiUsage`，包含：
  - `AiUsageSummary`、`AiUsageFilter`、`AiUsageQuery`、`AiUsageRow`；
  - `AiUsageReport`、`AiUsageGroupRow`、`AiUsageDailyRow`；
  - `AiUsageFilterOptions`、`AiUsageDetail`。
- **額度型別**：`AiQuotaStatus`、`AiQuotaScope`、`AiQuotaUserUsage` 從 `AiQuotaService.cs` 搬到 `Dtos/AiUsage/AiQuotaModels.cs`。
- **計算屬性**：標 `[JsonIgnore]`，伺服器與頁面各自算。
- **使用者清單**：`AiUsageFilterOptions.Users` 以前是 tuple 清單，JSON 序列化不出來，改用 `AiUsageUserOption(UserId, UserName)`。
- **CSV 欄位**：從 `TokenUsageView.CsvColumns` 搬到 `Web/Export/AiUsageCsv.Columns`，伺服器與目前的 Server 頁共用；`TokenUsageReportExporterTests` 改引用。
- 新增 `AiUsageOverviewDto`、`AiUsageDetailResponseDto`、`AiUsagePurgeRequestDto`。

### 2.3 文件與版本

- 文件：
  - Token 用量 PRD、Web API 端點目錄、認證授權；
  - 09 號「十、之六」與 §4.10 型別清單。
- 版本 1.1.35 → 1.1.36。

## 三、外部可見的行為變更

| 項目 | 以前 | 現在 |
|---|---|---|
| Token 用量 | 只有網頁畫面 | 另有 `api/v1/ai-usage` 10 支 API，只限管理者 |
| Token 用量頁 | — | 不變，仍是 Server 頁；1.1.37 搬到 WASM |

## 四、測試

- **L2 `AiUsageApiTests`**（假 PDF 的測試主機）：
  - 未登入 401，非管理者打總覽、統計、匯出、刪除、全部清除都是 403；
  - 總覽、統計、明細分頁、篩選選項都依篩選條件，選項經 JSON 還原後使用者名稱正確；
  - 單筆明細、不存在 404；
  - CSV 與 PDF 由伺服器產生，稽核 `via=Api`；
  - 刪除後再刪 404，稽核記功能別；
  - 清除某日之前會刪掉、稽核記日期。
- **單元**：`AiUsageAnalysisServiceTests`（選項改用新型別）、`AiQuotaServiceTests`、`TokenUsageReportExporterTests`（CSV 欄位改引用）照過。
- **抓錯檢查**：暫時拿掉 `[AdminOnly]` → L2 非管理者案例與授權守門掃描都失敗；已還原。
- **全套**：單元／API 與端對端照跑，MON-01／02 的 `/token-usage`（仍是 Server 頁）照過。
