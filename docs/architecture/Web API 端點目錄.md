# Web API 端點目錄

- 文件版本：1.26
- 文件狀態：已實作
- 現行系統版本：1.1.23
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/10

本文件彙整 `MeetingRecord.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `MeetingRecord.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。

## 二、資源 CRUD 控制器

五個資源控制器共用同一組動作樣板（以 `CategoryController` 為代表，`src/MeetingRecord/MeetingRecord.Web/Controllers/CategoryController.cs` 的 `GetById`／`Search`／`Create`／`Update`／`Delete` 動作）：

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無回 `NotFound`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>` |
| 新增 | `POST {controller}` | Create | `ApiResult<TDto>`（同名回 `Conflict`）|
| 更新 | `PUT {controller}/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 `BadRequest`；查無回 `NotFound`）|
| 刪除 | `DELETE {controller}/{id}` | Delete（**`Meeting`（1.0.47）、`Project`、`Todo`（1.0.52）例外是 Edit**，見下方）| `ApiResult`（查無回 `NotFound`）|

各控制器對應的路由前綴與權限鍵：

| 控制器 | 路由前綴 | 權限鍵（`MagicObjectHelper`）| 檔案 |
|--------|----------|------------------------------|------|
| `CategoryController` | `api/Category`、`api/v1/Category` | `角色_分類清單` | `Controllers/CategoryController.cs` |
| `TeamController` | `api/Team`、`api/v1/Team` | `角色_團隊清單`（0.4.101 曾改名 `角色_資料群組`，0.4.102 改回）| `Controllers/TeamController.cs` |
| `ProjectController` | `api/Project`、`api/v1/Project` | `角色_專案項目` | `Controllers/ProjectController.cs` |
| `PromptTemplateController` | `api/PromptTemplate`、`api/v1/PromptTemplate` | `角色_提示詞清單` | `Controllers/PromptTemplateController.cs` |
| `MeetingController` | `api/Meeting`、`api/v1/Meeting` | `角色_會議紀錄` | `Controllers/MeetingController.cs` |

分類另有兩支（1.1.7，WASM 分類頁用）：`PUT Category/{id}/enabled`（Edit，內容 `{ "isEnabled": true }`，切換啟用）與 `GET Category/teams/selectable`（View，啟用中的團隊 `TeamOptionDto`）。1.1.7 起分類 API 呼叫畫面用的 `CategoryService`：名稱重複去頭尾空白、不分大小寫判斷，回 409 的訊息是「分類名稱已存在，無法新增／修改。」；`CategoryDto` 多了 `teamIds`／`teamNames`，新增與修改可帶 `teamIds` 同步負責團隊，**沒帶就不動**、帶空陣列才清空。

團隊另有三支（1.1.8，WASM 團隊頁用）：`PUT Team/{id}/enabled`（Edit，切換啟用）、`GET Team/{id}/delete-check`（Delete；可刪 200、仍是專案主責團隊的非管理者 409 與原因、不存在 404）、`GET Team/members/selectable`（**Create 或 Edit 其中之一**，啟用中帳號 `MemberOptionDto`：`id`、`label`＝「姓名（帳號）」）。1.1.8 起團隊 API 呼叫 `TeamService`：名稱與代號重複去頭尾空白、不分大小寫，409 訊息是「團隊名稱／代號已存在，無法新增／修改。」；`TeamDto` 多了 `memberCount`（單筆另帶 `memberIds`），新增與修改可帶 `memberIds` 同步成員，**沒帶就不動**、帶空陣列才清空。

提示詞另有五支（1.1.9，WASM 提示詞頁用）：`PUT PromptTemplate/{id}/enabled`（Edit）；`GET PromptTemplate/presets`（Create，內建範本名稱）、`POST PromptTemplate/presets`（Create，一次建立全部內建範本，已有同名的略過，訊息說明新增與略過幾筆）、`POST PromptTemplate/presets/single`（Create，內容 `{ "name": "…" }`；不在內建清單 404、已存在 409「「x」已存在，未新增。」）；`GET PromptTemplate/categories/enabled`（View，啟用中的分類名稱）。1.1.9 起提示詞 API 呼叫 `PromptTemplateService`：名稱重複去頭尾空白、不分大小寫，409 訊息「提示詞名稱已存在，無法新增／修改。」。**分類**：`PromptTemplateDto` 多了 `categoryNames`（名稱清單）；輸入時 `categoryNames` 有帶就以它為準，沒帶才看舊欄位 `categories`（資料庫的儲存格式，每個名稱前後各一個換行，例 `"\n週報\n"`），兩個都沒帶＝沒有分類；存檔時一律正規化（去空白、去重複）。

