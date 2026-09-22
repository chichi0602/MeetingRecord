# 系統健康監控 PRD

- 文件版本：2.0
- 文件狀態：已實作
- 現行系統版本：0.4.93
- 首次實作版本：既有腳手架核心功能（0.4.93 擴充為「系統健康度」）
- 最後核對日期：2026/09/22

## 一、目標與範圍

提供維運人員一個巡檢頁面「系統健康度」（`/system-health`），以紅黃綠燈號與健康百分比快速判斷基礎設施與本系統專屬相依是否正常，並附最後 100 筆日誌；另提供部署平台使用的機器可讀探針端點。

- 範圍：`/system-health` 巡檢頁、14 項健康檢查（基礎設施 8 項＋本系統功能 6 項）、加權計分與燈號、重新檢查、日誌尾端顯示、`/health/live` 與 `/health/ready` 探針。
- 非範圍：告警通知／歷史趨勢、外部監控整合、自動修復、**任何會計費的 AI 連線測試**。機制細節不重寫，見 `docs/features/系統健康監控.md`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-health` | 系統管理 → 系統健康度（id 34） | 頁面權限 `系統健康度`（預設僅管理員）；頁尾日誌僅管理員 | 系統管理員／維運 |
| `/health/live` | 非選單（探針） | 無（匿名） | 部署平台存活探針 |
| `/health/ready` | 非選單（探針） | 無（匿名） | 部署平台就緒探針 |

## 三、畫面與欄位

- 摘要區：整體燈號（綠／黃／紅）、健康百分比（`Score%`）、狀態文字（正常／警示／異常）、最後檢查時間、「重新檢查」按鈕（執行中顯示載入狀態、重複點擊忽略）。
- 檢查項目分兩區：「基礎設施」「本系統功能」。每張卡片：名稱、類別、權重、狀態文字、燈號、佐證（Evidence）；異常時另顯示失敗訊息（FailureMessage）。
- 14 項檢查與權重（總計 100）：
  - 基礎設施 68：網站/應用程式(5)、API(5)、資料庫(20)、日誌(8)、身分驗證(10)、檔案系統(10)、主機資源(5)、安全設定(5)。
  - 本系統功能 32：AI 供應商設定(10)、語音轉錄 FFmpeg(6)、背景工作(6)、近期錯誤(4)、PDF 匯出(3)、匯率(3)。
- 日誌區（僅管理員）：標題「最後 100 筆日誌紀錄」、來源檔路徑；無資料顯示「沒有可顯示的日誌紀錄。」，否則以 `<pre>` 逐行呈現。
- 證據欄的長檔案路徑可任意斷行，窄螢幕（390px）與一般寬度都不產生整頁橫向捲動。

## 四、內部系統運作

1. `SystemHealthPage.OnInitializedAsync`：`AuthenticationStateHelper.Check` 驗證登入 → `CheckAccessPage(角色_系統健康度)`（管理員短路通過），不通過顯示「你沒有權限存取此頁面」並中止 → 記下 `CheckIsAdmin()` 決定是否顯示日誌 → 呼叫服務。
2. `ISystemHealthService.GetReportAsync`（`SystemHealthService`）先讀一次日誌尾端（「日誌」「近期錯誤」與頁尾共用），再依序執行 14 項檢查：
   - 基礎設施八項規則見機制文件 §4.1。0.4.93 修正：檔案系統檢查七個資料目錄；主機資源量資料目錄所在磁碟（`Path.GetPathRoot` 去重，逐顆 `DriveInfo`）。
   - 本系統功能六項的判斷在 `SystemHealthChecks` 純函式，服務只蒐集輸入：`LlmSettings`、`MediaSettings.FfmpegPath`＋`FfmpegPathResolver.Exists`、`ExportSettings.BrowserPath`＋`BrowserPathResolver.Resolve`、`ExchangeRateCache.Current／LastFailureAt`、兩個進度通知器的 `GetSnapshot()` 與 `Meeting` 的轉錄／草稿狀態、近 24 小時 `AiUsageLog.Outcome` 分組計數與今日日誌 ERROR 數。
3. 計分（`SystemHealthScoreCalculator`）：Healthy 計滿分、Degraded 計半、Unhealthy 計 0；`Score = round(earned/totalWeight*100)`。
4. 燈號門檻：`Score >= 90` 綠、`>= 70` 黃、其餘紅；狀態文字同門檻映射正常／警示／異常。
5. 探針：`/health/live` 對應 tag `live`（`self` 檢查恆 Healthy）；`/health/ready` 對應 tag `ready`（`DatabaseHealthCheck` 檢查資料庫連線）。

## 五、權限與安全

- 頁面採宣告式權限（`Menu.json` id 34 ＋ `MenuPermissionMap` ＋ `RolePermissionService` 單元素群組），預設只有管理員；勾給其他角色時，頁尾未遮罩日誌仍只有管理員看得到。
- 佐證訊息對敏感值採遮蔽：JWT Issuer／Audience、AI 金鑰只顯示「已設定／未設定」，SigningKey 只顯示長度。
- 健康檢查**不發任何 AI 連線**（兩個 Azure 端點都會計費），只檢查設定。
- 探針端點匿名可存取，僅回傳存活／就緒狀態，不含詳細佐證。

## 六、錯誤與邊界

- 報告載入前顯示「正在讀取系統健康狀態...」。
- 資料庫、背景工作、近期錯誤三項查詢擲例外時降級為 Unhealthy 並記錄例外型別，不使頁面崩潰。
- 日誌檔不存在時回傳 Degraded 的空尾端，頁面顯示無日誌提示。
- 沒啟用的功能（轉錄、匯率換算）顯示綠燈並說明「未啟用」，不扣分。
- 任一子項降級／異常僅影響其權重計分與整體燈號，其餘項目仍照常呈現。

## 七、驗收與測試

- `MeetingRecord.Tests/SystemHealthChecksTests.cs`（0.4.93）：權重合計恰好 100；AI 設定（未設定／開發金鑰／範例 Endpoint／缺單價／只有輸入單價／全齊且不洩漏金鑰）；FFmpeg；PDF；匯率（停用、無匯率、非即時來源、逾期、緩衝內）；`ExchangeRateCache.MarkFailed` 不動目前匯率；卡住工作（佇列沒有、未超時、超時、不同種類佇列不互相代表）；近期錯誤門檻；`IsErrorLine` 只看等級欄；選單與權限註冊。
- `MeetingRecord.Tests/SystemHealthTests.cs`：計分與燈號門檻、狀態→燈號映射、日誌尾端讀取與缺檔降級。
- `MeetingRecord.Tests/ApiIntegrationTests.cs`：`/health/ready`、`/health/live` 探針回應。
- `MeetingRecord.Tests/MenuIconTests.cs`：`monitor_heart` 列入 icon 白名單。
- 0.4.93 實跑（沙箱資料副本）：14 項兩區；把生成模型改成沒有單價的名稱 → AI 設定黃燈；`FfmpegPath` 指到不存在的檔案 → 語音轉錄紅燈，總分 87% 黃燈；把一場會議改成「處理中」→ 背景工作黃燈並指出是哪一場；重新檢查會更新時間；探針仍回 200。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/SystemHealthPage.razor:113`（頁面權限守門）、`:120`（日誌僅管理員）、`:124`（重新檢查）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthService.cs:86`（`GetReportAsync`）、`:254`（檔案系統）、`:283`（主機資源）、`:352`～`:437`（本系統功能六項）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthChecks.cs:19`（`SystemHealthWeights`）、`:49`（判斷純函式）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthScoreCalculator.cs:1`（計分與燈號門檻）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthModels.cs:1`（報告、項目、分組）
- `src/MeetingRecord/MeetingRecord.Web/Health/HealthLogReader.cs:85`（`IsErrorLine`）、`DatabaseHealthCheck.cs:1`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs:296`（`AddConfiguredHealthChecks`，探針 tag `live`/`ready`）
- `src/MeetingRecord/MeetingRecord.Web/Program.cs:492`（`MapHealthChecks` `/health/live`、`/health/ready`）
- 交叉連結：[系統健康監控（機制）](../features/系統健康監控.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
