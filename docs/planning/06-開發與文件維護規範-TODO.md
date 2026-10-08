# 開發與文件維護規範 TODO

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
> - 現行文件維護規則見 [維護規範](../operations/維護規範.md)（同步表、表頭更新與封存規則）；PowerShell 寫檔請用 `-Encoding utf8BOM`

## 文件規範
- [x] 目標說明：所有補強工作都要同步留下繁體中文文件，讓日後能逐項勾選與驗收。
- [x] 現況盤點：本輪六份 TODO 文件已更新為 UTF-8 BOM、繁體中文 Markdown checkbox 格式。
- [x] 實作待辦：完成後即更新 checkbox、驗收指令、剩餘風險與下一步。
- [x] 驗收標準：文件以 UTF-8 BOM 寫入，避免繁體中文亂碼。
- [x] 相關檔案：`docs/planning/01-專案總覽與定位-TODO.md` 到 `docs/planning/06-開發與文件維護規範-TODO.md`。
- [x] 備註風險：已掃描 `src`、`docs`、`scripts`、`.github` 主要文字檔，未發現 replacement character；終端顯示亂碼多半與主控台字型/編碼顯示有關，後續若要修正文案可另開逐檔校稿任務。

## 維護流程待辦
- [x] 每次完成功能後更新 TODO checkbox。
- [x] 每次驗證後記錄 build/test/vulnerability scan 結果。本輪驗證：Release build 成功、solution test 實際執行 9 個測試並全數通過、弱點掃描未列出風險；CI 已設定 `NUGET_HTTP_TIMEOUT_SECONDS=180`，降低 NuGet 來源偶發逾時造成假失敗。
- [x] 建立自動檢查文件 UTF-8 BOM 的腳本與 CI step。腳本：`scripts/Test-DocsEncoding.ps1`。
- [x] 建立文件更新 PR checklist，正式部署與安全檢查清單已納入驗收流程。
- [x] 將主要設計同步到長期文件：新增 API versioning、DTO 邊界、release checklist 與 seed 設定文件，並保留 TODO 作為追蹤入口。