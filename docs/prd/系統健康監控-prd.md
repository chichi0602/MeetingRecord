# 系統健康監控 PRD

- 文件版本：3.1
- 文件狀態：已實作
- 現行系統版本：1.0.29
- 首次實作版本：樣板（NET10-Blazor-Starter）既有核心功能（0.4.93 擴充為「系統健康度」，0.4.120 改名「系統健康監控」並改為只限管理者，0.4.123 改版為 17 項並重做畫面，1.0.29 新增「AI 用量上限」成為 18 項）
- 最後核對日期：2026/10/15

## 一、目標與範圍

提供管理者一個巡檢頁面「系統健康監控」（`/system-health`），以紅黃綠燈號與健康分數快速判斷基礎設施與本系統專屬相依是否正常，並可展開查看最近 24 小時警告以上的日誌；另提供部署平台使用的機器可讀探針端點。

- 範圍：
  - `/system-health` 巡檢頁。
  - 18 項健康檢查：基礎設施 11 項、本系統功能 7 項。
  - 加權計分與燈號、重新檢查。
  - 「最近 24 小時警告以上日誌」區塊。
  - `/health/live` 與 `/health/ready` 探針。
- 非範圍：
  - 告警通知、歷史趨勢、自動刷新。
  - 外部監控整合、自動修復。
  - **任何會計費的 AI 連線測試**。