**使用者管理**（1.1.10，`UserController`，`api/v1/User`，整個 Controller **只限管理者** `[AdminOnly]`，非管理者 403）：`POST search`（`UserSearchRequestDto`，排序欄位 account／name／email／roletext／statustext／createat／updateat）、`GET {id}`（含額外角色、團隊）、`POST`（密碼必填；帳號重複 409）、`PUT {id}`（先讀出原資料再套上可改的欄位；額外角色與團隊 null＝不動；密碼留白＝不變）、`DELETE {id}`、`PUT {id}/status`（`{ "isEnabled": false }`）、`POST teams/batch`（`{ "userIds": [], "teamId": 1, "add": true }`，回實際異動人數）、`GET roles`（`isDefault` 標出新建帳號的預設角色）、`GET teams`（啟用中的團隊）。不能刪除或停用自己與 support、不能拿掉自己的管理者身分，違反回 400 與原因。回應 `UserDto` 不含任何密碼相關欄位。

**角色管理**（1.1.11，`RoleController`，`api/v1/Role`，整個 Controller **只限管理者** `[AdminOnly]`）：`POST search`（不帶權限樹；排序欄位 name／createat／updateat）、`GET {id}`（含權限樹 `rolePermission`）、`GET permission-template`（全部未勾選的權限樹）、`POST`、`PUT {id}`（名稱重複 409；權限樹裡的群組、頁面、動作都要在權限目錄裡，否則 400「權限項目「X」不在權限目錄裡。」；修改沒帶權限樹＝權限不變）、`DELETE {id}`（以它為主要角色的人改用其他角色；最後一個角色不能刪）、`GET {id}/user-count`、`POST presets`（建立預設角色，可重複呼叫）。權限樹格式：`groups[].name／enable／permissions[].name／enable／actions{"view":true,…}`，勾 `enable` 的頁面＝該頁全部動作。

**儀表板**（1.1.12，`DashboardController`）：`GET api/v1/dashboard`，只需登入（0.4.97 起儀表板登入即可看），回 `ApiResult<DashboardSummary>`（型別在 `Dtos/Dashboards`）；呼叫者看不到的專案在「各專案會議紀錄份數」併成「其他專案」。

**變更密碼頁與設定 API 密碼頁**（1.1.13，`AccountController`，只需登入、只動呼叫者自己）：`POST api/v1/account/change-own-password`（內容 `{ currentPassword, newPassword, confirmPassword }`；要確認新密碼、沒有字數下限、稽核 `via=FirstLogin`；標 `[AllowWhenMustChangePassword]`，改完清掉須改密碼標記）；`GET api/v1/account/password-status`（回 `{ hasLocalPassword }`）；`POST api/v1/account/api-password`（內容同上，還沒有本地密碼的帳號免填 `currentPassword`）。失敗回 400、訊息照原頁；support 帳號一律被拒。與 1.1.6 的 `change-password`（使用者選單對話窗，至少 6 字）是不同的規則。

**背景工作進度**（1.1.15，`JobController`，WASM 頁右下角進度面板用）：`GET api/v1/jobs`（只需登入；回 `List<BackgroundJobDto>`，只含呼叫者資料範圍內的會議的上傳、轉錄、會議紀錄生成工作，進行中排前面）；`POST api/v1/jobs/{meetingId}/cancel?kind=Transcription|Draft`（會議紀錄 Edit；上傳中的轉錄工作會以「上傳」種類取消）；`POST api/v1/jobs/{meetingId}/dismiss?kind=…`、`POST api/v1/jobs/dismiss-all`（只需登入，關閉通知、工作照跑；關閉是全域的，比照 Server 頁面板）。看不到或已結束的工作回 404，`kind` 不對回 400。推播 `AppHub.JobsChanged`（不帶資料、節流 0.5 秒）提醒前端重查。

