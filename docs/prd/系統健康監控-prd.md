# 系統健康監控 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/07/14

## 一、目標與範圍

提供維運人員一個人工巡檢頁面（`/system-health`），以紅黃綠燈號與健康百分比快速判斷網站、API、資料庫、日誌、身分驗證、檔案系統、主機資源與安全設定是否正常，並附最後 100 筆日誌；另提供部署平台使用的機器可讀探針端點。

- 範圍：`/system-health` 巡檢頁、8 項健康檢查、加權計分與燈號、日誌尾端顯示、`/health/live` 與 `/health/ready` 探針。
- 非範圍：告警通知／歷史趨勢、外部監控整合、自動修復。機制細節不重寫，見 `docs/features/系統健康監控.md`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/system-health` | 非選單（需知網址直接進入） | 已登入且為管理員（`IsAdmin`） | 系統管理員／維運 |
| `/health/live` | 非選單（探針） | 無（匿名） | 部署平台存活探針 |
| `/health/ready` | 非選單（探針） | 無（匿名） | 部署平台就緒探針 |

## 三、畫面與欄位

- 摘要區：整體燈號（綠／黃／紅）、健康百分比（`Score%`）、狀態文字（正常／警示／異常）、最後檢查時間。
- 檢查項目卡片（逐項）：名稱、類別、權重、狀態文字、燈號、佐證（Evidence）；異常時另顯示失敗訊息（FailureMessage）。
- 8 項檢查與權重（總計 100）：網站/應用程式(10)、API(10)、資料庫(25)、日誌(15)、身分驗證(15)、檔案系統(10)、主機資源(5)、安全設定(10)。
- 日誌區：標題「最後 100 筆日誌紀錄」、來源檔路徑；無資料顯示「沒有可顯示的日誌紀錄。」，否則以 `<pre>` 逐行呈現。

## 四、內部系統運作

1. `SystemHealthPage.OnInitializedAsync`：先 `AuthenticationStateHelper.Check` 驗證登入，再 `CheckIsAdmin`；非管理員設定 `roleMessage` 並中止，不呼叫服務。
2. `ISystemHealthService.GetReportAsync`（`SystemHealthService`）依序執行 8 項檢查，各回傳 `SystemHealthItem`（狀態 Healthy/Degraded/Unhealthy）：
   - 應用程式：環境、`SystemVersion`（未設定→Degraded）、啟動時間與運作時長。
   - API：Controller action 數量（0→Unhealthy）、Swagger 依環境／設定。
   - 資料庫：`Database.CanConnectAsync`（false→Unhealthy）、待套用 migration 數（>0→Degraded）。
   - 日誌：目錄可寫入與今日檔案讀取筆數（不可寫→Unhealthy；可寫但無內容→Degraded）。
   - 身分驗證：Cookie 與 JWT scheme 是否註冊、JWT 設定完整度、Production 是否仍用開發用 key。
   - 檔案系統：資料庫／下載／上傳／各附件目錄存在且可寫入。
   - 主機資源：Working set 與磁碟可用空間（<1GB→Degraded）。
   - 安全設定：Production 是否開啟 Swagger 或回傳例外細節（風險→Degraded）。
3. 計分（`SystemHealthScoreCalculator`）：以權重加權，Healthy 計滿分、Degraded 計半、Unhealthy 計 0；`Score = round(earned/totalWeight*100)`。
4. 燈號門檻：`Score >= 90` 綠、`>= 70` 黃、其餘紅；狀態文字同門檻映射正常／警示／異常。
5. 日誌：`IHealthLogReader.ReadLatestLines(100)` 讀取當日日誌尾端 100 行。
6. 探針：`/health/live` 對應 tag `live`（`self` 檢查恆 Healthy）；`/health/ready` 對應 tag `ready`（`DatabaseHealthCheck` 檢查資料庫連線）。

## 五、權限與安全

- 巡檢頁為管理員專屬：非管理員顯示「您沒有權限檢視系統健康監控。」並記錄警告，不揭露任何檢查內容。
- 頁面採直接進入（不列於側邊選單），需登入且 `IsAdmin` 才可讀取報告；管理員豁免一如全站 RBAC 慣例。
- 佐證訊息對敏感值採遮蔽：JWT Issuer／Audience 僅顯示「已設定／未設定」，SigningKey 僅顯示長度。
- 探針端點匿名可存取，僅回傳存活／就緒狀態，不含詳細佐證。

## 六、錯誤與邊界

- 報告載入前顯示「正在讀取系統健康狀態...」。
- 資料庫檢查擲例外時降級為 Unhealthy 並記錄例外型別，不使頁面崩潰。
- 日誌檔不存在時回傳 Degraded 的空尾端（`HealthLogTail.Empty`），頁面顯示無日誌提示。
- 任一子項降級／異常僅影響其權重計分與整體燈號，其餘項目仍照常呈現。

## 七、驗收與測試

- `MeetingRecord.Tests/SystemHealthTests.cs`：
  - `CalculateScore_AllHealthy_ShouldReturnGreen100`、`CalculateScore_DegradedRange_ShouldReturnYellow`、`CalculateScore_UnhealthyRange_ShouldReturnRed`（計分與燈號門檻）。
  - `GetLight_ItemStatus_ShouldMapTrafficLight`（狀態→燈號映射）。
  - `HealthLogReader_ReadLatestLines_ShouldReturnLast100Lines`、`HealthLogReader_MissingFile_ShouldReturnDegraded`（日誌尾端讀取與缺檔降級）。
- `MeetingRecord.Tests/ApiIntegrationTests.cs`：`/health/ready`、`/health/live` 探針回應。

## 八、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/SystemHealthPage.razor:1`（含管理員守門 `:99`、燈號/狀態文字 `:109`）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthService.cs:60`（`GetReportAsync` 與 8 項檢查）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthScoreCalculator.cs:1`（計分與燈號門檻）
- `src/MeetingRecord/MeetingRecord.Web/Health/SystemHealthModels.cs:1`（報告與項目模型）
- `src/MeetingRecord/MeetingRecord.Web/Health/HealthLogReader.cs:1`、`DatabaseHealthCheck.cs:1`
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs:208`（`AddConfiguredHealthChecks`，探針 tag `live`/`ready`）
- `src/MeetingRecord/MeetingRecord.Web/Program.cs:398`（`MapHealthChecks` `/health/live`、`/health/ready`）
- 交叉連結：[系統健康監控（機制）](../features/系統健康監控.md)、[首頁與導覽 PRD](首頁與導覽-prd.md)
