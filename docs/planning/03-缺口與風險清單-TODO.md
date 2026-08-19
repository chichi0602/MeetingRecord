# 缺口與風險清單 TODO

- 文件版本：1.0
- 文件狀態：進行中
- 現行系統版本：0.4.23
- 首次實作版本：0.1.61
- 最後核對日期：2026/07/14

## 已處理風險
- [x] 目標說明：記錄安全、套件、測試、CI、warning、密碼與預設帳號等風險，避免腳手架問題被複製到新系統。
- [x] 現況盤點：AutoMapper 已為 16.1.1；套件弱點掃描目前未列出已知易受攻擊套件。
- [x] 實作待辦：已新增 JWT Bearer、ApiResult 例外封裝、測試專案與 CI。
- [x] 驗收標準：`dotnet list src/MeetingRecord/MeetingRecord.slnx package --vulnerable --include-transitive` 未列出弱點套件。
- [x] 相關檔案：`src/MeetingRecord/MeetingRecord.Web/MeetingRecord.Web.csproj`、`src/MeetingRecord/MeetingRecord.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：`ApiResult.Exception` 已改為依 `Security:ReturnExceptionDetails` 控制；Production 預設不回傳完整堆疊資訊。

## 尚待處理風險
- [x] 將 build warning 從 56 個收斂到 0 個；剩餘僅有 .NET preview SDK 提示訊息，非程式碼 warning。
- [x] 將 `appsettings.json` 內開發用 JWT signing key 改成部署環境 secret 的要求已納入 release checklist；實際正式 secret 需由部署環境提供。
- [x] 強化預設帳號與密碼策略：新增 `BootstrapSettings`，可用設定或環境變數覆寫預設 support 帳號與密碼；正式部署替換流程已寫入 checklist。
- [x] 補 refresh token 不落庫限制說明：目前無法可靠撤銷單一 refresh token，只能靠 signing key 輪替或縮短有效期。
- [x] 建立正式部署前安全檢查清單，包含 HTTPS、Swagger UI 暴露範圍、CORS、secret、日誌敏感資訊。文件：`docs/operations/正式部署與安全檢查清單.md`。
- [x] Production 啟動安全檢查已納入 JWT key、support 預設密碼與 Swagger 策略。