**待辦事項**（1.1.18，`TodoController` 改走 `TodoService`）：既有五支（`GET {id}`、`POST search`、`POST`、`PUT {id}`、`DELETE {id}`）路由、權限、回應形狀不變，`TodoDto` 只多帶欄位：`projectStatus`、`createdByUserId`、`parentTitle`、`children`、`childrenTotal`／`childrenCompleted`、`dueDateChangedAt`／`dueDateChangeText`（後四項只有清單樹會帶）、`canDelete`（呼叫者能不能刪，畫面用）。`POST search` 照舊是**平面**清單（子項目也是一筆）。畫面用的新端點：`POST api/v1/Todo/tree`（只分頁主層、子項目跟著主待辦，內容 `TodoTreeRequestDto`，含 `projectScope` Active／Archived／All）、`PUT {id}/completed`（edit；失敗訊息放 `errorMessage`）、`GET owners?projectId&scope`、`GET by-owner?owner&projectId&scope`、`GET {id}/changes`、`GET parent-candidates?projectId&excludeId`（create 或 edit 任一）、`GET projects/selectable`；除註明者外都要 view。看不到的待辦一律 404。

**會議紀錄**（1.1.20，`MeetingController` 改走 `MeetingService`）：既有五支路由、權限、回應形狀不變，`MeetingDto` 只多帶欄位（`projectId`、`projectTitle`、`createdByUserId`、`hasMedia`、`hasTranscript`、`canRetryTranscription`、`draftStatus`、`draftError`、`draftPromptTemplateName`、`draftCompletedAt`、`hasDraft`、`canGenerateDraft`、`isUploader`、`canDelete`；`draftContent` 只有 `GET {id}` 帶），`MeetingCreateUpdateDto` 多了 `categoryNames`（有帶就以它為準，比照提示詞）。`POST search` 照舊。**外部行為變更**：`PUT` 對已完成（封存）專案的會議改為 400（與畫面一致，管理者除外）。畫面用的新端點：`POST list`（分類篩選、畫面欄位排序，view）、`GET {id}/linked-todos`（edit）、`POST {id}/retranscribe`（edit）、`POST {id}/draft`（產生會議紀錄，edit，會花 AI 費用）、`PUT {id}/draft`（修改全文，edit）、`PUT {id}/project`（歸屬專案，edit）、`GET {id}/transcript`（view）、`GET {id}/transcript/file`（.txt 含 BOM，export，寫稽核）、`POST {id}/transcript/copied`（瀏覽器複製後寫稽核，export）、`POST {id}/draft/pdf`（可帶還沒存的內容，export，寫稽核）、`GET projects/selectable`（附與會人員名冊）、`GET prompt-templates/selectable`、`GET categories/enabled`（後三支 view，不借用其他頁的權限）。看不到的會議一律 404；服務層擋下回 400，新端點訊息放 `errorMessage`。

**AI 問答**（1.1.21，`AiChatController`，路由 `api/v1/ai-chat/{project|meeting}/{targetId}`）：`GET conversations`、`POST conversations`（開新對話，回 Id）、`PUT conversations/{id}/title`（edit）、`DELETE conversations/{id}`（delete）、`GET conversations/{id}/messages`、`PUT conversations/{id}/messages/{index}`（edit；帶原本的角色與內容，對不上回 409）、`POST conversations/{id}/ask`（multipart：`question`＋0～5 個 `files`，**串流**）、`POST conversations/{id}/messages/{index}/regenerate`（edit，**串流**）、`GET conversations/{id}/attachments/{storedName}`（`download=true` 寫稽核）、`POST conversations/{id}/pdf`（export；`messageIndex` 有值＝單則，沒值＝整段，內容由伺服器讀）。權限依範圍（`AiChatPermissionAttribute`：專案看專案項目，會議看會議紀錄或專案項目），未註明者要 view。**串流格式**：`application/x-ndjson`，每行一筆 `AiChatStreamEventDto`——零到多筆 `delta`（新產生的文字），最後一筆 `done`（附完整結果）或 `error`（附原因）；在第一段文字產生之前就失敗（看不到 404、附件不合規定或額度用完 400）照一般 API 回應。服務層的錯誤原文放 `errorMessage`。

