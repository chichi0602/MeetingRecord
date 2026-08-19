# 現有架構盤點 TODO

- 文件版本：1.0
- 文件狀態：進行中
- 現行系統版本：0.4.24
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/17

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