# WASM 遷移 M1：SignalR Hub 骨架（1.1.1 → 1.1.2）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：1.1.2
- 首次實作版本：1.1.2
- 最後核對日期：2026/11/10

使用者要求：

> 開始（M1 第三步：SignalR 即時推播骨架，先接通知鈴未讀數）

---

## 一、原因

- Blazor Server 頁靠自己的連線收伺服器事件（通知鈴、進度面板）；WASM 前端沒有那條連線，要改由一個 SignalR Hub 推播（[09 號遷移評估](../planning/09-Blazor-WASM-遷移評估與測試策略.md) §4.4）。
- 這一步先把通道打通，只接最簡單的一種：通知的未讀數。轉錄與會議紀錄生成的進度留到 M3。

## 二、修改

- **伺服器**：
  - `Web/Hubs/AppHub`（`/hubs/app`）：用 policy scheme 驗證（同 API），連線時依使用者 Id 加入群組 `user:{id}`；沒有「用戶端呼叫伺服器」的方法。
  - `Web/Hubs/HubNotificationBridge`（hosted service）：訂閱 `IUserNotificationNotifier`，對相關使用者的群組送 `NotificationsChanged`。推播**不帶資料**，前端收到再打 API 重查；送不出去只記警告，不影響寫通知的一方。Blazor Server 頁的通知鈴照舊直接訂閱事件，兩邊並存。
  - `Controllers/NotificationController`：`GET /api/v1/notifications/unread-count`，只需登入、只算自己的通知（列入授權守門掃描「只需登入」白名單）。
  - `Program.cs`：`AddSignalR()`、`MapHub<AppHub>`；Cookie 的未登入／沒權限回 401／403 JSON 的範圍從 `/api` 擴大到 `/hubs`（原本連 Hub 未登入會 302 到登入頁）。
- **WASM 前端**：
  - 套件 `Microsoft.AspNetCore.SignalR.Client` 10.0.14。
  - `Realtime/AppHubConnection`：連 `/hubs/app`、自動重連，對外提供「通知有變動」與「重連」事件；連不上只記錄，不讓頁面掛掉。
  - `Api/NotificationApi`：未讀數。
  - `WasmLayout` 頂端顯示未讀數：先查一次，收到推播或重連後重查。
- **測試共用**：網頁登入表單取得 Cookie 的寫法抽成 `Tests/TestCookieLogin`（`CookieApiTests` 與新的 `AppHubTests` 共用）。
- 文件：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §4、[認證授權與權限機制](../security/認證授權與權限機制.md)、[架構總覽](../architecture/架構總覽.md)、09 號 §4.4 與「十、之二」。

## 三、測試

- 新增 `AppHubTests`（用真的網頁登入 Cookie，LongPolling 連 Hub）：登入者收得到自己的通知變動、收不到別人的；未登入連不上（401）；未讀數只算自己的未讀通知。
- 新增端對端 WASM-05：負責人開著 WASM 頁，協作成員在 Server 待辦頁改狀態，負責人的未讀數不必重新整理就變多。
- 抓錯檢查：讓轉送服務不訂閱事件 → 「收得到自己的通知變動」與 WASM-05 都失敗。驗完已還原。
- 下載量：加 SignalR 用戶端後 Brotli 合計 +0.18 MB（全部 `.br` 4.50 MB，只下載一個語系檔約 4.15 MB），修剪警告 0。
- 全套單元／API 與端對端照跑。
