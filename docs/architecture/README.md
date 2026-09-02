# architecture — 系統架構與設計規範

- 文件版本：1.1
- 文件狀態：維護中
- 現行系統版本：0.4.33
- 首次實作版本：0.4.23
- 最後核對日期：2026/09/01

本目錄收納系統架構、資料模型、API / DTO 設計規範與開發慣例。動手改本專案前，請先讀《開發慣例與限制速查》。

| 文件 | 說明 |
|------|------|
| [開發慣例與限制速查](開發慣例與限制速查.md) | **AI／開發者必讀**：分層、SQLite migration、追蹤清理、權限同步等不變量速查 |
| [架構總覽](架構總覽.md) | 各專案分層、依賴方向、啟動流程、DI 註冊清單 |
| [系統功能總覽](系統功能總覽.md) | 功能面全貌：端到端流程、每個選單頁在做什麼、權限與背景工作、**已知問題與限制** |
| [資料模型與資料庫](資料模型與資料庫.md) | `BackendDBContext`、主要 Entity、關聯與刪除政策 |
| [DTO 與模型邊界規範](DTO%20與模型邊界規範.md) | API / UI / Business / Entity 資料邊界原則與新 CRUD 模組待辦 |
| [Web API 設計慣例](Web%20API%20設計慣例.md) | Controller 樣板、`ApiResult<T>`、`PagedResult<T>`、Search DTO |
| [Web API 端點目錄](Web%20API%20端點目錄.md) | 全部 controller 的實際路由與授權／回傳型別對照表 |
| [API Versioning 策略](API%20Versioning%20策略.md) | `/api/...` 與 `/api/v1/...` 平行路由、Swagger v1 分組與後續導入策略 |

> 返回 [文件總索引](../README.md)
