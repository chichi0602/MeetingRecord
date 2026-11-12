# 建立一個新 CRUD 操作網頁說明

- 文件版本：1.6
- 文件狀態：已實作
- 現行系統版本：1.1.52
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/11/12

> 目標：照本系統現在的架構做出一個「新增、查詢、修改、刪除、搜尋、排序、分頁、驗證、通知」都齊全的頁面：
> 頁面在瀏覽器執行（Blazor WebAssembly，`Web.Client/Pages`），資料一律經 API Controller，規則寫在服務層，
> 並附上授權測試（未登入 401、沒權限 403）。先用 `scripts/New-CrudModule.ps1` 產生骨架，再照第 3 節登錄、第 4 節改成自己的欄位。

---

## 1. 用腳本產生骨架

```powershell
pwsh scripts/New-CrudModule.ps1 -Name Product -Title 產品清單
pwsh scripts/New-CrudModule.ps1 -Name Product -Title 產品清單 -Route /products   # 自訂路由（預設 /product）
```

| 參數 | 說明 |
|------|------|
| `-Name` | 英文的實體名稱（大寫開頭），決定類別、檔名、API 路徑 `api/v1/<Name>` |
| `-Title` | 頁面標題，同時是權限鍵（`MagicObjectHelper.角色_<Title>`）與選單名稱 |
| `-Route` | 頁面路由，預設 `/<name 小寫>` |
| `-SolutionRoot` | 方案資料夾，預設 `src/MeetingRecord` |
| `-Force` | 檔案已存在時覆寫（沒帶時只要有一個已存在就整組不寫） |

腳本直接把檔案寫進方案的各專案，最後印出第 3 節那 8 處要貼的程式碼。

## 2. 產生了什麼

| 檔案 | 內容 |
|------|------|
| `AccessDatas/Models/<Name>.cs` | 資料表實體：`Id`、`Name`、`Description`、`IsEnabled`、`CreatedAt`、`UpdatedAt` |
| `Dtos/Models/<Name>CreateUpdateDto.cs` | 新增與修改的輸入，DataAnnotations 驗證（頁面與 API 共用） |
| `Dtos/Models/<Name>Dto.cs` | API 的輸出，也是頁面表單綁定的模型；有 `Clone()` |
| `Dtos/Commons/<Name>SearchRequestDto.cs` | 搜尋條件：分頁、排序、關鍵字、啟用篩選 |
| `Business/Services/DataAccess/<Name>Service.cs` | 查詢（篩選、排序、分頁）、名稱重複檢查、新增、修改、刪除；寫入前後以 `CleanTrackingHelper` 清除追蹤，失敗回 `VerifyRecordResult` |
| `Web/Controllers/<Name>Controller.cs` | `GET {id}`、`POST search`、`POST`、`PUT {id}`、`DELETE {id}`；`[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]`＋每個動作一個 `[HasPermission]`；回傳 `ApiResult<T>`、分頁包 `PagedResult<T>`；名稱重複回 409、找不到回 404 |
| `Web.Client/Api/<Name>Api.cs` | WASM 端呼叫 API 的包裝（JSON 走 `ClientJsonContext` 原始碼產生器） |
| `Web.Client/Pages/<Name>/<Name>Page.razor(.cs/.css)` | 清單＋新增／修改對話窗（細節見第 5 節） |
| `Tests/<Name>ApiTests.cs` | L2：5 支端點各測未登入 401、沒有這頁權限 403；有權限時新增→重複名稱 409→搜尋→修改→刪除走一輪 |

## 3. 手動登錄 8 處

腳本不改既有檔案，下列 8 處要自己貼（腳本最後會印出帶好名稱的版本）。頁面權限是**宣告式、以 `id` 對應**：重排 `Menu.json` 不會錯位。

