# 現有架構盤點 TODO

- 文件版本：1.0
- 文件狀態：已封存（樣板規劃階段快照，不再維護）
- 現行系統版本：0.4.24
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/17

> 📌 本文為**樣板（NET10-Blazor-Starter）時期的規劃快照**，保留當時的判斷脈絡；本專案已於 0.4.25 更名並發展為 MeetingRecord（AI 會議紀錄系統）。
> 系統現況請以 [`docs/prd/`](../prd/README.md) 的能力覆蓋矩陣與[系統功能總覽](../architecture/系統功能總覽.md)為準；各次異動的落地細節見 [`docs/changelog/`](../changelog/README.md)。
> ⚠️ 本檔頭的「現行系統版本／最後核對日期」**刻意停在快照當時**，不隨系統版本推進 ——
> 它記錄的是「當時看到的樣子」，更新它反而會讓人誤以為內容經過重新查證。

> 📌 現況差異（2026/10/08 核對 0.4.118）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - 本專案已不是「腳手架」，而是 MeetingRecord AI 會議紀錄系統（0.4.25 更名）；文中「腳手架定位」「複製成新系統」等描述不再適用
> - Web API 現為 Auth、ExternalAuth（Google 登入）、Project、Category、Team、Meeting、Todo、PromptTemplate（另有樣板遺留的 WeatherForecast）—— 見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)
> - CI 仍為 `.github/workflows/dotnet-ci.yml`（build、test、文件編碼、弱點掃描）—— 見 [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md)
> - `MyTaskController` 已移除；`MeetingController` 是 0.4.27 起本系統自己的「會議紀錄」API，與樣板當年的 Meeting 模組無關
> - 分層與服務註冊現況見 [架構總覽](../architecture/架構總覽.md)

## 分層現況
- [x] 目標說明：盤點腳手架分層，讓後續系統能從清楚的責任邊界開始擴充。
- [x] 現況盤點：目前包含 `AccessDatas`、`Business`、`Dtos`、`Models`、`Share`、`Web`、`Tests` 七個專案。
- [x] 實作待辦：已新增測試專案並加入 solution；Web API 已使用 DTO 作為 request/response，不直接暴露 Entity。
- [x] 驗收標準：`ProjectController`、`MyTaskController`、`MeetingController` 使用 Create/Update/Search/Dto 類別作為 API 邊界。（後兩者已於 0.4.24 移除）
- [x] 相關檔案：`src/MeetingRecord/MeetingRecord.Dtos`、`src/MeetingRecord/MeetingRecord.Web/Controllers`、`src/MeetingRecord/MeetingRecord.Tests`。
- [x] 備註風險：已新增 `ControllerApiResponseExtensions`，Project/MyTask/Meeting controller 的 500 `ApiResult` 封裝已改用共用 helper；後續仍可再深化為 action pipeline 或 service result pattern。

## 架構整理待辦
- [x] 拆分 `Program.cs`，建立 service registration extension 與 middleware extension。已新增 `Extensions/ServiceCollectionExtensions.cs` 與 `Extensions/ApplicationBuilderExtensions.cs`。
- [x] 修正 `Program.cs` 內 ASP0000 `BuildServiceProvider` warning，改由 `app.Services.GetRequiredService<ILogger<Program>>()` 取得 logger。
- [x] 清理 `ProjectRepository` 的 self-assignment warning，保留目前 API shape 無 related include 的註解。
- [x] 評估 `Models` 與 `Dtos` 專案責任邊界，避免 AdapterModel 與 API DTO 混用。文件：`docs/architecture/DTO 與模型邊界規範.md`。