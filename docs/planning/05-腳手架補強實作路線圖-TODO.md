# 腳手架補強實作路線圖 TODO

- 文件版本：1.0
- 文件狀態：進行中
- 現行系統版本：0.4.24
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/17

## 第一階段：API 與 JWT 基礎
- [x] 目標說明：先補齊日後所有系統都會需要的 API contract、JWT、Swagger、測試與 CI。
- [x] 現況盤點：第一階段已完成，並保留既有 API 路由相容性。
- [x] 實作待辦：已新增 Auth DTO、JWT service、AuthController、ApiExceptionFilter、ApiValidationFilter 結構化錯誤、測試專案與 CI。
- [x] 驗收標準：build 成功、測試通過、弱點掃描未列出風險套件。
- [x] 相關檔案：`src/MeetingRecord/MeetingRecord.Web/Auth`、`src/MeetingRecord/MeetingRecord.Web/Controllers`、`src/MeetingRecord/MeetingRecord.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：目前 refresh token stateless，不支援單顆 token 撤銷；限制已寫入正式部署安全清單。

## 第二階段：品質收斂
- [x] 修正 50 個 build warnings 中的高價值項目，目前剩 0 個 compiler/analyzer warning，已記錄於文件。
- [x] 拆分 `Program.cs`，已先抽出 localization/application services 與 middleware/static files extension；database/seed 深度拆分保留為後續低風險重構。
- [x] 加入 Web API integration tests，覆蓋 401、login、refresh、me、Project CRUD Bearer 授權與 validation 400；solution 已修正為會實際建置並執行測試專案。
- [x] 補正式部署設定範本，說明 JWT key、資料庫路徑、NLog 路徑、Swagger UI 暴露策略。文件：`docs/operations/正式部署與安全檢查清單.md`。

## 第三階段：腳手架產品化
- [x] 建立新專案改名與替換清單。文件：`docs/guides/腳手架新專案啟動流程.md`。
- [x] ~~建立 SQL Server 切換與 migration 操作文件。~~ **已於 0.4.24 作廢**：SQL Server 支援整體移除，該文件已刪除。
- [x] 建立預設帳號初始化設定流程：新增 `BootstrapSettings` 支援覆寫 support 帳號與密碼；強制改密碼功能化仍可依正式需求另開任務。
- [x] 建立 API versioning 策略，但不破壞目前 `/api/...`。文件：`docs/architecture/API Versioning 策略.md`。
- [x] 建立新專案初始化與 CRUD 模組骨架腳本：`scripts/New-StarterProject.ps1`、`scripts/New-CrudModule.ps1`。
- [x] 補齊 Production 啟動安全檢查、health checks、CORS、rate limiting 與 `/api/v1/...` 平行路由。
