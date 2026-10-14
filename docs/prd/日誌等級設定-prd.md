# 日誌等級設定 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.120
- 首次實作版本：0.4.120（移植自 NET10-Blazor-Starter）
- 最後核對日期：2026/10/14

## 一、目標與範圍

排查問題時，管理者需要暫時看到比平常更詳細的日誌（Debug／Trace），問題解決後再調回來；過去只能改 `nlog.config`。本功能提供一個頁面，在**執行期**調整應用程式日誌的最低等級。

- 範圍：`/log-level-setting` 頁面、顯示目前生效等級與系統預設等級、套用 TRACE～FATAL、還原為系統預設、稽核紀錄。
- 非範圍：寫回 `nlog.config` 或任何設定檔（重新啟動即恢復）、調整框架日誌（`Microsoft.*`、`System.*`）、依類別／命名空間個別設定等級、多台主機同步。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/log-level-setting` | 系統管理 → 監控與診斷 → 日誌等級設定（id 38） | **只限管理者**（不在角色權限目錄，`SidebarMenuService.AdminOnlyMenuIds`） | 系統管理員／維運 |

## 三、畫面與欄位

- 兩張卡片：「目前生效等級」（有執行期覆寫時副標以警示色寫「執行期覆寫中，重新啟動後恢復預設」）、「系統預設等級」（來自 `nlog.config` 的 `<logger name="*">` minlevel）。
- 警示條說明：只在執行期有效、重新載入 `nlog.config` 後仍保留、只影響應用程式日誌、Debug／Trace 會大幅增加日誌量。
- 單選：TRACE、DEBUG、INFO、WARN、ERROR、FATAL，各附一句用途說明。
- 按鈕（圖示＋文字）：「套用」（選的等級與目前相同時停用）、「還原為系統預設等級」（沒有覆寫時停用）。
- 套用 TRACE 或 DEBUG 前跳確認窗「確認調整日誌等級」（OkText「套用」）；調高等級不確認。
- `nlog.config` 讀不到或找不到 `<logger name="*">` 規則時，整頁只顯示錯誤說明，不提供操作。

## 四、內部系統運作

1. `LogLevelRuntimeState`（Singleton）在 `Program.cs` 的 `app.Build()` 之後呼叫 `Initialize()`：讀 `NLog.LogManager.Configuration`，找出有 target 的 `<logger name="*">` 規則，記下系統預設等級與 NLog 變數，並訂閱 `LogManager.ConfigurationChanged`。
2. 套用：`rule.SetLoggingLevels(level, Fatal)` 後 `LogManager.ReconfigExistingLoggers()`（少了這一行，已建立的 Logger 會沿用快取的等級）。
3. 還原：把系統預設等級套回同一條規則，清掉覆寫。
4. `nlog.config` 被重新載入（`autoReload="true"`）時：補回被清掉的 `BasePath`／`LogFilenamePrefix` 變數、重讀系統預設等級、再把覆寫套回去。
5. 框架規則（`Microsoft.Hosting.Lifetime`、`Microsoft.*`、`System.*`）在 `nlog.config` 設為 `final`，所以調低等級只會讓應用程式日誌變多，不會引來框架日誌洪水。
6. 套用與還原各寫一筆 `AuditLog`：`LogLevel.Apply`／`LogLevel.Restore`，TargetType `NLog`、TargetId `logger:*`，Detail 記「由 X 調整為 Y（僅執行期有效）」。

## 五、權限與安全

- 管理者限定：頁面以 `AuthenticationStateHelper.CheckIsAdmin()` 守門（套用、還原方法內再檢查一次）；側邊欄由 `AdminOnlyMenuIds` 排除非管理者，不看權限鍵。
- 權限判定完成前不渲染任何內容。
- 不寫任何檔案，重新啟動即回到 `nlog.config` 的值。

## 六、錯誤與邊界

- `LogManager.Configuration` 為 null（例如測試主機）或找不到萬用規則：記 Warning，頁面顯示無法使用。
- 套用／還原失敗：訊息「套用失敗，請確認 NLog 設定。」／「還原失敗，請確認 NLog 設定。」。
- 多台主機部署時，每台各自獨立；本系統目前為單機設計。

## 七、驗收與測試

- `MeetingRecord.Tests/LogLevelRuntimeStateTests.cs`（移植自 Starter）：初始化讀到設定檔的預設等級、套用只改應用程式規則不動框架規則、還原與「沒有覆寫時還原回 false」、拒絕 Any／Unknown、找不到萬用規則時降級為不可用、**用專案真正的 `nlog.config` 驗證調到 Debug 只影響應用程式日誌**、`LogLevelRank` 與 NLog 序位一致。
- `MeetingRecord.Tests/MonitoringMenuRegistrationTests.cs`：選單 id 38 在「監控與診斷」底下、名稱與路由正確、屬管理者限定且不在權限目錄。
- 實跑：套用 Debug 後日誌檔開始出現 DEBUG 行；還原後回到 INFO；稽核表各有一筆。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/LogLevelRuntimeState.cs`（`Initialize`、`Apply`、`RestoreDefault`、`OnConfigurationChanged`）
- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/LogModels.cs`（`LogLevelRank`、`LogLevelRankHelper`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Monitoring/LogLevelSettingPage.razor`、`Components/Views/Monitoring/LogLevelSettingView.razor(.cs/.css)`
- `src/MeetingRecord/MeetingRecord.Web/Program.cs`（`#region 執行期日誌等級`）、`nlog.config`
- 交叉連結：[日誌與設定檔說明](../operations/日誌與設定檔說明.md)、[系統健康監控 PRD](系統健康監控-prd.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
