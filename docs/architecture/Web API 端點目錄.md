# Web API 端點目錄

- 文件版本：1.5
- 文件狀態：已實作
- 現行系統版本：0.4.115
- 首次實作版本：0.1.61
- 最後核對日期：2026/10/02

本文件彙整 `MeetingRecord.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `MeetingRecord.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。

## 二、資源 CRUD 控制器

五個資源控制器共用同一組動作樣板（以 `CategoryController` 為代表，`src/MeetingRecord/MeetingRecord.Web/Controllers/CategoryController.cs:35`）：

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無回 `NotFound`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>` |
| 新增 | `POST {controller}` | Create | `ApiResult<TDto>`（同名回 `Conflict`）|
| 更新 | `PUT {controller}/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 `BadRequest`；查無回 `NotFound`）|
| 刪除 | `DELETE {controller}/{id}` | Delete | `ApiResult`（查無回 `NotFound`）|

各控制器對應的路由前綴與權限鍵：

| 控制器 | 路由前綴 | 權限鍵（`MagicObjectHelper`）| 檔案 |
|--------|----------|------------------------------|------|
| `CategoryController` | `api/Category`、`api/v1/Category` | `角色_分類清單` | `Controllers/CategoryController.cs` |
| `TeamController` | `api/Team`、`api/v1/Team` | `角色_團隊清單`（0.4.101 曾改名 `角色_資料群組`，0.4.102 改回）| `Controllers/TeamController.cs` |
| `ProjectController` | `api/Project`、`api/v1/Project` | `角色_專案項目` | `Controllers/ProjectController.cs` |
| `PromptTemplateController` | `api/PromptTemplate`、`api/v1/PromptTemplate` | `角色_提示詞清單` | `Controllers/PromptTemplateController.cs` |
| `MeetingController` | `api/Meeting`、`api/v1/Meeting` | `角色_會議紀錄` | `Controllers/MeetingController.cs` |

> 注意：**資料可見範圍（0.4.99 起，0.4.102 改以專案的主責＋協作團隊判斷）**：`ProjectRepository`、`MeetingRepository`、`TodoRepository` 與 Blazor 服務層同樣經 `ProjectAccessService` 過濾——呼叫者所屬團隊與專案的主責或協作團隊有交集才看得到，**沒有公開專案**；會議跟著專案、未歸屬只有上傳者；管理者不受限。**看不到的一律回 404**（當成不存在）。`PromptTemplate` 對所有人開放、`Team`／`Category` 只看權限鍵，都不做列級過濾。
>
> **專案的團隊與分類（0.4.102）**：`ProjectCreateUpdateDto`／`ProjectDto` 多了選填的 `primaryTeamId`、`collaboratorTeamIds`、`categories`，專案的 GET／搜尋結果會帶回團隊 Id。
> - `POST api/Project` 沒給 `primaryTeamId` 時，用建立者所屬、Id 最小的團隊當主責；建立者不屬於任何團隊時回 **400**「請先加入團隊，或指定主責團隊。」
> - `PUT api/Project/{id}` 的這三個欄位為 null 時保留原值。
> - 團隊選擇不合法（非管理者把主責改成不是自己的團隊、主責團隊不存在）回 **400**；規則與畫面共用 `ProjectAccess.ResolveProjectTeams`＋`ProjectTeamWriter`。
> - `DELETE api/Team/{id}`：非管理者刪除還是某個專案主責團隊的團隊時回 **409**，訊息列出那些專案；只當協作團隊的可以刪。管理者不受此限（0.4.104）：照刪，受影響的專案就沒有主責團隊，之後編輯專案時再補；下次啟動時 `TeamConversionService` 會替沒有主責的專案補上（取協作團隊中 Id 最小的，沒有就掛「待分配」）。
>
> **會議與提示詞範本的 `teams` 欄位已移除（0.4.103）**：`MeetingCreateUpdateDto`／`PromptTemplateCreateUpdateDto` 不再有 `teams`（舊的團隊標籤字串，0.4.99 起已不影響可見性），舊客戶端仍送這個欄位會被忽略、不會報錯。分類請用 `categories`。
>
> 詳見 [開發慣例與限制速查](開發慣例與限制速查.md) §4.0。

`MeetingController` 與其他四個的差異：

- **沒有同名衝突檢查**（會議標題可重複），因此新增／更新不會回 `Conflict`。
- **只開放中繼資料**。影音檔上傳、語音轉錄與逐字稿讀取刻意不開 API——那些操作牽涉實體檔案、背景佇列與長時間外部呼叫，只在 Blazor 服務層提供（見 [會議紀錄 PRD](../prd/會議紀錄-prd.md)）。
- `PUT` 與 `MeetingRepository.UpdateAsync` 一律沿用資料庫既有的媒體與轉錄欄位，API 用戶端無法覆寫背景轉錄寫入的狀態。
- `DELETE` 走 `MeetingService.DeleteAsync`（0.4.115）：和畫面同一套，一併清影音檔、逐字稿與 AI 問答對話；轉錄或會議紀錄生成進行中回 **400**。以前由控制器自己清兩個實體檔，對話檔留在磁碟上。
- `POST` 一律由資料庫配號，不照抄用戶端的 `id`（0.4.115，待辦、專案同）。
- 待辦的 `status` 必須是 待辦／進行中／已完成、`priority` 必須是 低／中／高，否則 **400**（0.4.115）。

## 三、認證控制器 `AuthController`

`src/MeetingRecord/MeetingRecord.Web/Controllers/AuthController.cs`，路由 `api/Auth`、`api/v1/Auth`；帳密換 JWT。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 登入 | `POST Auth/login` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（失敗回 `Unauthorized`；需要改密碼的帳號也回 `Unauthorized`，0.4.115）|
| 換發 Token | `POST Auth/refresh` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（Refresh Token 無效，或帳號已停用、鎖定、刪除、待改密碼回 `Unauthorized`；0.4.115 起重查資料庫）|
| 目前使用者 | `GET Auth/me` | `[Authorize(JwtBearer)]` | `ApiResult<CurrentUserDto>`（讀 JWT Claims）|

## 四、Google 第三方登入 `ExternalAuthController`

`src/MeetingRecord/MeetingRecord.Web/Controllers/ExternalAuthController.cs`，路由前綴 `Auths/Google`。**此為網頁 Cookie 登入導向端點，非 API**（回傳 `Challenge`／`Redirect`，不走 `ApiResult`）。詳見《[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)》。

| 動作 | 路由 | 說明 |
|------|------|------|
| 觸發 Google 驗證 | `GET Auths/Google/Login` | 未設定金鑰時導回 `/Auths/Login`|
| 驗證回呼 | `GET Auths/Google/Callback` | 查找/建立帳號；停用帳號導向 `/Auths/Pending`，啟用則完成 Cookie 登入 |

## 五、模板遺留

`WeatherForecastController`（路由 `[controller]`，即 `/WeatherForecast`）為專案模板遺留範例，非正式能力；新專案啟動時可移除（見《[腳手架新專案啟動流程](../guides/腳手架新專案啟動流程.md)》）。

## 六、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)
- [會議紀錄提示詞 PRD](../prd/會議紀錄提示詞-prd.md)

> 返回 [architecture 索引](README.md)
