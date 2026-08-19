# 以 `RoleViewView` 為藍本手動開發新 CRUD 頁面的計畫

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/07/14

> 目標：完整複刻 `RoleViewView` 的「新增、查詢、更新、刪除、過濾、排序、分頁、驗證、通知」行為，並保留同等結構（Page + View + Service + AdapterModel + Entity + 註冊 + 樣式）。

---

## 1. 先做盤點（複刻前準備）

1. **確認資料實體與欄位**
   - 目標 CRUD 的資料表實體（Entity）欄位需定義完成，至少要有 `Id`、主要顯示欄位、建立/更新時間欄位。
   - 參考：`RoleView` 的資料結構。  

2. **確認前後端模型對應**
   - 建立 AdapterModel（ViewModel）承接畫面資料與驗證屬性。
   - 若會在編輯時修改集合/巢狀物件，需提供 `Clone()` 避免直接改到表格來源。

3. **確認 AutoMapper 映射**
   - 新增 `Entity <-> AdapterModel` 雙向映射。

4. **確認 DI 註冊與路由入口**
   - 在 `Program.cs` 加入對應 Service。
   - 建立對應 `Page.razor` 並放入 `<YourEntityView />`。

---

## 2. 後端服務層複刻計畫（DataAccess Service）

以 `RoleViewService` 為模板建立 `YourEntityService`：

1. **GetAsync(DataRequest)**：列表查詢
   - 支援 `Search` 關鍵字篩選。
   - 支援 `SortField` + `SortDescending` 排序（每個可排序欄位要明確分支）。
   - 支援分頁 `CurrentPage/PageSize/Take`。
   - 回傳 `DataRequestResult<T>`（`Result + Count`）。

2. **GetAsync(id)**：單筆查詢
   - 以 `AsNoTracking` 取單筆後映射成 AdapterModel。

3. **AddAsync / UpdateAsync / DeleteAsync**
   - 參照既有流程：清除追蹤（`CleanTrackingHelper.Clean<T>`）→ 寫入 → `SaveChangesAsync()`。
   - 全部包裝 `try/catch`，失敗時 `Logger.LogError` 並回傳 `VerifyRecordResult`。

4. **BeforeXxxCheckAsync**（新增/修改/刪除前檢查）
   - 新增重複檢查。
   - 修改時做「資料仍存在」與「唯一性衝突」檢查。
   - 刪除前檢查可放關聯限制邏輯。

5. **其他依賴資料初始化**
   - 若有 JSON 欄位/權限樹/外部依賴，集中在 `OtherDependencyData` 類方法處理，避免散落在 UI。

---

## 3. 前端 View 元件複刻計畫（.razor + .razor.cs + .razor.css）

### 3.1 Razor 結構（UI 佈局）

1. **工具列（Toolbar）**
   - 左側：新增、重新整理。
   - 右側：搜尋輸入框、清空搜尋（有值才顯示）、搜尋按鈕。

2. **Table（RemoteDataSource=true）**
   - `@bind-PageIndex`、`@bind-PageSize`、`@bind-Total`。
   - `OnChange=OnTableChange` 處理分頁與排序。
   - 至少一個 `ActionColumn` 放修改/刪除按鈕。

3. **Modal + EditForm**
   - `OnOk` 統一走儲存。
   - `DataAnnotationsValidator + ValidationSummary + ValidationMessage`。
   - 使用 `InputWatcher` 將 `EditContext` 傳到 code-behind，供 `Validate()` 使用。
   - 依欄位型態放對應元件（Input、Select、Checkbox...）。

### 3.2 Code-behind 行為（事件與狀態）

1. **狀態欄位**
   - `_pageIndex/_pageSize/_total/searchText/sortField/sortDirection`。
   - `modalVisible/isNewRecordMode/CurrentRecord`。

2. **ReloadAsync**
   - 統一列表資料載入入口，任何操作後都回到這裡。

3. **OnTableChange**
   - 更新頁碼。
   - 解析目前排序欄位與方向（含 `FieldName` fallback 處理），再 `ReloadAsync()`。

4. **OnSearchAsync / OnRefreshAsync**
   - 搜尋時頁碼重設為 1。
   - 重新整理後提供通知。

5. **OnAddAsync / OnEditAsync / OnDeleteAsync**
   - Add：建立新物件與預設值。
   - Edit：使用 `Clone()` 防止雙向綁定直接污染列表項目。
   - Delete：先 Confirm，再刪除，再通知，再 Reload。

6. **OnModalOKHandleAsync**
   - 先做 `LocalEditContext.Validate()`。
   - 新增/修改分流：補齊 `CreateAt/UpdateAt` 或僅更新 `UpdateAt`。
   - 成功後關閉 Modal 並 Reload。

7. **鍵盤操作**
   - Enter 觸發儲存、Esc 關閉，提升可用性。

