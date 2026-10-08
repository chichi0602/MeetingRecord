# 缺口與風險清單 TODO

- 文件版本：1.0
- 文件狀態：已封存（樣板規劃階段快照，不再維護）
- 現行系統版本：0.4.23
- 首次實作版本：0.1.61
- 最後核對日期：2026/07/14

> 📌 本文為**樣板（NET10-Blazor-Starter）時期的規劃快照**，保留當時的判斷脈絡；本專案已於 0.4.25 更名並發展為 MeetingRecord（AI 會議紀錄系統）。
> 系統現況請以 [`docs/prd/`](../prd/README.md) 的能力覆蓋矩陣與[系統功能總覽](../architecture/系統功能總覽.md)為準；各次異動的落地細節見 [`docs/changelog/`](../changelog/README.md)。
> ⚠️ 本檔頭的「現行系統版本／最後核對日期」**刻意停在快照當時**，不隨系統版本推進 ——
> 它記錄的是「當時看到的樣子」，更新它反而會讓人誤以為內容經過重新查證。

> 📌 現況差異（2026/10/08 核對 0.4.118）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - 本專案已不是「腳手架」，而是 MeetingRecord AI 會議紀錄系統（0.4.25 更名）；文中「腳手架定位」「複製成新系統」等描述不再適用
> - Web API 現為 Auth、ExternalAuth（Google 登入）、Project、Category、Team、Meeting、Todo、PromptTemplate（另有樣板遺留的 WeatherForecast）—— 見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)
> - CI 仍為 `.github/workflows/dotnet-ci.yml`（build、test、文件編碼、弱點掃描）—— 見 [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md)
> - 預設帳號另有「首次登入強制改密碼」（0.4.113），停用帳號在登入頁即擋下 —— 見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)
> - 本系統只支援 SQLite（0.4.24 起）

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
