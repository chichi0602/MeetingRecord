# 專案總覽與定位 TODO

- 文件版本：1.0
- 文件狀態：進行中
- 現行系統版本：0.4.24
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/17

## 腳手架定位
- [x] 目標說明：本專案定位為未來開發 .NET 10 Blazor + Web API 系統的預設腳手架，提供 UI、資料存取、DTO、API、認證授權、日誌與文件基礎。
- [x] 現況盤點：目前已有 Blazor Web App、EF Core SQLite 預設、NLog、AutoMapper、DTO 專案、Project/MyTask/Meeting CRUD API、Swagger、Cookie 登入與 JWT Bearer API 認證基礎。（MyTask/Meeting 已於 0.4.24 移除）
- [x] 實作待辦：已完成第一階段 API/JWT/測試/CI 補強，後續要持續收斂 nullable warning、Program.cs 結構、預設帳號安全與 API versioning。
- [x] 驗收標準：`dotnet build src/MeetingRecord/MeetingRecord.slnx -v:minimal --no-incremental` 可成功建置；目前摘要為 56 warnings、0 errors。
- [x] 相關檔案：`src/MeetingRecord/MeetingRecord.slnx`、`src/MeetingRecord/MeetingRecord.Web`、`src/MeetingRecord/MeetingRecord.Dtos`、`src/MeetingRecord/MeetingRecord.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：目前仍有 .NET preview SDK 訊息與 0 個 build warning；`Program.cs` 已初步拆分，預設種子帳號已改為可透過 `BootstrapSettings` 覆寫。

## 後續定位待辦
- [x] 將此腳手架整理成新專案建立流程，包含改名、資料庫路徑、JWT signing key、預設管理員帳號與部署設定替換步驟。文件：`docs/guides/腳手架新專案啟動流程.md`。
- [x] ~~補充 SQL Server 切換教學，但保留 SQLite 作為預設開發資料庫。~~ **已於 0.4.24 作廢**：SQL Server 支援整體移除，SQLite 為唯一資料庫，該文件已刪除。
- [x] 建立 release checklist，避免腳手架被複製後仍沿用開發用 secret 或預設帳密。文件：`docs/operations/正式部署與安全檢查清單.md`。