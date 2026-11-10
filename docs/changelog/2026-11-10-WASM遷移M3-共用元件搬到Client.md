# WASM 遷移 M3：共用元件搬到 WASM 專案（1.1.16 → 1.1.17）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.17
- 首次實作版本：1.1.17
- 最後核對日期：2026/11/10

使用者要求：

> M2 做完後自動做 M3，M3 也一次做完（每版全綠自動 commit 推送），做到 M3 里程碑報告再停。

---

## 一、原因

接下來要搬的待辦、會議、專案頁會用到拖放上傳區、Markdown 編輯器、專案範圍切換，以及剪貼簿、下載、文字編輯器、待辦自動欄寬等前端互動；這些原本放在伺服器專案，WASM 頁拿不到。

## 二、修改

- **元件搬到 `Web.Client/Components/Commons`**（命名空間不變）：`FileDropZone`、`MarkdownEditorModal`、`ProjectScopeSwitch`。
- **前端互動類別搬到 `Web.Client/Services`**（命名空間仍是 `MeetingRecord.Web.Services`）：`ClipboardInterop`、`FileDownloadInterop`、`TextEditorInterop`、`TodoAutoFitInterop`；WASM 端也註冊一份。使用說明頁的 `HelpViewInterop` 留在伺服器（M4 再處理）。
- **`MarkdownRenderer`、`TextSearchHelper` 搬到 Share**（命名空間改成 `MeetingRecord.Share.Helpers`），Markdig 套件從 Business 移到 Share；匯出、使用說明、AI 問答與 Markdown 編輯器都用同一份。Share 對測試開放 internal（`SanitizeUrl` 的測試）。
- 舊版頁面照用：伺服器專案參考 WASM 專案；畫面、行為都不變。
- WASM 下載量（Brotli 合計）4.66 → 4.82 MB，多出來的主要是 Markdig；只下載一個語系檔約 4.47 MB，仍在 5 MB 門檻內。
- 下載 API（`DownloadApi`）改在第一個用到下載的頁面那版做，不先做用不到的程式。
- 文件：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（共用元件與前端互動放 Client、Markdown 一律用 Share 的 `MarkdownRenderer`）、會議紀錄／待辦事項／專案項目／使用說明／AI 問答 PRD 的檔案路徑、09 號「十、之四」。
- 版本 1.1.16 → 1.1.17。

## 三、測試

- 程式只搬位置，沒有改邏輯：既有的 `MarkdownRendererTests`、`TextSearchHelperTests`、`HelpManualTests`、匯出測試，以及掃描元件原始碼的測試（對話窗尺寸、提示文字等，已同時掃 Web 與 Client）照過。
- 全套單元／API 與端對端照跑；用到這些元件的 Server 頁情境（會議、專案、待辦、AI 問答、MTG／PRJ／TDO／AI 系列）照過。
