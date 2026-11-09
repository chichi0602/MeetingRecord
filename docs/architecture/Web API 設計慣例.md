# Web API 設計慣例

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：1.0.63
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/09

## 目的
本文件記錄 MeetingRecord Web API 的固定設計規範（沿用自本系統源頭 NET10-Blazor-Starter 樣板），未來新增 API 時應遵守同一套 contract，讓前端與外部用戶端能用一致格式處理成功、失敗、驗證錯誤、授權錯誤與例外。

## Controller 規範
- API Controller 放在 `src/MeetingRecord/MeetingRecord.Web/Controllers/`。
- 路由需同時提供 `[Route("api/[controller]")]` 與 `[Route("api/v1/[controller]")]`；新用戶端優先使用 `/api/v1/...`。
- Controller 必須使用 `[ApiController]` 與 `[ApiValidationFilter]`。
- 需要保護的 CRUD API 必須加上 `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`。
- 除驗證身分外，受保護動作方法**必須**以 `[HasPermission("resource", "action")]` 標註做**功能級／動作級授權**（見「功能級／動作級授權」節）。
- API request/response 必須使用 DTO，不可以直接接收或回傳 Entity。
- Entity 與 DTO 轉換優先使用 AutoMapper profile 維護。

## 統一回傳格式
所有一般 Web API 回應固定使用 `MeetingRecord.Dtos.Commons.ApiResult<T>` 或非泛型 `ApiResult`。

標準欄位：
- `Success`：是否成功。
- `StatusCode`：HTTP 狀態碼。
- `Message`：使用者可讀訊息。
- `Data`：成功時的回傳資料。
- `Errors`：欄位或規則錯誤集合。
- `TraceId`：請求追蹤識別碼。
- `Exception`：例外資訊。Development 或 `Security:ReturnExceptionDetails=true` 時回傳；Production 預設不回傳。

相容欄位：
- `ErrorMessage`：舊版錯誤訊息欄位，暫時保留。
- `ErrorDetail`：舊版錯誤詳細欄位，暫時保留。
- `Timestamp`：舊版時間戳欄位，暫時保留。

## HTTP 狀態碼
API 必須維持語意正確的 HTTP 狀態碼，Body 再包成 `ApiResult<T>`。

| 狀態碼 | 使用情境 |
| --- | --- |
| 200 | 查詢、更新、刪除等成功 |
| 400 | ModelState 或商業規則驗證失敗 |
| 401 | 未登入、未提供 Bearer token、token 無效 |
| 403 | 已登入但權限不足（由 `HasPermissionAttribute` 判定產生） |
| 404 | 找不到指定資源 |
| 409 | 資料衝突，例如名稱重複 |
| 500 | 未預期例外 |

## JWT 與 Swagger
Swagger 已設定 Bearer security definition。開發者可在 Swagger UI 透過 Authorize 輸入 JWT access token 後測試受保護 API。

JWT access token 與 refresh token 由 `AuthController` 提供：
- `POST /api/Auth/login`
- `POST /api/Auth/refresh`
- `GET /api/Auth/me`
- `POST /api/v1/Auth/login`
- `POST /api/v1/Auth/refresh`
- `GET /api/v1/Auth/me`

## 功能級／動作級授權
身分驗證（`[Authorize]`）之外，受保護 CRUD **每個動作方法**再以 `[HasPermission(頁面鍵, 動作)]`（`src/MeetingRecord/MeetingRecord.Web/Filters/HasPermissionAttribute.cs`）強制授權：

- 由 `IPermissionChecker`（`MeetingRecord.Business/Services/Other/PermissionChecker.cs`，UI 與 API 共用的單一 RBAC 權威來源）判權；**管理員短路**一律通過。
- 動作對應：GET/`search`→`view`、POST→`create`、PUT→`edit`、DELETE→`delete`（動作常數見 `MeetingRecord.Share/Helpers/PermissionKeys.cs`）。
- **權限鍵兩型**：裸頁面鍵（如 `專案項目`）＝該頁全動作（向後相容）；動作鍵（如 `專案項目:edit`）＝特定動作。「唯讀角色」只給 `專案項目:view`。
- 無權限一律回 **403**，Body 維持 `ApiResult` 格式並標示缺少的權限鍵。多角色時權限取聯集。
- 詳見 [認證授權與權限機制](../security/認證授權與權限機制.md)。
- **授權守門測試（1.0.58）**：`ApiAuthorizationConventionTests` 以反射掃描所有 Controller 的每個 action，每一個必須是下列其一，否則測試失敗並列出缺的端點：
  1. 在**匿名白名單**（目前只有 `AuthController.Login`／`Refresh` 與 `ExternalAuthController.*`）；
  2. 有 `[Authorize]` **且**有 `[HasPermission]`；
  3. 有 `[Authorize]` 且在**「只需登入」白名單**（目前只有 `AuthController.Me`）。
  另外每個 `[Authorize]` 都必須寫 `AuthenticationSchemes`（預設 scheme 是網頁 Cookie，沒寫會靜靜改用 Cookie 驗證）。白名單每一項都要寫理由，**不要為了讓測試變綠就把端點加進去**。
- **授權矩陣測試（1.0.58）**：`ApiIntegrationTests.Authorization.cs` 對分類、提示詞、團隊、會議每支端點驗三件事：沒權杖 401；角色有該頁其他所有動作、唯獨缺這個動作時 403（並留 `PermissionDenied` 稽核）；角色只有這一個動作時成功。新增 Controller 時把端點加進 `GuardedEndpoints`，建測試帳號用 `CreateApiUserAsync`。
- **例外：上傳分塊端點**（1.0.60）`UploadChunkController` 走網頁登入 Cookie 而不是 JWT，不帶 `[HasPermission]`（授權靠上傳工作階段擁有者），列在「只需登入」白名單，詳見 [檔案上傳機制](../features/檔案上傳機制.md) §3.0.1。

## 限流（1.0.63）
- 所有 Controller 經 `MapControllers().RequireRateLimiting("api")` 限流，**依使用者分開計算**：已登入依帳號（Cookie 的 Sid／JWT 的 NameIdentifier）、未登入依來源 IP；上限見 `RateLimitSettings`（[日誌與設定檔說明](../operations/日誌與設定檔說明.md) §4.3.0.0）。
- 被擋回 **429**，body 維持 `ApiResult`，並帶 `Retry-After`。
- 不該被限流的端點（例如上傳分塊）以 `[DisableRateLimiting]` 明確免除。

## 例外與驗證
- `ApiValidationFilter` 會將 ModelState 錯誤轉成 `ApiResult<T>.Errors`。
- `ApiExceptionFilter` 會攔截未處理 API 例外，依 `Security:ReturnExceptionDetails` 決定是否填入 `ApiResult.Exception`。
- 既有 Controller 內手動 catch 的例外也應呼叫 `ApiResult.ServerErrorResult(message, exception)`，保留完整 exception。

## 待辦
- [x] 補完整 integration tests，實際驗證 400、401、403、404、409、500 的 HTTP body。
- [x] 正式環境預設隱藏 `Exception.StackTrace`。
- [x] 已導入 `/api/v1/...` 平行路由，目前保留 `/api/...` 相容路由。
