# 會議紀錄提示詞 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.26
- 首次實作版本：0.4.26
- 最後核對日期：2026/08/19

## 一、目標與範圍

提供「會議紀錄提示詞（PromptTemplate）」範本主資料的維護能力，讓具權限者在 `/prompttemplates` 頁面完成提示詞的查詢、新增、修改、刪除。提示詞為獨立主資料，無外鍵關聯，`Name` 唯一（不分大小寫）；`Content` 為長文字指令，可含固定變數佔位符，供日後產生會議紀錄時代入實際內容。亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的提示詞名稱。

本版**只到資料維護為止**：系統不會呼叫任何 LLM 或語音轉錄 API，`appsettings.json` 的 `LlmSettings` 僅為 provider-aware 的強型別設定骨架（見第四節與 [日誌與設定檔說明](../operations/日誌與設定檔說明.md)）。

非範圍：
- 不做提示詞的版本歷程、草稿、比較或還原（更新即覆寫）。
- 不做匯入／匯出、批次操作、軟刪除（刪除為實體刪除）。
- 不做提示詞的實際執行與產出預覽（不呼叫 LLM）。
- 不做與其他實體的外鍵關聯或參照完整性檢查（`BeforeDeleteCheckAsync` 直接回成功）。
- 不做音檔上傳、語音轉錄、會議紀錄產出——該完整流程為規劃中，見 [會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/prompttemplates`（`PromptTemplatePage.razor`，`MainLayout`） |
| REST API | `api/PromptTemplate`、`api/v1/PromptTemplate`（`GET {id}`／`POST search`／`POST`／`PUT {id}`／`DELETE {id}`） |
| 選單路徑 | 資料定義（id=5）> 提示詞清單（id=53，`url=/prompttemplates`，icon `article`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[53] = 角色_提示詞清單` |
| UI 頁面權限 | 頁面鍵「提示詞清單」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `提示詞清單:view` / `提示詞清單:create` / `提示詞清單:edit` / `提示詞清單:delete` |
| 主要使用者 | 具「提示詞清單」角色權限的後台管理者；系統管理員無條件可存取 |

選單以 `id` 對應權限鍵（`Menu.json` 的 `id=53` ↔ `MenuPermissionMap[53]`），重排 `Menu.json` 不會錯位。權限鍵常數另需登記於 `RolePermissionService` 的「資料定義」群組，才會出現在角色權限矩陣。

## 三、畫面與欄位

單頁清單 + Modal 表單（`PromptTemplateViewView`）：

- 搜尋：關鍵字比對 `Name`、`Content` 或 `Description`（`Contains`）。清空搜尋鈕在有輸入時出現。
- 工具列：新增、重新整理、分類過濾（多選）、團隊過濾（多選）、關鍵字、清空搜尋、搜尋。
- 排序：可排序欄位 `Name`、`IsEnabled`、`CreatedAt`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。`Content` 為長文字，刻意**不開放排序**。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、內容預覽、描述、分類、團隊、啟用狀態（啟用／停用）、更新時間、操作（修改／刪除）。
  - 內容預覽 `ContentPreview` 為唯讀計算屬性：將 `Content` 單行化後截斷 60 字並加上刪節號。
- 新增／編輯表單欄位：
  - 名稱 `Name`（必填，最長 100）
  - 提示詞內容 `Content`（必填，最長 20000，14 列 `TextArea`）
  - 描述 `Description`（選填，最長 2000）
  - 分類 `Categories`（多值標籤，供檢索分群，**不影響可見性**）
  - 團隊 `Teams`（多值標籤，Placeholder「選擇團隊（不設定表示公開）」，**決定可見範圍**）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
- 按鈕級權限：新增／修改／刪除按鈕分別以 `CheckAccessAction(角色_提示詞清單, PermissionActions.Create/Edit/Delete)` 控制顯示（與 `ProjectViewView` 一致，較 `CategoryViewView` 多此一層）。
- 鍵盤行為：Esc 關閉 Modal。**與其他清單頁不同，本頁 Enter 不送出表單**——`Content` 是多行輸入，Enter 必須留給換行。
- 刪除：`ConfirmAsync` 二次確認，提示不可復原。

### 提示詞變數

| 變數 | 代入內容 |
| --- | --- |
| `{{transcript}}` | 會議逐字稿全文（規劃中流程才會有值） |
| `{{meetingTitle}}` | 會議標題 |
| `{{meetingDate}}` | 會議日期 |

大括號內允許前後空白（`{{ transcript }}` 亦視為已知變數），變數名比對不分大小寫。儲存時掃描 `{{...}}`，出現固定集合以外的變數時以 `NotificationService` 發出警告並列出未知變數與支援清單，**但不阻擋儲存**（範本作者可能刻意先寫下尚未支援的佔位符）。這不是驗證錯誤。

## 四、內部系統運作

- UI 路徑：`PromptTemplateViewView` →（注入）`PromptTemplateService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`PromptTemplateController` → `PromptTemplateRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `PromptTemplate`（`Id/Name/Content/Description/IsEnabled/Categories/Teams/CreatedAt/UpdatedAt`），DbSet 為 `context.PromptTemplate`。`Content` 在 SQLite 為 `TEXT`，Entity 端刻意不加長度上限，長度限制只在 AdapterModel／DTO 以 `[StringLength(20000)]` 表達。
- 標籤欄位 `Categories`／`Teams` 以 `TagStringHelper` 的「換行包夾」格式儲存（例 `\n團隊A\n`）；AutoMapper 以 `ForMember` 搭配 `TagStringHelper.ToList` / `ToStored` 在 `List<string>` 與儲存字串之間轉換。
- 變數檢查在 UI 層的 `NotifyUnknownVariables()`，位於 `EditContext.Validate()` 與 `BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 都通過之後、`AddAsync`／`UpdateAsync` 之前；**刻意不放進 Service 的前置檢查流程**，因為它不阻擋儲存。判斷邏輯抽在 `PromptVariableHelper` 以便單元測試。
- 查詢一律 `AsNoTracking()`；寫入前 `CleanTrackingHelper.Clean<PromptTemplate>` 清追蹤，寫入後再清一次。
- 編輯前於 UI 以 `CurrentRecord = model.Clone()` 複製，避免污染清單資料；`Clone()` 為淺複製後另建 `Categories`／`Teams` 新清單。`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified/Deleted` 提交。
- 模型變更需在 `MeetingRecord.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）；本能力對應 migration `AddPromptTemplate`（只新增 `PromptTemplate` 一張表，不影響既有表）。
- LLM 設定：`LlmSettings` 於 `Program.cs` 以 `AddOptions<LlmSettings>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` 綁定為 `IOptions<LlmSettings>`；`Providers` 為以供應商名稱為鍵的字典，`DefaultProvider` 指定預設供應商。**本版無任何呼叫端**，僅供規劃中流程預留。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_提示詞清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`提示詞清單:view`、`提示詞清單:create`、`提示詞清單:edit`、`提示詞清單:delete`。裸鍵「提示詞清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面、以 `CheckAccessAction` 控制按鈕顯示，API 用 JWT Bearer 並以動作鍵控制個別操作。
- **團隊列級權控只在 Blazor Service 層生效**：非管理員於 `PromptTemplateService` 以 `TagStringHelper.BuildTeamAccessPredicate` 只能看到公開（無團隊）或與自身有效團隊有交集的提示詞；單筆讀取以 `TagStringHelper.IsTeamAccessible` 守門，越權時回空模型。**Web API 的 repository 路徑不做列級過濾**，與 `ProjectController`／`ProjectRepository` 一致（見 [開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §4.1）——持有有效 JWT 與 `提示詞清單:view` 的用戶端可經 API 讀到跨團隊資料，這是既有設計界線，非本次引入。
- 新增權限鍵後，掛「預設角色」的使用者需重啟一次應用程式才會生效；掛自訂角色者需由管理員到 `/roleviews` 手動勾選。
- `LlmSettings` 的 `ApiKey` 為機敏值：版控內只放開發預設值，`appsettings.Production.json` 一併清空 `DefaultProvider` 與 `ApiKey`，正式環境須以環境變數（`LlmSettings__Providers__AzureOpenAI__ApiKey`）或 user-secrets 提供。`StartupSafetyValidator` 在 Production 啟動時檢查：指定了 `DefaultProvider` 就不允許 `ApiKey` 留空或沿用開發預設值、也不允許 `Endpoint` 留空或沿用範例值。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（`ToLower()` 不分大小寫，修改時排除自身），重複回「提示詞名稱已存在」。API 端另以 `ExistsByNameAsync` 回 409 Conflict。
- **名稱唯一性是全域、可見性是團隊範圍**：B 團隊建立的提示詞，A 團隊使用者在清單上看不到，但用同名新增時仍會收到「提示詞名稱已存在」。這是沿用分類／團隊清單的既有語意，屬預期行為而非缺陷。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的提示詞資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱與內容必填、長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 內容為空白或只含空白：視為未填，走必填驗證。
- 未知變數：**只警告不阻擋**，列出未知變數名稱與支援清單後仍完成儲存。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 非管理員且無任何有效團隊：僅能看到公開（無團隊）的提示詞。
- 例外：Service 以 try/catch 記錄並回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

## 七、驗收與測試

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/PromptTemplateServiceTests.cs`：

- `BeforeAddCheckAsync_WithUniqueName_ShouldSucceed`：唯一名稱可新增。
- `BeforeAddCheckAsync_WithDuplicateName_ShouldFail`：重複名稱被拒。
- `BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail`：大小寫不同仍視為重複。
- `BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed`：同一筆用原名可通過。
- `BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail`：名稱被他筆占用被拒。
- `AddAsync_ShouldPersistContentAndTagStrings`：新增後保留內容、描述、啟用狀態，且標籤欄位確實轉為 `TagStringHelper` 儲存格式（漏接轉換器會使團隊權控全面失效）。
- `GetAsync_ById_ShouldRoundTripTagsToList`：儲存字串可還原為標籤清單。
- `UpdateAsync_ShouldReplaceTagsAndKeepCreatedAt`：更新換掉標籤、保留 `CreatedAt`、推進 `UpdatedAt`。
- `AddAsync_WithMultiKilobyteContent_ShouldPersistIntact`：8192 字元內容原樣保存（防止誤植過小的長度上限）。
- `DeleteAsync_ShouldRemoveRecord`：刪除後查不到。
- `GetAllEnabledNamesAsync_ShouldReturnOnlyEnabledOrderedByName`：僅回啟用中並依名稱排序。
- `GetAsync_Admin_ShouldSeeAllRecords`：管理員看得到全部。
- `GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords`：非管理員只看公開與團隊交集。
- `GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords`：無團隊者只看公開。
- `GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope`：單筆越權回空模型。
- `GetAsync_WithTeamFilter_ShouldFilterByTeam`：團隊過濾生效。
- `GetAsync_WithKeyword_ShouldMatchContent`：關鍵字可命中提示詞內容。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/PromptVariableHelperTests.cs`：

- `KnownVariables_ShouldMatchPrdContract`：固定變數集與本文件綁定，變更時兩邊必須同步。
- `FindUnknownVariables_WithKnownVariablesOnly_ShouldReturnEmpty`、`FindUnknownVariables_WithUnknownVariable_ShouldReturnIt`、`FindUnknownVariables_ShouldDeduplicate`、`FindUnknownVariables_WithInnerWhitespace_ShouldStillMatchKnown`、`FindUnknownVariables_ShouldIgnoreCaseWhenMatchingKnown`、`FindUnknownVariables_WithNullOrWhitespace_ShouldReturnEmpty`、`FindUnknownVariables_WithoutAnyPlaceholder_ShouldReturnEmpty`、`DescribeKnownVariables_ShouldRenderAllPlaceholders`：變數掃描與說明文字行為。

對應測試檔 `src/MeetingRecord/MeetingRecord.Tests/PromptTemplateRegistrationTests.cs`（守住宣告式權限註冊三件組）：

- `RolePermissionCatalog_ShouldContainPromptTemplatePage`、`RolePermissionCatalog_ShouldPlacePromptTemplateUnderDataDefinitionGroup`：權限鍵登記於「資料定義」群組。
- `MenuJson_ShouldContainPromptTemplateNode`：`Menu.json` 有 id=53、名稱與 url 正確的節點。

`src/MeetingRecord/MeetingRecord.Tests/MenuIconTests.cs` 的 `AllowedIcons` 已加入 `article`；未加入會使整套測試失敗。

`LlmSettings` 與其 Production 檢查另有 `src/MeetingRecord/MeetingRecord.Tests/LlmSettingsTests.cs` 與 `src/MeetingRecord/MeetingRecord.Tests/StartupSafetyValidatorTests.cs`。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper；團隊權控以假的 `IRecordAccessScopeProvider` 注入。注意 `EnsureCreatedAsync` 直接由模型建表、**繞過 migration**，所以測試全綠不代表 migration 存在。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/PromptTemplates/PromptTemplatePage.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/PromptTemplates/PromptTemplateViewView.razor:1`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/PromptTemplates/PromptTemplateViewView.razor.cs:86`（頁面權限檢查）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/PromptTemplateController.cs:36`（`[HasPermission]` 動作鍵）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/PromptTemplateService.cs:151`（AddAsync）、`src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/PromptTemplateService.cs:244`（前置檢查）
- `src/MeetingRecord/MeetingRecord.Business/Repositories/PromptTemplateRepository.cs:1`（API 路徑，不做列級過濾）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/PromptVariableHelper.cs:15`（固定變數集）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Models/PromptTemplate.cs:8`（Entity 欄位）、`src/MeetingRecord/MeetingRecord.AccessDatas/BackendDBContext.cs:23`（DbSet）
- `src/MeetingRecord/MeetingRecord.Business/Models/AutoMapping.cs:48`（標籤欄位轉換）
- `src/MeetingRecord/MeetingRecord.Dtos/Models/PromptTemplateCreateUpdateDto.cs:9`、`src/MeetingRecord/MeetingRecord.Dtos/Commons/PromptTemplateSearchRequestDto.cs:6`
- `src/MeetingRecord/MeetingRecord.Share/Helpers/MagicObjectHelper.cs:37`、`src/MeetingRecord/MeetingRecord.Share/Helpers/PermissionKeys.cs:9`
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/RolePermissionService.cs:26`（權限矩陣登記）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs:27`、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json:58`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs:80`（DI 註冊）
- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs:22`、`src/MeetingRecord/MeetingRecord.Web/Program.cs:237`（Options 綁定）、`src/MeetingRecord/MeetingRecord.Web/Configuration/StartupSafetyValidator.cs:46`（Production 檢查）
- `src/MeetingRecord/MeetingRecord.Tests/PromptTemplateServiceTests.cs:1`、`src/MeetingRecord/MeetingRecord.Tests/PromptVariableHelperTests.cs:1`、`src/MeetingRecord/MeetingRecord.Tests/PromptTemplateRegistrationTests.cs:1`
- 交叉連結：[會議紀錄產生流程 PRD](會議紀錄產生流程-prd.md)、[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[紀錄分類與團隊權控-prd.md](紀錄分類與團隊權控-prd.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)