1. **權限鍵常數**：`MeetingRecord.Share/Helpers/MagicObjectHelper.cs` 加 `public const string 角色_<Title> = "<Title>";`。
2. **權限目錄**：`MeetingRecord.Business/Services/Other/RolePermissionService.cs` 的 `GetRoleListPermissionAllName()` 把常數放進合適的群組，角色管理頁才勾得到。
3. **選單**：`MeetingRecord.Web/Datas/Menu.json` 加節點，`id` 全檔唯一，`icon` 要在 `MenuIconTests` 的允許清單裡。
4. **選單與權限的對應**：`MeetingRecord.Web/Components/Layout/SidebarMenuService.cs` 的 `MenuPermissionMap` 加 `[id] = MagicObjectHelper.角色_<Title>,`。
5. **資料表**：`MeetingRecord.AccessDatas/BackendDBContext.cs` 加 `DbSet`，再產生 migration（本系統只支援 SQLite）：

   ```powershell
   cd src/MeetingRecord
   dotnet ef migrations add Add<Name> --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
   ```

6. **服務註冊**：`MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs` 加 `services.AddScoped<<Name>Service>();`（`Program.cs` 不直接 `AddScoped`）。
7. **API 包裝註冊**：`MeetingRecord.Web.Client/Program.cs` 加 `builder.Services.AddScoped<<Name>Api>();`。
8. **JSON 型別**：`MeetingRecord.Web.Client/Api/ClientJsonContext.cs` 加 4 行 `[JsonSerializable(...)]`（分頁結果、單筆結果、搜尋條件、新增修改輸入）。WASM 端的 JSON 一律走原始碼產生器，漏了會在修剪後的執行期出錯。

登錄完：

```powershell
dotnet build src/MeetingRecord/MeetingRecord.slnx -c Release
dotnet test src/MeetingRecord/MeetingRecord.Tests -c Release --filter "FullyQualifiedName~<Name>ApiTests"
dotnet test src/MeetingRecord/MeetingRecord.Tests -c Release   # 頁面慣例的守門測試會掃到新頁面
```

## 4. 改成自己的欄位

骨架只有名稱、描述、啟用三個欄位。加欄位時一路改下去：

1. 實體（`AccessDatas/Models`）→ 新的 migration。
2. `<Name>CreateUpdateDto`（驗證屬性、`JsonPropertyName`）；有集合欄位時 `<Name>Dto.Clone()` 要一併複製集合。
3. 服務的 `AddAsync`／`UpdateAsync`／`ToDto`；可排序的欄位加進 `SearchAsync` 的 `switch`（API 的 `sortBy` 不分大小寫，頁面送的是欄位的 `DataIndex`）。
4. 頁面的 `<Column>` 與對話窗的 `<FormItem>`。
5. 測試補上這個欄位的規則。

規則（重複、狀態限制、預設值）一律寫在服務層，Controller 只轉換狀態碼，頁面只顯示結果——API 也可能被外部程式直接呼叫。

## 5. 頁面的寫法（骨架已照做，改的時候不要破壞）

- **登入與權限**：登入狀態（未登入、停用、須改密碼）由 `WasmLayout` 統一處理。頁面在 `OnInitializedAsync` 先 `PermissionView.LoadAsync()`，`CanAccessPage` 不通過就顯示「你沒有權限存取此頁面」並呼叫 `PageAccessApi.RecordPageDeniedAsync("<路由>")` 留稽核（`PageAccessDeniedAuditTests` 會掃）。
- **按鈕**：新增、修改、刪除用 `PermissionView.Can(頁, PermissionActions.Xxx)` 決定顯示；API 端另有 `[HasPermission]`，畫面藏按鈕不等於安全。圖示按鈕外包 `HoverTip`。
- **編輯前先複製**：`OnEditAsync` 用 `Clone()` 再綁表單，取消時清單上的那筆不受影響。
- **對話窗**：`Visible` 用單向繫結（不用 `@bind-Visible`），開關一律由頁面決定，伺服器擋下時視窗與填的內容都留著；要有 `Class="form-modal-*"` 尺寸類別。
- **送出**：「確定」與 Enter 共用 `FormSubmitGate`（`submitGate.RunAsync(SaveModalAsync)`、Enter 用 `FormSubmitGate.EnterSettleDelay`），存檔開頭 `if (!modalVisible) return;`，連按或關窗動畫期間再按都不會新增兩筆。
- **鍵盤**：一律走 `FormKeyboardHelper.IsSubmit`／`IsCancel`。⚠️ 不要自己比對 `args.Key == "Enter"`：會漏掉 `IsComposing`，中文輸入法按 Enter 選字就把對話窗存檔關閉。表單裡有下拉或日期欄時，外層包 `<div @onkeydown:stopPropagation="true">`，並把頁面加進 `FormEnterGuardTests.CrudForms`。
- **放棄修改**：取消時用 `FormDirtyHelper` 比對開窗當下的快照，有改過先確認；`isDiscardConfirming` 旗標防止 Esc 同時觸發兩條路而疊出兩個確認視窗。
- **錯誤訊息**：API 的失敗原因在 `ApiResult.ErrorMessage`（`Message` 只是通稱）；401 時 `ApiClient` 已導回登入頁，頁面不再跳通知。
- **分頁**：搜尋條件變更時頁碼回到 1；刪掉最後一頁唯一一筆時夾回最後一頁再載一次。
- **多選刪除**（需要時）：`<Selection />` 要包在 `CanDelete` 判斷裡（`TableSelectionColumnTests`），參考分類清單的 `BatchSelectionBar`＋`BatchRunner`。