> 注意：**資料可見範圍（0.4.99 起，0.4.102 改以專案的主責＋協作團隊判斷）**：`ProjectRepository`（待辦 1.1.18、會議 1.1.20 起直接走服務層）與 Blazor 服務層同樣經 `ProjectAccessService` 過濾——呼叫者所屬團隊與專案的主責或協作團隊有交集才看得到，**沒有公開專案**；會議跟著專案、未歸屬只有上傳者；管理者不受限。**看不到的一律回 404**（當成不存在）。`PromptTemplate` 對所有人開放、`Team`／`Category` 只看權限鍵，都不做列級過濾。
>
> **專案的團隊與分類（0.4.102）**：`ProjectCreateUpdateDto`／`ProjectDto` 多了選填的 `primaryTeamId`、`collaboratorTeamIds`、`categories`，專案的 GET／搜尋結果會帶回團隊 Id。
> - `POST api/Project` 沒給 `primaryTeamId` 時，用建立者所屬、Id 最小的團隊當主責；建立者不屬於任何團隊時回 **400**「請先加入團隊，或指定主責團隊。」
> - `PUT api/Project/{id}` 的這三個欄位為 null 時保留原值。
> - 團隊選擇不合法（非管理者把主責改成不是自己的團隊、主責團隊不存在）回 **400**；規則與畫面共用 `ProjectAccess.ResolveProjectTeams`＋`ProjectTeamWriter`。
> - **1.0.47**：`PUT api/Project/{id}` 由**不是主責團隊成員**的人送出、而主責或協作團隊與原本不同時回 **400**「只有主責團隊的成員可以變更團隊設定。」（三個欄位給 null＝保留原值，不算變更）；其他欄位照常可改。
> - **1.0.47**：`DELETE api/Project/{id}` 看不到回 **404**；不是主責團隊成員回 **400**「只有主責團隊的成員可以刪除專案。」；專案已完成（封存）回 **400**「這個專案已完成（封存），請先重新開啟再修改。」。以前服務層任何失敗都回 500，現在只有帶例外的失敗才是 500。管理者不受限。**1.0.52 起改成只限專案負責人**（見下一段）。

> **專案負責人（1.0.52）**：`ProjectCreateUpdateDto`／`ProjectDto` 多了選填的 `ownerUserId`（負責人帳號 Id），回應一律帶回；`owner` 改成選填，回應裡是負責人的**姓名快照**。規則與畫面共用 `ProjectOwnerRules`。
> - `POST api/Project`：`owner` 與 `ownerUserId` **至少要給一個**，都沒給回 **400**「負責人 不可為空白」。有給 `ownerUserId` 時必須是主責＋協作團隊裡啟用中的帳號，否則回 **400**「負責人必須是專案團隊裡啟用中的帳號，請重新選擇。」；`owner` 文字會被帳號姓名取代。只給 `owner`（1.0.52 前的呼叫端）時，用它在候選人裡比對姓名或帳號（不分大小寫），**剛好一位**才採用，否則存成未指定負責人（只有管理者能刪）。
> - `PUT api/Project/{id}`：不帶 `ownerUserId`＝保留原本的負責人（`owner` 文字會被忽略）。要換負責人只限主責團隊成員、目前的負責人與管理者，否則回 **400**「只有主責團隊的成員或目前的負責人可以變更負責人。」。負責人或團隊有變就重新驗證是不是團隊裡啟用中的帳號（舊資料沒有負責人、也沒換的，不強迫補）。
> - `DELETE api/Project/{id}` **改要求 `專案項目:edit`**（以前是 `delete`）：只限負責人與管理者，不是負責人回 **400**「只有專案負責人可以刪除專案。」；看不到 404、封存 400 同上。只有 `delete` 沒有 `edit` 的自訂角色從此呼叫不了。
> - `DELETE api/Team/{id}`：非管理者刪除還是某個專案主責團隊的團隊時回 **409**，訊息列出那些專案；只當協作團隊的可以刪。管理者不受此限（0.4.104）：照刪，受影響的專案就沒有主責團隊，之後編輯專案時再補；下次啟動時 `TeamConversionService` 會替沒有主責的專案補上（取協作團隊中 Id 最小的，沒有就掛「待分配」）。
>
> **會議與提示詞範本的 `teams` 欄位已移除（0.4.103）**：`MeetingCreateUpdateDto`／`PromptTemplateCreateUpdateDto` 不再有 `teams`（舊的團隊標籤字串，0.4.99 起已不影響可見性），舊客戶端仍送這個欄位會被忽略、不會報錯。分類請用 `categories`。
>
> 詳見 [開發慣例與限制速查](開發慣例與限制速查.md) §4.0。