- 機制細節不在這裡重寫，見 `docs/features/系統健康監控.md`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-health` | 系統管理 → 監控與診斷（id 36）→ 系統健康監控（id 34） | 只限管理者（`CheckIsAdmin()`＋`SidebarMenuService.AdminOnlyMenuIds`；不在權限目錄、不能勾給角色） | 系統管理員／維運 |
| `/health/live` | 非選單（探針） | 無（匿名） | 部署平台存活探針 |
| `/health/ready` | 非選單（探針） | 無（匿名） | 部署平台就緒探針 |

## 三、畫面與欄位

畫面照本專案 UI 規範：薄頁＋View、不放 h1（標題由 Menu.json 顯示）、燈號只用 `--oat-ok／--oat-warn／--oat-err` 色票。

- **工具列**：
  - 「重新檢查」（`refresh` 圖示＋文字）。執行中顯示載入狀態，重複點擊會被忽略。
  - 右側顯示最後檢查時間。
- **摘要**：
  - 燈號圓點與總分（`NN / 100`）。
  - 狀態文字（正常／警示／異常）。
  - 正常、警示、異常各幾項，以及計分規則的一句說明。
- **檢查項目**：分「基礎設施」「本系統功能」兩組卡片。
  - 每張卡片：燈號、名稱、類別、權重、狀態膠囊、佐證（Evidence）。
  - 有問題時另顯示失敗訊息（FailureMessage），底色跟著燈號。
  - 證據欄的長路徑可任意斷行，窄螢幕（390px）也不產生整頁橫向捲動。
- **18 項檢查與權重**（總計 100）：
  - 基礎設施 72：網站/應用程式(4)、API(4)、資料庫(18)、日誌(6)、身分驗證(8)、**登入金鑰(6)**、檔案系統(8)、主機資源(5)、安全設定(5)、**快取服務(4)**、**例外紀錄管線(4)**。
  - 本系統功能 28：AI 供應商設定(8)、語音轉錄 FFmpeg(5)、背景工作(4)、近期錯誤(4)、**AI 用量上限(3)**（1.0.29）、PDF 匯出(2)、匯率(2)。1.0.29 前為 AI 供應商設定(9)、語音轉錄(6)、背景工作(5)。
- **最近 24 小時警告以上日誌**：
  - 預設收合，點標題列展開（`expand_more`／`expand_less`）。展開後標題旁顯示「共 N 筆」，達 200 筆時加註「只列最新 200 筆」。
  - 表格欄位：時間、等級（WARN 警示色、ERROR／FATAL 錯誤色）、記錄器、訊息。最新在前，每頁 20 筆，左側箭頭展開看原文 `<pre>`。
  - 沒有資料時顯示「最近 24 小時沒有警告以上的日誌。」；讀不到日誌時顯示原因（例如目錄不存在）；讀檔警告逐條列出。
  - 頁尾說明附「日誌檢視」連結。

## 四、內部系統運作

1. **開頁**：`SystemHealthView.OnInitializedAsync` 依序執行：
   1. `AuthenticationStateHelper.Check` 驗證登入。
   2. `CheckIsAdmin()`：非管理者顯示「你沒有權限存取此頁面」並中止。0.4.120 起不再看權限鍵。
   3. 呼叫 `RefreshAsync`。開頭以 `isLoading` 早退，避免同一個 circuit 的 DbContext 被並行使用。
2. **產生報告**：`ISystemHealthService.GetReportAsync`（`SystemHealthService`）依序執行 18 項檢查。判斷都放在 `SystemHealthChecks` 的純函式，服務只蒐集輸入：
   - **日誌**：`INLogFilePathResolver` 算目錄與今日檔、`FileInfo.Length`、`LogLevelRuntimeState.EffectiveLevel／OverrideLevel` → `EvaluateLogging`。0.4.123 起不讀檔內容。
   - **登入金鑰**：`ExternalFileSystem.DataProtectionKeyPath`、`key-*.xml` 檔數、`IKeyManager.GetAllKeys()` 過濾未撤銷且未過期 → `EvaluateDataProtection`。
   - **快取服務**：`CacheSettings.GetProvider()`（解析失敗直接紅燈）→ `ProbeCacheAsync` 對 `ICacheService` 寫、讀、刪一筆哨兵，5 秒逾時。
   - **例外紀錄管線**：`LoggingPipelineMonitor.Snapshot()`、`ChannelReader<ExceptionLogEntry>.Count`、`ExceptionLogProvider.QueueCapacity／DroppedCount`、日誌目錄所在磁碟剩餘空間 → `EvaluateExceptionPipeline`。
   - **檔案系統、主機資源**：十個資料目錄，0.4.123 補上 `DataProtectionKeyPath`、`ExceptionPath`，1.0.29 補上 `AiCallLogPath`。
   - **近期錯誤**：近 24 小時 `AiUsageLog.Outcome` 分組計數，加上 `ExceptionLog` 中 `LastOccurredAt` 在 24 小時內的列數 → `EvaluateRecentErrors`。
   - **AI 用量上限**（1.0.29）：`IAiQuotaService.GetStatusAsync`（全系統本月用量，依每列匯率換算台幣）、`AiQuotaSettings.PerUserMonthlyTwd`（`IOptionsMonitor`），有每人上限時再取用量最高的 20 人 → `EvaluateAiQuota`。兩個上限都是 0 → 綠燈；全系統達上限 → 紅燈；全系統達八成，或有使用者達每人上限 → 黃燈。只讀帳本，不呼叫 AI。
   - 其他各項的輸入見機制文件 §4。
3. **計分**（`SystemHealthScoreCalculator`）：
   - Healthy 計滿分、Degraded 計半、Unhealthy 計 0。
   - `Score = round(earned/totalWeight*100)`。
4. **燈號門檻**：`Score >= 90` 綠、`>= 70` 黃、其餘紅。狀態文字用同一組門檻對應正常／警示／異常。
5. **警告日誌**：
   - 第一次展開時呼叫 `ILogQueryService.QueryAsync`，條件為現在往前 24 小時、`MinimumLevel = Warn`、`Take = 200`。
   - 結果反轉成最新在前。
   - 之後按「重新檢查」時，若區塊已展開會一併重查。
6. **探針**：
   - `/health/live` 對應 tag `live`（`self` 檢查恆 Healthy）。
   - `/health/ready` 對應 tag `ready`（`DatabaseHealthCheck` 檢查資料庫連線）。

## 五、權限與安全

- **只限管理者**（0.4.120 起）：
  - 頁面以 `CheckIsAdmin()` 守門，權限判定完成前不渲染任何內容。
  - 選單 id 34 列在 `SidebarMenuService.AdminOnlyMenuIds`。
  - 不在 `MenuPermissionMap` 與 `RolePermissionService` 權限目錄，無法勾給角色。
  - 未遮罩的日誌原文因此只有管理者看得到。
- **舊權限鍵退役**：0.4.93～0.4.119 的權限鍵「系統健康度」（含「系統健康度:*」動作鍵）由 `RbacBackfillService.RetireLegacyPermissionKeysAsync` 在每次啟動時清除：
  - 刪除 `Permission` 與 `RolePermissionMap` 的對應列。
  - 從 `RoleView.TabViewJson` 移除。
  - 處理是冪等的；常數為 `MagicObjectHelper.已退役權限鍵_系統健康度`。
- **佐證訊息遮蔽敏感值**：
  - JWT Issuer／Audience、AI 金鑰、Redis 連線字串只顯示「已設定／未設定」。
  - SigningKey 只顯示長度。
  - 登入金鑰只顯示檔數、有效數與到期日，不顯示金鑰內容。
- **不發 AI 連線**：健康檢查不連任何 AI 服務（兩個 Azure 端點都會計費），只檢查設定。快取檢查只寫一筆 1 分鐘後過期的哨兵，並立即刪除。
- **探針**：端點匿名可存取，只回傳存活／就緒狀態，不含詳細佐證。

## 六、錯誤與邊界

- 報告載入前顯示「正在檢查系統健康狀態…」；整份報告失敗時顯示錯誤框，頁面不崩潰。
- 各項目擲例外時降級為 Unhealthy，佐證寫例外型別，不影響其他項目：
  - 資料庫、背景工作、近期錯誤、登入金鑰、快取服務、AI 用量上限（讀不到帳本時失敗訊息為「無法讀取用量帳本。」）。
- 快取 5 秒內沒回應 → Unhealthy「快取在 5 秒內沒有回應」。
- 日誌目錄所在磁碟讀不到時，「例外紀錄管線」證據寫「未設定或無法讀取」，不扣分。
- 今日日誌檔尚未建立 → 日誌 Degraded（例如剛過午夜、還沒有任何紀錄）。
- 沒有有效登入金鑰 → Degraded。下一次登入時框架會自動建立，不算故障。
- 例外紀錄管線的計數是本次啟動以來的累計，重啟歸零。
- 沒啟用的功能（轉錄、匯率換算）顯示綠燈並說明「未啟用」，不扣分；沒設用量上限時「AI 用量上限」也是綠燈。

## 七、驗收與測試

- **`MeetingRecord.Tests/SystemHealthChecksTests.cs`**：
  - 權重合計恰好 100（18 項）。
  - 0.4.93 起的本系統功能六項：AI 設定、FFmpeg、PDF、匯率、卡住工作、近期錯誤門檻。
  - 0.4.123 新增：
    - 近期錯誤指向「系統例外紀錄」。
    - `EvaluateLogging`：BasePath 未設定、目錄不可寫、今日檔未建立、執行期覆寫標示。
    - `EvaluateDataProtection`：路徑未設定、不存在、不可寫為紅；無有效金鑰為黃；正常為綠，且只列數量。
    - `ProbeCacheAsync`：正常且刪掉哨兵為綠、讀回不符為黃、拋例外為紅、逾時為紅。
  - 1.0.29 新增：`EvaluateAiQuota` 三種燈號（不限制為綠、全系統達上限為紅、達八成或有人達每人上限為黃）。
  - 選單註冊。
- **`MeetingRecord.Tests/LoggingPipelineHealthTests.cs`**（0.4.123，移植）：
  - 監控計數累加。
  - 全部正常為綠；只有瀏覽器限流丟棄仍為綠。
  - 寫入失敗、NLog 內部錯誤、佇列八成、有丟棄、磁碟 < 1 GB 為黃。
  - 佇列滿、磁碟 < 200 MB 為紅。
  - 磁碟未知不扣分。
- **`MeetingRecord.Tests/SystemHealthTests.cs`**：計分與燈號門檻、狀態對應燈號。
- **`MeetingRecord.Tests/ApiIntegrationTests.cs`**：
  - `/health/ready`、`/health/live` 探針回應。
  - `SystemHealthService_ShouldResolveAndReportAll18ItemsWithoutLeakingSecrets`：從 DI 取得服務，報告 18 項（1.0.29 前為 17 項）、權重 100、快取綠燈，證據不含金鑰 XML。
  - 未登入開 `/system-health` 時，不出現「最近 24 小時警告以上日誌」與「例外紀錄管線」。
- **`MeetingRecord.Tests/MonitoringMenuRegistrationTests.cs`**、**`RbacBackfillServiceTests.cs`**、**`MenuIconTests.cs`**：選單、退役權限鍵、圖示。
- **0.4.123 實跑**：未登入不外洩、探針 200、日誌沒有新 ERROR。待使用者以管理者實測：
  - 17 項分兩組（1.0.29 起 18 項）。
  - 停機時移走金鑰檔，重啟後登入金鑰的證據會變化。
  - 快取指向不存在的 Redis → 快取紅燈。
  - 警告日誌區塊展開才載入。
  - 「AI 用量分析」沒有新增紀錄。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Monitoring/SystemHealthPage.razor`：薄頁。
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Monitoring/SystemHealthView.razor(.cs/.css)`：
  - `RefreshAsync`：重新檢查。
  - `ToggleWarnLogsAsync`／`LoadWarnLogsAsync`：警告日誌。
  - `WarnLogTake = 200`。
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthService.cs`：
  - `GetReportAsync`。
  - `CheckLogging`、`CheckDataProtection`、`CheckFileSystem`、`CheckHostResources`、`CheckCacheAsync`、`CheckExceptionPipeline`。
  - 本系統功能七項的 `Check*`（1.0.29 加 `CheckAiQuotaAsync`）。
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthChecks.cs`：
  - `SystemHealthWeights`。
  - 判斷純函式：`EvaluateLogging`、`EvaluateDataProtection`、`EvaluateExceptionPipeline`、`ProbeCacheAsync`、`EvaluateAiProvider`、`EvaluateTranscription`、`EvaluatePdfExport`、`EvaluateExchangeRate`、`EvaluateBackgroundJobs`、`EvaluateRecentErrors`、`EvaluateAiQuota`（1.0.29）。
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthScoreCalculator.cs`、`SystemHealthModels.cs`、`DatabaseHealthCheck.cs`
- `src/MeetingRecord/MeetingRecord.Web/Diagnostics/`：`LoggingPipelineMonitor`、`ExceptionLogProvider`、`NLogFilePathResolver`、`LogLevelRuntimeState`、`LogQueryService`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`：`AddConfiguredHealthChecks`（探針 tag `live`/`ready`）、`AddConfiguredDataProtection`、`AddConfiguredCache`。
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`AdminOnlyMenuIds`）、`MeetingRecord.Business/Services/Other/RbacBackfillService.cs`（`RetireLegacyPermissionKeysAsync`）
- 交叉連結：
  - [系統健康監控（機制）](../features/系統健康監控.md)
  - [首頁與導覽 PRD](首頁與導覽-prd.md)
  - 同組的 [日誌檢視 PRD](日誌檢視-prd.md)、[系統例外紀錄 PRD](系統例外紀錄-prd.md)、[日誌等級設定 PRD](日誌等級設定-prd.md)、[資料庫用量 PRD](資料庫用量-prd.md)