## 6. 依需求再加

| 需求 | 做法 |
|------|------|
| 資料屬於專案（要依團隊決定誰看得到） | 服務層的查詢與寫入一律經 `ProjectAccessService`，看不到的當成不存在（回 404）；參考 `TodoService`、`MeetingService` |
| 要留稽核 | 服務注入 `IAuditLogService`，在 `AuditActions` 加動作常數，寫入成功後 `WriteForCurrentUserAsync`，detail 帶 `via=Web／Api`（參考 `CategoryService`） |
| 只限管理者的頁面 | Controller 用 `[AdminOnly]` 取代 `[HasPermission]`；頁面用 `PermissionView.IsAdmin` 守門；權限鍵**不要**放進權限目錄，選單 id 加進 `SidebarMenuService.AdminOnlyMenuIds`、不放 `MenuPermissionMap`（參考稽核紀錄頁 `AuditLogPage`） |
| 刪除只要求修改權限、或「任一動作即可」 | `[HasPermission(頁, PermissionActions.Edit)]`；任一動作用 `[HasPermission(頁, new[] { PermissionActions.Create, PermissionActions.Edit })]`（參考 `TeamController`） |
| 外部程式會依賴的 API | 在 `docs/architecture/Web API 端點目錄.md` 補上端點 |

## 7. 參考範例

功能最接近骨架的是**分類清單**，要加功能時照它的寫法：

- 頁面：`Web.Client/Pages/Categories/CategoryPage.razor(.cs/.css)`（多了負責團隊、啟用切換、多選刪除）
- API 包裝：`Web.Client/Api/CategoryApi.cs`；JSON 型別：`Web.Client/Api/ClientJsonContext.cs`
- Controller：`Web/Controllers/CategoryController.cs`
- 服務：`Business/Services/DataAccess/CategoryService.cs`（AdapterModel＋AutoMapper、稽核）
- 測試：`Tests/CategoryApiTests.cs`、授權矩陣 `Tests/ApiIntegrationTests.Authorization.cs`

## 8. 手動測試清單（交付前必跑）

1. 開啟頁面後資料正確載入（含總筆數）；沒有權限的帳號看到「你沒有權限存取此頁面」，稽核紀錄有一筆。
2. 搜尋關鍵字可過濾；點欄位排序可切換升冪／降冪；換頁後資料正確。
3. 新增：必填未填顯示驗證錯誤；名稱重複時顯示伺服器的訊息、對話窗留著；成功後列表看得到。
4. 修改：開啟的資料正確；取消後清單那筆不變；儲存後更新時間跟著變。
5. 刪除：有二次確認；成功後列表更新。
6. 鍵盤：Enter 送出、Esc 關閉。**用注音打字按 Enter 選字，對話窗不可以關閉**。
7. 角色只有「檢視」時，新增、修改、刪除按鈕都不出現；直接呼叫 API 回 403。