`MeetingController` 與其他四個的差異：

- **沒有同名衝突檢查**（會議標題可重複），因此新增／更新不會回 `Conflict`。
- **只開放中繼資料**。影音檔上傳、語音轉錄與逐字稿讀取刻意不開 API——那些操作牽涉實體檔案、背景佇列與長時間外部呼叫，只在 Blazor 服務層提供（見 [會議紀錄 PRD](../prd/會議紀錄-prd.md)）。
- `PUT` 經 `MeetingService.UpdateAsync`（1.1.20 起，與畫面同一份）一律沿用資料庫既有的媒體、轉錄、會議紀錄與歸屬欄位，API 用戶端無法覆寫背景轉錄寫入的狀態。
- `DELETE` 走 `MeetingService.DeleteAsync`（0.4.115）：和畫面同一套，一併清影音檔、逐字稿與 AI 問答對話；轉錄或會議紀錄生成進行中回 **400**。以前由控制器自己清兩個實體檔，對話檔留在磁碟上。
- **`DELETE api/Meeting/{id}` 1.0.47 起要求 `會議紀錄:edit`（以前是 `delete`）**：刪除只限上傳者與管理者，由 `MeetingService.DeleteAsync` 把關，上傳者的角色只要能編輯就能刪自己上傳的（與畫面一致）。不是上傳者回 **400** `ValidationError`「只有上傳者或管理者可以刪除這筆會議紀錄。」；看不到回 404；沒有 `edit` 回 403。只有 `delete` 沒有 `edit` 的自訂角色從此呼叫不了這支 API。刪除會保留從這場會議抽出的待辦（`MeetingId` 改成 null）。**1.0.52 起專案負責人也能刪**該專案底下任何人的會議，擋下的訊息改為「只有上傳者或專案負責人可以刪除這筆會議紀錄。」。
- **`DELETE api/Todo/{id}` 1.0.52 起要求 `待辦事項:edit`（以前是 `delete`）**：刪除只限專案負責人與管理者（`TodoService.DeleteAsync`，1.1.18 起 API 與畫面同一份），不是負責人回 **400** `ValidationError`「只有專案負責人可以刪除待辦。」；看不到 404；沒有 `edit` 回 403。主待辦連同子項目一起刪。
- **`PUT api/Todo/{id}` 改期限要附原因（1.0.55）**：`dueDate` 的日期與原本不同時，`dueDateChangeNote` 必填（≤500），否則 **400**「改期限請填寫原因。」；原因寫進異動紀錄並附在通知後面。甲方帳號（`MyUser.IsExternal`）`DELETE` 只能刪自己 `POST` 建立的待辦，否則 **400**「甲方帳號只能刪除自己建立的待辦。」。
- `POST` 一律由資料庫配號，不照抄用戶端的 `id`（0.4.115，待辦、專案同）。
- 待辦的 `status` 必須是 待辦／進行中／已完成、`priority` 必須是 低／中／高，否則 **400**（0.4.115）。
- 待辦的 `parentTodoId`（1.0.45，`TodoCreateUpdateDto`，`TodoDto` 繼承）：指定主待辦就成為子項目。與畫面共用 `TodoHierarchyRules`，違反時 `POST`／`PUT` 都回 **400** 並帶訊息：主待辦本身是子項目（只允許一層）、這筆已有子項目卻要掛到別人底下、指定自己、與主待辦不同專案。`PUT` 現在也把 `InvalidOperationException` 轉成 400。⚠️ `PUT` 是**整筆取代**：沒帶 `parentTodoId` 會把子項目脫離主待辦。主待辦換專案時子項目跟著搬；`DELETE` 主待辦會連同子項目一起刪。
- 已完成（封存）專案的待辦（1.0.46）：非管理者對狀態「已完成」的專案 `POST`／`PUT`／`DELETE` 待辦都回 **400**「這個專案已完成（封存），請先重新開啟再修改。」；`PUT` 的原專案與目標專案都檢查（搬進、搬出都不行）。管理者不受限。與畫面共用 `ProjectArchiveRules.CheckWritableAsync`；`DELETE` 也會把 `InvalidOperationException` 轉成 400。要恢復修改，先把專案狀態改回其他狀態（`PUT api/Project/{id}` 不受此限）。

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

