# WebAPI JWT ApiResult 設計 TODO

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
> - 受保護 CRUD 另以 `[HasPermission("resource:action")]` + `IPermissionChecker` 做動作級授權，資料可見性經 `ProjectAccessService`（主責＋協作團隊）—— 見 [認證授權與權限機制](../security/認證授權與權限機制.md)

## 本輪已完成
- [x] 目標說明：建立 Web API 統一回傳格式、DTO 邊界、JWT Bearer 認證與 Swagger 測試入口。
- [x] 現況盤點：已有 `ApiResult<T>`、DTO 專案、CRUD API、JWT settings、Auth API、Bearer 驗證與 Swagger security definition。
- [x] 實作待辦：已新增 `Errors`、`TraceId`、`Exception` 欄位並保留 `ErrorMessage`、`ErrorDetail`、`Timestamp` 相容欄位。
- [x] 驗收標準：成功、驗證失敗、401、403、500 皆以 `ApiResult<T>` 或 `ApiResult` 作為 JSON body；檔案下載成功例外原則保留待未來檔案 API 實作。
- [x] 相關檔案：`src/MeetingRecord/MeetingRecord.Dtos/Commons/ApiResult.cs`、`src/MeetingRecord/MeetingRecord.Web/Auth`、`src/MeetingRecord/MeetingRecord.Web/Controllers/AuthController.cs`、`src/MeetingRecord/MeetingRecord.Web/Filters`。
- [x] 備註風險：目前 `ApiResult.Exception` 依需求完整回傳堆疊資訊；正式環境資訊揭露風險已納入 release checklist。

## API 與 DTO 待辦
- [x] 建立泛型 `ApiResult<T>`，所有 Web API 固定回傳統一格式。
- [x] API 維持正確 HTTP 狀態碼，Body 統一包成 `ApiResult<T>`。
- [x] API 例外封裝到 `ApiResult<T>.Exception`。
- [x] API request/response 採用 DTO，不直接暴露 Entity。
- [x] 從 `appsettings.json` 建立 `JwtSettings` 強型別設定。
- [x] 實作 JWT Bearer access token。
- [x] 實作不落庫 signed refresh JWT，並標示限制。
- [x] 補 Swagger Bearer 授權設定。
- [x] Project/MyTask/Meeting CRUD API 加上 Bearer 授權。（MyTask/Meeting 已於 0.4.24 移除）
- [x] 補更完整的 API integration tests：已驗證 HTTP 401、403、validation 400、500、login、refresh、me、Project CRUD；403/500 透過測試專案 probe controller 觸發，不新增正式 API contract。
- [x] 評估是否導入 `/api/v1/...`，本輪依決策保留目前 `/api/...` 路由；策略文件：`docs/architecture/API Versioning 策略.md`。