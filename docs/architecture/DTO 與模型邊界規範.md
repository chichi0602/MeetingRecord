# DTO 與模型邊界規範

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：1.1.53
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/12

## 目標
- [x] 明確定義 API、UI、Business、Entity 的資料邊界，避免腳手架日後擴充時直接暴露資料庫 Entity。

## 邊界原則
- [x] Web API request/response 只使用 `MeetingRecord.Dtos`，不可直接接收或回傳 `AccessDatas.Models` Entity。
- [x] Entity 僅作為 EF Core persistence model 使用。
- [x] WASM 前端（`MeetingRecord.Web.Client`）只參考 `Dtos`／`Share`（`WebClientArchitectureTests` 只允許再加 `Models`），頁面拿到的資料一律是 API 回傳的 DTO。
- [x] `MeetingRecord.Models/AdapterModel` 的 AdapterModel 是 Business 服務的內部模型，不直接當 API 回應。例外是稽核紀錄、系統例外、AI 對話紀錄三支管理 API 回傳的 `AuditLogAdapterModel`／`ExceptionLogAdapterModel`／`AiCallLogAdapterModel`——它們放在 `MeetingRecord.Dtos`，實質就是 DTO；新 API 一律定義 `XxxDto`。
- [x] Controller 呼叫 Business 服務，由服務以 AutoMapper 或明確 mapping 在 DTO 與 Entity 之間轉換。

## 新 CRUD 模組待辦
- [ ] 新增 `XxxDto` 作為 response DTO。
- [ ] 新增 `XxxCreateUpdateDto` 作為 create/update request DTO。
- [ ] 新增 `XxxSearchRequestDto` 作為查詢 request DTO。
- [ ] 新增 mapping profile，並補 DTO 欄位驗證 attribute。
- [ ] 新增 integration tests，確認 JSON body 不含不該暴露的 Entity 導覽屬性或內部欄位。

## 驗收標準
- [x] 全部 Controller（含 `ProjectController`、`CategoryController`、`TeamController`、`PromptTemplateController`、`MeetingController`、`TodoController` 與 WASM 頁用的 API）均使用 `MeetingRecord.Dtos` 的型別作為 API 邊界。
- [x] integration tests 已驗證 Project CRUD 成功 response 可用 DTO contract 反序列化。
- [ ] 日後每新增一個 API Controller，都要在 PR checklist 檢查 DTO 邊界。

## 備註風險
- [ ] 目前 AdapterModel 與 API DTO 並存，命名與責任邊界需在 code review 中持續守住；WASM 端用到的型別要能以 `ClientJsonContext` 原始碼產生器序列化。