## 四之一、上傳分塊（網頁內部使用）

`src/MeetingRecord/MeetingRecord.Web/Controllers/UploadChunkController.cs`（1.0.60）。**給網頁本身用、不是給外部用戶端的 API**：走網頁登入 Cookie，工作階段由網頁在使用者按下存檔、通過權限與資料範圍檢查後才建立。

| 動作 | 路由 | 說明 |
|------|------|------|
| 送一塊 | `PUT api/v1/uploads/{uploadId}/chunks/{index}` | body 為原始位元組（每塊 8 MB，最後一塊為餘數）；須帶 `X-MeetingRecord-Upload: 1`；200 收下、400 編號或長度不對、401 未登入、404 找不到或不是自己的、409 已停止；免除限流 |
| 建立工作階段（1.1.16） | `POST api/v1/uploads` | WASM 頁用（`UploadSessionController`）。內容 `{ purpose: "MeetingMedia", meetingId, fileName, contentType, size, deleteMeetingOnCancel }`；會議紀錄「新增」或「修改」權限；先跑 `MeetingService.CheckMediaUploadAsync`（格式、大小、資料範圍、只限上傳者、封存、進行中工作），不合回 400 與原因；回 `{ uploadId, chunkSize, chunkCount }`。讀檔、存檔、排入轉錄在伺服器背景以上傳者身分執行 |
| 查狀態（1.1.16） | `GET api/v1/uploads/{uploadId}` | 回 `{ state: Uploading/Succeeded/Cancelled/Failed, message, meetingDeleted, cancelTooLate }`；只認擁有者，別人的 404；結束後保留 10 分鐘 |
| 取消（1.1.16） | `DELETE api/v1/uploads/{uploadId}` | 等同 Server 頁的「取消上傳」：新增時附的檔連會議一起刪（建好就取消、服務層還沒開始收檔時也一樣）；檔案已寫完則來不及取消、照樣排入轉錄 |
| 回報失敗（1.1.23） | `POST api/v1/uploads/{uploadId}/fail` | 內容 `{ message }`。瀏覽器送不下去（重試用完、登入失效）時呼叫，伺服器立刻結束那趟上傳、會議保留（同 Server 頁），不必等 2 分鐘逾時；只認擁有者，別人的回 404 |

專案附件的上傳用途等專案頁搬到 WASM 時再加（1.1.21）。

## 五、模板遺留

~~`WeatherForecastController`~~（路由 `/WeatherForecast`）是專案模板遺留範例，沒有任何驗證、任何人都能呼叫，**1.0.58 已刪除**（連同 `MeetingRecord.Models/Others/WeatherForecast.cs`）。目前沒有模板遺留的 API；新增端點一律要有授權屬性，`ApiAuthorizationConventionTests` 會擋（見《[Web API 設計慣例](Web%20API%20設計慣例.md)》）。

## 六、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)
- [會議紀錄提示詞 PRD](../prd/會議紀錄提示詞-prd.md)

> 返回 [architecture 索引](README.md)
