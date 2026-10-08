# 登入與帳號流程 PRD

- 文件版本：1.3
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：樣板（NET10-Blazor-Starter）既有核心功能
- 最後核對日期：2026/10/08

## 一、目標與範圍

提供本專案的身分驗證與帳號自助維護能力，涵蓋本地帳密登入、Google OAuth2 第三方登入、登出、待審核導向、個人 API 密碼設定與變更密碼。網頁採 Cookie 驗證、API 採 JWT Bearer，兩者共用同一份使用者與 RBAC 權威來源。

- **範圍**：`/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Profile`、`/ChangePassword`；Google 導向端點 `/Auths/Google/Login`、`/Auths/Google/Callback`；API 端 `/api/v1/auth/login`、`/refresh`、`/me`。帳號安全（PBKDF2 雜湊、登入失敗鎖定、登入時雜湊自動升級）。
- **非範圍**：帳號 CRUD 與角色／團隊指派見 [使用者管理](使用者管理-prd.md)；角色與權限矩陣見 [角色管理](角色管理-prd.md)。二階段驗證（TOTP）僅有資料模型與 `TotpService` 骨架，預設關閉、尚未提供強制啟用 UI，不在本次驗收範圍。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/Auths/Login` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名 | 所有人 |
| `/Auths/Logout` | 無版面 | 已登入 | 所有登入者 |
| `/Auths/Pending` | `NoFooterLayout` | 匿名 | Google 自動建帳待審核者 |
| `/Profile` | 預設版面 | 已登入（`AuthenticationStateHelper.Check`）| 需設定 API 密碼者。**0.4.113 起從右上使用者選單移除**：它寫的就是同一組登入密碼，對帳號密碼登入的人等於第二個「變更密碼」；頁面保留，之後接外部系統時再開放，目前只能直接輸入網址進入 |
| `/ChangePassword` | 預設版面 | 已登入 | 需變更密碼者 |
| `/api/v1/auth/login`、`/refresh` | — | 匿名（帳密換 JWT）| API 用戶端 |
| `/api/v1/auth/me` | — | JWT Bearer | API 用戶端 |

## 三、畫面與欄位

- **登入頁**：帳號、密碼、驗證碼（4 碼數字，前端產生存於隱藏欄位 `CaptchaCode`，以 `Ordinal` 比對）、「記住我」核取方塊。`GoogleOAuthSettings.IsConfigured` 為真時顯示「使用 Google 登入」按鈕，連結至 `/Auths/Google/Login`（帶 `returnUrl`）。前端逐項檢查空值與驗證碼，錯誤顯示「請輸入帳號／密碼／驗證碼」「驗證碼錯誤」。
- **待審核頁**：靜態說明，告知 Google 帳號已建立但預設停用，須管理者啟用後再登入，提供返回登入頁連結。
- **個人資料（設定 API 密碼）**：新密碼、確認新密碼；已有本地密碼者另需「目前密碼」。設定後可用「帳號（Email）＋此密碼」呼叫 `/api/v1/auth/login` 取得 JWT。`support` 開發帳號被禁止。
- **變更密碼**：目前密碼、新密碼、確認新密碼（`[Compare]` 驗證一致）。`support` 開發帳號被禁止。

## 四、內部系統運作

- **本地登入**（`MyUserServiceLogin.LoginAsync`，View → Service → `BackendDBContext`）：以 `Account` 查 `MyUser`；帳號不存在或密碼錯誤一律回「帳號或者密碼不正確」（不區分，避免帳號枚舉）。密碼以 `SecurePasswordHasher.VerifyPassword`（PBKDF2）驗證。
- **帳號鎖定**：失敗時 `AccessFailedCount++`，達 `MaxFailedAccessAttempts=5` 設 `LockoutEndUtc = UtcNow + 15 分`；鎖定期間回「帳號已鎖定，請稍後再試。」成功登入後將 `AccessFailedCount` 歸零、`LockoutEndUtc` 清空。
- **雜湊自動升級**：驗證回傳 `SuccessRehashNeeded`（舊格式）時，即時以 PBKDF2 重新雜湊並存回。
- **Cookie 簽發**（Login.razor.cs）：建立 `ClaimTypes.Role=User`、`Name`、`NameIdentifier=Account`、`Sid=Id`，以 `CookieAuthenticationScheme` `SignInAsync`；`IsPersistent = RememberMe`（記住我 → 持久性 Cookie），`RedirectUri` 取 `ReturnUrl` 或 `/App`。
- **Google 登入**（`ExternalAuthController` + `ExternalLoginService.FindOrCreateAsync`）：Callback 驗證 `ExternalCookieScheme` 後，依序「GoogleId 比對 → Email 連結既有帳號 → 自動建立停用新帳號」（`Status=false`、`IsAdmin=false`、`Password=""`、`Salt=null`、指派預設角色）。`!Status` 導向 `/Auths/Pending`，否則簽發 Cookie 並導回本地安全的 `returnUrl`。
- **登出**：`SignOutAsync(CookieScheme)` 後 `NavigateTo("/Auths/Login", forceLoad: true)`。
- **登入後狀態**（`AuthenticationStateHelper.Check`）：驗證已登入、`Sid` 有效、使用者存在且 `Status` 啟用、具角色；`NeedChangePasswordAsync` 為真且不在改密碼頁時強制導向 `/ChangePassword`。0.4.113 起條件是「`MyUser.MustChangePassword` 為 true **或**密碼等於 `123456`」：管理者建立帳號、或替別人重設密碼時設旗標（改自己的不設；`support` 一律不設，因為它被禁止改密碼），本人透過任何一個改密碼入口改完就清掉。載入 `CurrentUser`，`RoleList` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync`（RBAC 多角色聯集）為權威、`TeamList` 由 `EffectiveTeamResolver` 決定。
- **API 登入**（`AuthController`）：`login` 以帳密換 `TokenResponseDto`（JWT + Refresh），`refresh` 換新 Token，`me` 回目前使用者；一律包 `ApiResult<T>`，失敗回 401。
  - 0.4.115：`login` 對需要改密碼的帳號（同 `NeedChangePasswordAsync` 的條件）回 401「請先登入網頁變更密碼後，再使用 API。」以前強制改密碼只在網頁生效。
  - 0.4.115：`refresh` 換發前以 `MyUserServiceLogin.GetUserForTokenRefreshAsync` 重查資料庫，停用、鎖定、刪除或待改密碼都回 401，身分以資料庫為準。以前只看舊權杖的內容，被停用的人在 7 天內都能一直換到新權杖。
  - 0.4.115：`PermissionChecker` 對停用的帳號一律回 false（管理者也一樣），手上的存取權杖在到期前也不能再呼叫受保護 API。
