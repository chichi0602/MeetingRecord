# WASM 遷移 M6：拆除 Blazor Server 互動模式（1.1.45 → 1.1.46）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.46
- 首次實作版本：1.1.46
- 最後核對日期：2026/11/11

使用者要求：

> 需要做 M6 並且一次性做完。

---

## 一、原因

1.1.45 後應用程式的頁面全部是 WASM，伺服器只靜態算繪登入、登出與錯誤頁。Blazor Server 互動模式（circuit、`MainLayout`、重連視窗等）已經沒有任何頁面在用，這版拆掉。

## 二、修改

### 2.1 拿掉 Server 互動模式

- `Program.cs` 拿掉：
  - `AddInteractiveServerComponents().AddHubOptions(...)`（10 MB 的 Hub 訊息上限只給 circuit 用）；
  - `AddInteractiveServerRenderMode()`。
- `AddSignalR()` 保留：WASM 的推播通道 AppHub 還要用。
- Server 的 `Routes.razor`：
  - 只服務靜態頁（登入、登出、等待核准、錯誤頁），預設版面改成 `NoFooterLayout`；
  - 拿掉兩層 `LoggingErrorBoundary`、AntDesign 容器與往 WASM 頁的整頁載入。
- `App.razor` 拿掉 `ReconnectModal`。

### 2.2 刪除只給 Server 頁用的程式

- 版面與重連：`MainLayout`、`ReconnectModal`（含 .js／.css）。
- circuit 與錯誤邊界：`ApplicationCircuitHandler`（含註冊）、`LoggingErrorBoundary`。
- 範例檔：`RoleViewViewSample`。
- Server 頁的資料來源：`ServerNotificationSource`、`ServerJobProgressSource`、`ServerAiChatSource`（通知鈴、進度面板、AI 問答在 WASM 頁走 API）。
- Server 端 `FileDownloadInterop`、`ClipboardInterop`、`TextEditorInterop`、`TodoAutoFitInterop` 的註冊（這些類別只在 WASM 端用）。
- `ClientIpProvider` 的 Blazor 連線 IP：只取 HTTP 請求的連線 IP。
- `AuthenticationStateHelper` 改成純靜態類別：
  - 只剩 `CanAccessPage`，以及由以前的 `CheckAccessAction` 改成純函式的 `CanDoAction`；
  - `Check`、`GetUserInformation`、`CheckIsAdmin`、`CheckAccessPage`、`RecordPageAccessDeniedAsync` 已無呼叫者，一併移除；
  - `AuthenticationCheckResult` 一併刪除；
  - `SidebarMenuService` 拿掉收 `AuthenticationStateHelper` 的多載。

### 2.3 測試

- **`AuthenticationStateHelperTests` → `PermissionRulesTests`**：
  - 預設角色的頁面與動作權限矩陣（檢視者、一般使用者、主管、管理者）改成直接驗純函式，另加「整頁鍵＝全部動作」；
  - 以前經 `Check` 驗的帳號狀態，改成驗 `UserSessionService`（Session API 用）：不存在、停用、沒有角色（各帶登出原因）、須改密碼、正常；
  - 「角色 JSON 壞掉」只發生在 Server 頁的舊流程（權限現在一律從 RBAC 表取），不再測。
- **`PageAccessDeniedAuditTests`**：
  - 改成只掃 `Web.Client`、只認 `PageAccessApi.RecordPageDeniedAsync`；
  - 以前寫死掃 `Components/Views`，刪檔後在新環境會因資料夾不存在而丟例外。
- **`LayoutMountTests`**：拿掉 `MainLayout` 三則，`WasmLayout` 補上通知鈴。
- **`ModalSizeClassTests`**：拿掉範例檔的豁免。
- **`ClientIpProviderTests`**：拿掉 Blazor 連線 IP 兩則。

### 2.4 文件與版本

- 只修正已經不對的說法：
  - 架構總覽、認證授權、測試指南；
  - 登入與帳號流程 PRD、稽核紀錄 PRD、介面視覺設計規範；
  - 09 號「十、之七」。
- 只適用 Blazor Server 的開發規範（`Clone()`、`CleanTrackingHelper`、DbContext 每條連線等），在 1.1.48 一起改寫。
- 版本 1.1.45 → 1.1.46。

## 三、外部可見的行為變更

| 項目 | 以前 | 現在 |
|---|---|---|
| 伺服器的 Blazor Server 連線（`/_blazor`） | 首頁等少數頁還會建立 | 不再有 |
| 登入、登出、錯誤頁 | 經 Blazor Server 的路由與版面 | 靜態算繪，畫面不變 |

## 四、測試

- **單元／API**：2198 則全過。
- **端對端**：NAV、LGN（登入）、L4 全過；全套照跑。