---

## 4. 手動開發步驟（建議順序）

1. 建立 `Entity`（AccessDatas/Models）。
2. 建立 `AdapterModel`（Models/AdapterModel）+ DataAnnotations。
3. 在 `AutoMapping` 加入雙向映射。
4. 建立 `YourEntityService`（Business/Services/DataAccess）並完成 CRUD + 查詢排序過濾分頁。
5. 在 `Program.cs` 註冊 `AddScoped<YourEntityService>()`。
6. 建立 `YourEntityView.razor`（照 RoleViewView 版型）。
7. 建立 `YourEntityView.razor.cs`（照 RoleViewView 的狀態與事件流程）。
8. 建立 `YourEntityView.razor.css`（先複製同命名 class，再微調）。
9. 建立 `YourEntityPage.razor` 與 `@page` 路由。
10. 本機驗證（新增/查詢/修改/刪除/過濾/排序/分頁/驗證提示）。

---

## 5. 「完美複刻」的關鍵注意事項

1. **排序欄位名稱一致性**
   - UI `DataIndex` 必須對應 Service `SortField` 比對字串（通常用 `nameof(AdapterModel.Property)`）。

2. **分頁行為一致性**
   - 任何查詢條件變更（搜尋、重設）時，頁碼要回到第 1 頁。

3. **編輯時資料隔離**
   - 使用 `Clone()` 後再編輯，避免列表資料提早變動造成 UI 錯亂。

4. **Validation 上下文取得**
   - 若要在 Modal 的 `OnOk` 觸發 `Validate()`，要確保 `LocalEditContext` 已被 `InputWatcher` 正確帶入。

5. **通知與錯誤體驗一致**
   - 成功/失敗都要有明確通知（`NotificationService`/`MessageService`）。

6. **資料追蹤一致性（EF Core）**
   - Update/Delete 前先清 tracking，降低同一 DbContext 追蹤衝突。

7. **初始化商業邏輯收斂**
   - 新增時的預設值（如權限預設）寫在 Add 流程，不要散在 UI 多處。

---

## 6. 參考與引用程式碼清單（複製模板時優先順序）

1. **主樣板元件（首要）**
   - `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/RoleViewView.razor`
   - `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/RoleViewView.razor.cs`
   - `src/MeetingRecord/MeetingRecord.Web/Components/Views/Admins/RoleViewView.razor.css`

2. **服務層樣板**
   - `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/RoleViewService.cs`

3. **模型樣板**
   - `src/MeetingRecord/MeetingRecord.Models/AdapterModel/RoleViewAdapterModel.cs`
   - `src/MeetingRecord/MeetingRecord.AccessDatas/Models/RoleView.cs`
   - `src/MeetingRecord/MeetingRecord.Models/Systems/DataRequest.cs`
   - `src/MeetingRecord/MeetingRecord.Models/Systems/DataRequestResult.cs`

4. **通用機制樣板**
   - `src/MeetingRecord/MeetingRecord.Web/Components/Commons/InputWatcher.cs`
   - `src/MeetingRecord/MeetingRecord.Business/Models/AutoMapping.cs`
   - `src/MeetingRecord/MeetingRecord.Web/Program.cs`
   - `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Admins/RoleViewPage.razor`

---

## 7. 手動測試清單（交付前必跑）

1. 開啟頁面後資料可正確載入（含總筆數）。
2. 搜尋關鍵字可過濾，清空搜尋可還原。
3. 點欄位排序可切換升冪/降冪，資料順序正確。
4. 分頁切頁後資料正確。
5. 新增：
   - 必填未填會顯示驗證錯誤。
   - 儲存後出現成功通知，列表可見新資料。
6. 修改：
   - 開啟編輯資料正確。
   - 儲存後資料更新且時間欄位更新。
7. 刪除：
   - 有二次確認。
   - 刪除成功後列表更新。
8. 鍵盤操作：Enter 可送出、Esc 可關閉。
9. 例外路徑：服務拋錯時有錯誤紀錄與使用者可理解訊息。

---

## 8. 建議的實作原則（降低後續維護成本）

1. **命名對齊**：`XxxPage / XxxView / XxxService / XxxAdapterModel`。
2. **方法對齊**：`ReloadAsync / OnTableChange / OnAddAsync / OnEditAsync / OnDeleteAsync / OnModalOKHandleAsync`。
3. **單一資料入口**：所有 UI 刷新都走 `ReloadAsync`。
4. **商業邏輯集中**：預設值、JSON 轉換、限制檢查放 Service。
5. **UI 僅做互動**：按鈕事件只組裝參數與呼叫 Service。

> 依照此計畫，你可以在不引入額外框架的前提下，將新 CRUD 頁面以最小偏差複刻成與 `RoleViewView` 一致的結構與行為。