- **稽核**：登入寫入 `Login.Success` / `Login.Failed` / `Login.LockedOut`（`AuditLog`）。

## 五、權限與安全

- 網頁 Cookie、API JWT 各自獨立；權限判定統一由 RBAC 表（`IPermissionChecker`）為單一權威，管理員短路一律通過。
- 錯誤訊息不洩漏帳號是否存在；輸出模型不含密碼、`Salt`、Token（`MyUserService.OtherDependencyData` 清空密碼欄位）。
- **帳號停用者在登入這一步就擋下**（0.4.113，`MyUserServiceLogin.LoginAsync`）：密碼驗證**成功後**才檢查 `Status`，停用時回「此帳號已停用，請聯絡管理者。」並寫 `Login.Disabled` 稽核；密碼錯誤時一律回「帳號或者密碼不正確」，不讓人試探帳號是否存在或已停用。網頁與 API 登入共用此方法。以前要等登入成功、進系統後才由 `Check` 發現停用而登出，畫面會閃一下像當機；`Check` 的停用檢查保留作為登入期間被停用的後備。Google 自動建帳預設停用並導向待審核，須管理者啟用。
- 密碼雜湊與儲存、記住我原理、Google 流程細節見交叉文件，本 PRD 不重述。

## 六、錯誤與邊界

- 驗證碼錯誤／欄位空白：停留登入頁並重新產生驗證碼。
- 連續 5 次失敗鎖定 15 分鐘；鎖定到期後（`LockoutEndUtc` 過期）可再次登入。管理者**重設密碼即解鎖**；只編輯其他欄位不會解鎖（0.4.115，見 [使用者管理](使用者管理-prd.md)）。
- Google Callback 缺 `subject`／`email`：登出外部身分並導回登入頁。
- `support` 帳號於 `/Profile`、`/ChangePassword` 一律被拒；Google 帳號首次設 API 密碼免驗舊密碼。
- 使用者無角色、`RoleView` 為 null 或 `TabViewJson` 解析失敗：導向登出。

## 七、驗收與測試

- `MeetingRecord.Tests/MyUserServiceLoginTests.cs`：新舊雜湊登入、舊雜湊自動升級、密碼錯誤、5 次失敗鎖定並拒絕正確密碼、成功後歸零、鎖定到期放行、成功／失敗稽核。
- `MeetingRecord.Tests/MyUserServicePasswordTests.cs`：`ChangeOwnPasswordAsync` 正確／錯誤舊密碼、空白新密碼、確認不一致、`support` 帳號被拒。
- `MeetingRecord.Tests/SecurePasswordHasherTests.cs`：自述式雜湊、非決定性、新舊格式驗證與要求 rehash。
- `MeetingRecord.Tests/AuthenticationStateHelperTests.cs`：未驗證／無效 Sid／查無使用者／停用／無角色／壞 RoleJson 導向登出、需改密碼導向、多角色聯集初始化。
- `MeetingRecord.Tests/TotpServiceTests.cs`：TOTP 產碼／驗證（骨架，預設關閉）。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Auths/Login.razor`（`<EditForm FormName="login">` 登入表單）、`Login.razor.cs`（`Login.LoginUser`：驗證與 Cookie 簽發）
- `src/MeetingRecord/MeetingRecord.Web/Components/Auths/Logout.razor.cs`（`Logout.OnInitializedAsync`：登出）、`Pending.razor`（`@page "/Auths/Pending"`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Profile.razor`（`OnSubmitAsync`）、`ChangePassword.razor`（`OnSubmitAsync`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/MyUserServiceLogin.cs`（`MyUserServiceLogin.LoginAsync`：鎖定、PBKDF2、升級、稽核）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/ExternalLoginService.cs`（`ExternalLoginService.FindOrCreateAsync`：Google 查找／建立）
- `src/MeetingRecord/MeetingRecord.Business/Services/Other/AuthenticationStateHelper.cs`（`AuthenticationStateHelper.Check`：登入後檢查與 RBAC 載入）
- `src/MeetingRecord/MeetingRecord.Web/Controllers/AuthController.cs`（`AuthController.Login`／`Refresh`／`Me`）、`ExternalAuthController.cs`（`ExternalAuthController.Login`／`Callback`）
- 交叉連結：[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)、[記住我登入原理說明](../security/記住我登入原理說明.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
