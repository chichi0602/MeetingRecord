# Visual Studio 偵錯 WASM 終止的處理（1.1.56 → 1.1.57）

- 文件版本：1.0
- 文件狀態：已實作（待使用者在 Visual Studio 確認）
- 現行系統版本：1.1.57
- 首次實作版本：1.1.57
- 最後核對日期：2026/11/12

使用者回報：在 Visual Studio（18 Insiders）按 F5 偵錯，跳出「載入 'System.Private.CoreLib.dll' 的中繼資料時發生嚴重錯誤，必須終止偵錯。作業失敗，因為偵錯工具無法取得中繼資料。」

---

## 一、現象與調查

輸出視窗裡，`Managed Wasm Debugger` 的情形：

- 讀不到 .NET 框架組件的中繼資料，包括 `System.*`、`Microsoft.AspNetCore.Components.*`、`Microsoft.Extensions.*`；
- 附有本機路徑的套件組件都成功：AntDesign、OneOf、Markdig、SignalR 等；
- 最後在 `System.Private.CoreLib.dll` 終止偵錯。

調查過程與結論（照實記錄，含推論錯誤的部分）：

1. **第一次推論（錯）**：以為 VS 用了內建的偵錯代理，所以加上 `app.UseWebAssemblyDebugging()` 與 `inspectUri`。使用者重試仍然失敗。
2. VS 的詳細記錄（`%TEMP%\visualstudio-js-debugger.txt`）顯示，VS 本來就啟動套件裡的偵錯代理（`microsoft.aspnetcore.components.webassembly.server/10.0.14/Tools/BlazorDebugProxy/BrowserDebugHost.dll`）。
3. **第二次推論（未證實）**：代理 10.0.14 與瀏覽器實際跑的 WASM 執行環境 10.0.15（SDK 11.0.100 選的 `Microsoft.NET.Sdk.WebAssembly.Pack`）版本不一致。
4. **實測**：用無頭 Chrome 經偵錯代理模擬 VS 的流程：
   - 先在 `WasmLayout.razor` 第 63 行下中斷點，再開 `/meetings`；
   - 會停在 `WasmLayout.OnInitializedAsync`，讀得到 `Task<ApiResult<SessionDto>>`、`List<MenuItemDto>` 等框架型別的變數；
   - **10.0.15 與 10.0.14 兩版的代理都正常**。
   - 所以在全新建置下，網站與偵錯代理這一層沒有問題，版本不一致**不是**已證實的原因。
5. 當時 Debug 輸出（`Web.Client/bin/Debug/net10.0/wwwroot/_framework`）的框架檔還是 2026/11/9 的舊建置產物。實測用的是清掉後重建的輸出，舊檔是否就是原因，要等使用者在 VS 驗證。

## 二、修改

- **清掉舊的 Debug 輸出**：關掉 Visual Studio（正常關閉），刪除 `MeetingRecord.Web`、`MeetingRecord.Web.Client` 的 `bin/`、`obj/` 後重建。
- **ASP.NET Core 的 WASM 相關套件與執行環境同版（10.0.14 → 10.0.15）**：
  - `MeetingRecord.Web`：`Microsoft.AspNetCore.Components.WebAssembly.Server`、`Microsoft.AspNetCore.OpenApi`；
  - `MeetingRecord.Web.Client`：`Microsoft.AspNetCore.Components.WebAssembly`、`Microsoft.AspNetCore.Components.Authorization`、`Microsoft.AspNetCore.SignalR.Client`；
  - `MeetingRecord.Tests`：`Microsoft.AspNetCore.SignalR.Client`（否則還原時報 NU1605 套件降級）。
  - 這是保持一致的做法，不是已證實的解法。
- **偵錯端點（官方範本的標準設定）**：
  - `Program.cs` 在開發環境掛 `app.UseWebAssemblyDebugging()`；
  - `launchSettings.json` 兩個 profile 加上 `inspectUri`。
  - 開發環境多了 `/_framework/debug`，可在瀏覽器按 Shift+Alt+D 用 DevTools 偵錯，也是上面實測用的端點。
- **文件** `docs/operations/啟動與停止系統.md` 第三節「在 Visual Studio 偵錯瀏覽器端（WASM）的程式」：
  - 套件版本要一起升；
  - 再遇到這個錯誤時的處理順序：清 `bin/obj` → 用 DevTools 判斷是不是 VS 本身的問題 → 必要時用 `global.json` 鎖 SDK；
  - 實測結果。
- 版本 1.1.56 → 1.1.57。

## 三、外部可見的行為變更

- 套件修補版升級（10.0.14 → 10.0.15），功能不變。
- 開發環境多了 `/_framework/debug*` 偵錯端點；測試、E2E、正式環境沒有。

## 四、驗證

- 還原、Release／Debug 建置 0 錯誤；`project.assets.json` 是 `Microsoft.AspNetCore.Components.WebAssembly.Server/10.0.15`。
- 開發環境 `GET /_framework/debug` 回 200（偵錯代理的說明頁）；正式環境回 404（只在驗證時臨時給 JWT 簽章金鑰）。
- 經偵錯代理的中斷點實測：見第一節第 4 點。
- 全套 `Run-E2E.ps1 -NoReport`（套件升級後）全部通過：單元／API 2218 則、端對端 126 過 1 略過（同 M0 起的既有略過）；WASM 下載 4.83 MB、修剪警告 0。
- **待使用者確認**：在 Visual Studio 按 F5 能在 `Web.Client` 的中斷點停下。
  - 若仍跳同樣的錯誤，因為代理這一層已確認正常，問題就在 Visual Studio 18 Insiders 本身。
