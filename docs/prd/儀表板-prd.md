# 儀表板 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.4.54
- 最後核對日期：2026/10/08

## 一、目標與範圍

提供整個系統的「營運概況」：用數字卡、圓餅圖、長條圖與指標列呈現專案、會議、逐字稿、待辦、提示詞範本、儲存空間與處理效能的**全公司彙總數字**。純唯讀，本頁不寫入任何資料，也沒有新增任何資料收集——所有數字都取自既有欄位與既有檔案。

- 範圍：6 張數字卡、4 張圓餅圖、2 張長條圖、6 塊指標列（兩兩並排）、手動重新整理。
- **刻意的例外**：本頁**不套團隊資料權限**（0.4.97 起），所有登入者看到同一份數字。也因此**只放數字與圖表，不放任何明細清單**（會議標題、待辦內容、逐字稿內容等）。這是全系統「資料權限一律經 `ProjectAccessService`」的唯一例外，見 [開發慣例與限制速查](../architecture/開發慣例與限制速查.md) 與 [認證授權與權限機制](../security/認證授權與權限機制.md)。
- 非範圍：
  - 時間趨勢圖（0.4.96 已移除，理由見四）。
  - 可點擊下鑽到明細、待處理清單（失敗的轉錄／生成、逾期待辦）——討論過、未做。
  - 依團隊或個人切換範圍、匯出報表、快取。
  - 使用者與帳號狀態、登入失敗統計——0.4.74 明確不做（需要額外權限閘門）。
  - AI 費用：另見 [AI 用量分析 PRD](AI用量分析-prd.md)（有權限控管的獨立頁）。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/dashboard`（`Components/Pages/Dashboards/DashboardPage.razor`，掛 `MainLayout`，內容在 `DashboardView`） |
| REST API | 無 |
| 選單路徑 | 核心功能區第一項：儀表板（`Menu.json` id=11，icon `space_dashboard`，`section: "核心功能"`） |
| 權限鍵 | **無**。id 11 列在 `SidebarMenuService.PublicMenuIds`，登入即可看；不在 `MenuPermissionMap`、`RolePermissionService` 權限矩陣，`MagicObjectHelper` 也沒有 `角色_儀表板` 常數（0.4.97 移除） |
| 主要使用者 | 所有已登入使用者（管理者與一般使用者看到相同數字） |

> id 用 11 而不是回收 1：1 是 0.4.45 移除的「首頁」用過的 id，回收會讓舊資料與文件難以對照。

## 三、畫面與欄位

頁首「營運概況」標題與「重新整理」鈕（載入中顯示讀取狀態、不接受重複觸發）。資料未取回前顯示「載入中…」。

### 數字卡（`StatCard` × 6）

| 卡片 | 主數字 | 副標 |
|------|--------|------|
| 專案總數 | 全部專案數 | 進行中 N |
| 會議紀錄 | 全部會議數 | 本月新增 N（依 `CreatedAt`） |
| 逐字稿完成 | 轉錄完成的會議數 | 處理中 N（等待中＋處理中；> 0 時黃色） |
| 待辦未完成 | 狀態不是「已完成」的待辦數 | 有逾期時「已逾期 N」（紅），否則「共 N 筆」 |
| 儲存空間 | 影音檔＋逐字稿＋專案附件合計 | 截至 yyyy/MM/dd |
| AI 問答次數 | 所有對話檔中使用者提問的累計則數 | 累計提問 |

### 圓餅圖（`PieChart` × 4）

專案狀態分布、逐字稿轉錄狀態、會議紀錄生成狀態、未完成待辦優先度（高／中／低，已完成不算；由高到低排）。0 筆的切片不畫。配色：轉錄／生成狀態的完成＝成功色、失敗＝危險色、等待／處理中＝警示色，其餘（未上傳、已取消等）中性色；專案狀態的已完成＝成功色、進行中＝警示色、其餘中性色；優先度高＝紅、中＝黃。

### 長條圖（`BarChart` × 2）

- **各專案的會議紀錄份數（前 8 名）**：依專案名稱分組，含「未歸屬」一列（警示色）。
- **提示詞範本使用次數**：依 `Meeting.DraftPromptTemplateName`（生成當下的範本名稱快照）分組。

### 指標列（兩兩並排）

| 區塊 | 四格 | 標色規則 |
|------|------|----------|
| 待辦概況 | 已逾期、7 天內到期（含今天與第 7 天）、進行中、由會議擷取（有 `MeetingId` 的占比） | 逾期 > 0 紅、7 天內到期 > 0 黃 |
| 專案概況 | 進行中平均完成度、已過結束日（已完成不算）、14 天內到期（已完成不算）、暫緩／等待 | 已過結束日 > 0 紅、14 天內到期 > 0 黃 |
| 提示詞範本 | 範本總數、啟用中、已停用、啟用但未使用 | 已停用 > 0 **黃**（停用是正常管理動作，不是故障）；啟用但未使用 > 0 黃並多一行提示 |
| 儲存空間 | 影音檔、逐字稿、專案附件、合計 | — |
| 會議時數 | 累計、本月、平均每場、有時長紀錄（N／M 場） | 下方固定灰字：「0.4.80 版之前轉錄的會議沒有時長，不計入時數」 |
| 處理效能 | 平均轉錄耗時、平均生成耗時、轉錄失敗率、未歸屬逐字稿 | 失敗率 > 20% 紅；未歸屬逐字稿 > 0 黃並多一行提示（可直接在會議紀錄產生，或到專案項目選取後產生） |

「提示詞範本」區塊刻意緊接在「提示詞範本使用次數」長條圖之後——「有幾個」與「用了哪些」要讀在一起才有意義。

### 「沒有資料」與「0」要分得出來

- 失敗率沒有任何成敗紀錄、平均耗時沒有完整區間、由會議擷取占比沒有任何待辦、進行中平均完成度沒有進行中專案、會議時數沒有任何紀錄 → 一律顯示「—」，不顯示 `0%`／`0 秒`（後者會被讀成「成功率 100%」「非常快」「都是手動建立」）。
- 圖表沒有資料時顯示「尚無資料」，不畫空白圖。

## 四、內部系統運作

- 資料流：`DashboardPage.razor` → `DashboardView` → `DashboardService.GetSummaryAsync()`（Scoped）→ `BackendDBContext` 與檔案系統。沒有 API、沒有快取。
- **一次投影、記憶體分組**：`Meeting`、`Project`、`Todo`、`PromptTemplate` 各撈一次需要的純量欄位（`MeetingFact`／`ProjectFact`／`TodoFact`／`PromptTemplateFact`，全部 `AsNoTracking()`），再於記憶體分組計算，而不是十幾條各自的 `GroupBy` 查詢。專案附件容量用 `ProjectFile.FileSize` 的 `SumAsync`。
- **平均耗時在記憶體算**：SQLite／EF Core 無法對 `TimeSpan` 做 `AVG`，取回 `TranscriptionStartedAt`／`CompletedAt` 與 `DraftStartedAt`／`CompletedAt` 有頭有尾的區間後由 `DashboardMetrics.AverageDuration` 平均。資料量成長後要改在 SQL 端用 `julianday` 相減。
- **轉錄失敗率**：`DashboardMetrics.CalculateFailureRate`，分母為「完成＋失敗」，兩者皆 0 回 null。
- **未歸屬逐字稿**：轉錄完成、`ProjectId` 為 null **且會議紀錄尚未生成完成**（0.4.73 收緊；不必先建專案就能生成之後，「未歸屬但已生成」是正常終態）。
- **會議時數**：`Meeting` 沒有時長欄位，改取 AI 用量帳本 `AiUsageLog` 中轉錄成功、有 `MeetingId` 與 `AudioSeconds` 的列。帳本一段音訊一列、重轉錄會再寫一整輪，所以每場只取 `OccurredAt >= TranscriptionStartedAt` 的列（最後一輪，`DashboardMetrics.SumLatestRunAudioSeconds`），且只算轉錄完成的會議。0.4.80 之前轉錄的會議沒有帳本紀錄、也沒有回填。
- **儲存空間**：影音檔＝`Meeting.MediaFileSize` 加總；逐字稿＝`DirectorySizeCalculator.Measure(SystemSettings.ExternalFileSystem.MeetingTranscriptPath)` 實際掃目錄（`Meeting` 沒有逐字稿容量欄位）；專案附件＝`ProjectFile.FileSize` 加總；合計三項相加。數字卡與細分列共用同一個 `StorageSummary`，保證同源。顯示格式統一用 `FileSizeFormatter.Describe`。
- **AI 問答次數**：`AiChatStore.CountQuestions()` 遞迴掃 AI 對話根目錄下所有 `*.jsonl`，數使用者發言（0.4.60 起對話存在檔案系統而非資料庫）。
- **啟用但未使用**：`DashboardMetrics.CountUnusedEnabledTemplates` 以**名稱**比對 `Meeting.DraftPromptTemplateName`（不分大小寫、先 Trim）。
- **圖表是手工 SVG／CSS**（`Components/Commons/Charts/`：`StatCard`、`PieChart`、`BarChart`、`LineChart`，集中配色 `ChartPalette`），沒有引任何圖表套件、零 JS。圓餅與折線是 SVG，懸停提示用 SVG 原生 `<title>`；長條圖是 HTML／CSS 長條，標籤以 `title` 屬性顯示完整名稱。SVG 座標與 CSS 寬度百分比一律以 `CultureInfo.InvariantCulture` 格式化。
- 日期基準：`DateTime.Today`（伺服器當地時間），本月＝當月 1 日起。

### 設計決策

| 議題 | 結論 |
|------|------|
| 資料範圍 | **0.4.97 起全公司同一份數字**，`DashboardService` 不注入任何存取範圍，也不呼叫 `ProjectAccessService`。0.4.54～0.4.96 曾沿用各頁規則（會議、範本套團隊過濾），為了「儀表板與點進去看到的筆數對得上」；0.4.97 使用者要求儀表板給所有人看、不設權限後改為全公司彙總。0.4.99 的專案權控與 0.4.102 的主責＋協作團隊也刻意不套到這裡 |
| 為何不放明細 | 不套權限的直接後果：任何清單都會把別的團隊看不到的內容露出來。新增區塊只能是數字或圖表 |
| 登入即可看怎麼做 | 宣告在 `SidebarMenuService.PublicMenuIds`。⚠️ **單純不放進 `MenuPermissionMap` 沒有用**：沒有對應的項目會退回用 `Name`（「儀表板」）當權限鍵，角色沒有同名權限就照樣被藏起來。頁面本身只做登入檢查（`AuthenticationStateHelper.Check`），不呼叫 `CheckAccessPage`。資料庫裡舊的「儀表板」Permission 列成為孤兒，無害、未清理 |
| 不做快取 | 儀表板本來就該顯示當下數字；Business 層也注入不到 Web 層的 `ICacheService`。代價是每次重新整理都會掃一次逐字稿目錄與全部對話檔，資料成長後這裡會是痛點 |
| 移除趨勢圖（0.4.96） | 上傳→轉錄→生成通常幾分鐘內完成，「新增會議」與「完成紀錄」兩條線幾乎重疊；量小（每天 0～3 場）畫出來是貼地鋸齒；資訊已由「本月新增」與狀態圓餅涵蓋；切天數會重載整份統計。`TrendPoint`、`DashboardMetrics.BuildDayBuckets`／`BuildDayLabels`、`LineChart.razor` **刻意保留**，AI 用量分析頁的累計曲線還在用 |
| 「啟用但未使用」用名稱比對 | `DraftPromptTemplateName` 是生成當下的**名稱快照**（讓範本刪除後仍看得出當初用了什麼）。必然結果：改過名的範本會被算成未使用、已刪除的範本仍出現在使用次數長條圖。畫面提示已寫明，`DashboardMetricsTests.CountUnusedEnabledTemplates_RenamedTemplate_ShouldCountAsUnused` 把它釘成契約 |
| 逐字稿容量掃目錄 | 量的是磁碟現況，會把資料庫已刪、檔案還在的孤兒檔算進去——刻意的，「儲存空間」問的是磁碟被吃掉多少 |
| 「儲存空間」卡片與細分同源 | 0.4.74 把原「音檔總容量」卡改名並改成三項合計，否則卡片與細分合計會兩個數字打架 |

## 五、權限與安全

- 頁面與選單：登入即可看（`PublicMenuIds`），未登入或身分無效由 `AuthenticationStateHelper.Check` 導向 `/Auths/Logout`（再回登入頁）。沒有動作權限，因為本頁沒有任何寫入動作。
- **資料權限例外**：不經 `ProjectAccessService`，所有人看到全公司數字，因此會與一般使用者在會議、專案、待辦頁看到的筆數不同——刻意如此（使用說明 FAQ 也有說明）。
- 露出的名稱只有兩類：專案名稱（各專案會議份數長條圖）與提示詞範本名稱（使用次數長條圖）。0.4.97 使用者確認保留；⚠️ 0.4.115 上線前審查重新列為「未修，請判斷」：**看不到某專案的人也會看到該專案的名稱與會議數**，專案名稱本身可能就是機密，待決定（見 [上線前全面審查與修正](../changelog/2026-10-02-上線前全面審查與修正.md) §四）。
- 日後新增任何區塊都不得帶出會議標題、待辦內容、逐字稿或對話內容。

## 六、錯誤與邊界

- **載入失敗**：`GetSummaryAsync` 拋例外時只寫 Error log、不顯示錯誤訊息。首次載入就失敗時 `summary` 維持 null，畫面會一直停在「載入中…」；已有資料後重新整理失敗，則保留上一份數字。可再按「重新整理」重試。
- 逐字稿目錄路徑空白或不存在 → 回 0（全新環境不是錯誤）；個別檔案讀不到（正在寫入、權限不足）→ 略過不拋。
- AI 對話根目錄不存在 → 問答次數 0；個別對話檔讀取 `IOException` → 寫 Warning log 後略過。
- 圓餅只有單一切片時以兩段半圓接成整圓（SVG arc 起終點重合畫不出東西）；長條圖最大值、圓餅總和、失敗率分母皆先擋 0。
- 失敗率、平均耗時、占比、平均完成度、會議時數沒有資料時顯示「—」。
- 「啟用但未使用」對非管理者的偏高問題已不存在（0.4.97 起不再套範本的團隊過濾）。
- 版面：數字卡 `minmax(190px, 1fr)`、圓餅 `minmax(320px, 1fr)`、寬格（長條與指標列）`minmax(420px, 1fr)` 自動換行；並排指標列把格子下限從 160px 收到 120px，避免四格排成 3＋1。

## 七、驗收與測試

- `src/MeetingRecord/MeetingRecord.Tests/DashboardServiceTests.cs`（服務層，SQLite in-memory＋暫存逐字稿目錄）：
  - 提示詞範本：啟用／停用拆分、啟用＋停用＝總數、以真實會議比對未使用、無範本回 0。
  - 儲存空間：影音檔加總、專案附件加總、掃逐字稿目錄、目錄不存在不拋例外、合計＝三項之和、數字卡顯示合計而非只有影音檔。
  - 待辦：逾期與 7 天內到期的邊界、由會議擷取占比、無待辦時占比為 null、未完成優先度排除已完成與空切片。
  - 專案：已過結束日／14 天內到期／暫緩等待、平均完成度只算進行中。
  - 會議時數：重轉錄只算最後一輪、失敗紀錄與無時長的會議不計、無紀錄顯示「—」。
- `src/MeetingRecord/MeetingRecord.Tests/DashboardMetricsTests.cs`（純函式）：日桶與日標籤（AI 用量分析共用）、`SumLatestRunAudioSeconds`、`ToPercentages`、`BuildArcPath`（四分之一圓、整圓兩段弧、大弧旗標）、`DescribeDuration`、`CalculateFailureRate`、`AverageDuration`、`FileSizeFormatter`、`CountUnusedEnabledTemplates`（含改名契約）、`DirectorySizeCalculator`。
- `src/MeetingRecord/MeetingRecord.Tests/AiChatStoreTests.cs`「儀表板計數」區與 `AiChatConversationTests`：問答次數要遞迴到 `meeting/<id>/` 子目錄、根目錄不存在不拋例外。
- 人工驗收：[上架前人工測試清單](../guides/上架前人工測試清單.md) 12-1（一般使用者選單含儀表板）、14-1（每個帳號開儀表板數字一致）。
- ⚠️ 「不套團隊過濾」原有的守門測試 `PromptTemplatesAndMeetings_ShouldCountAllTeams` 已隨 0.4.103 移除舊團隊標籤一併刪除；目前**沒有測試**守住「儀表板不得套 `ProjectAccessService`」這條例外，`DashboardServiceTests` 類別註解仍寫著有。

## 八、相關程式與文件

### 版本沿革

- 0.4.54：新增 `/dashboard`（6 卡、3 圓餅、2 長條、近 12 個月雙線趨勢、處理效能），需授予「儀表板」權限。
- 0.4.62～0.4.63：趨勢圖改為可選 7／30／90 天的日視角。
- 0.4.73：「未歸屬逐字稿」收緊為未歸屬且尚未生成。
- 0.4.74：加入提示詞範本統計與儲存空間細分；「音檔總容量」卡改為「儲存空間」三項合計。
- 0.4.96：移除趨勢折線圖與天數下拉。
- 0.4.97：登入即可看（`PublicMenuIds`）、全公司同一份數字；新增未完成待辦優先度圓餅、待辦概況、專案概況、會議時數。

### 程式

- `src/MeetingRecord/MeetingRecord.Web/Components/Pages/Dashboards/DashboardPage.razor`
- `src/MeetingRecord/MeetingRecord.Web/Components/Views/Dashboards/DashboardView.razor`、`DashboardView.razor.cs`（`OnInitializedAsync` 只做登入檢查、`ReloadAsync`、`FormatPercent`）、`DashboardView.razor.css`
- `src/MeetingRecord/MeetingRecord.Business/Services/Dashboard/DashboardService.cs`（`GetSummaryAsync`、`BuildCards`、`BuildTodoOverview`、`BuildProjectOverview`、`BuildMeetingHours`、`BuildPerformance`、`BuildPromptTemplates`）
- `src/MeetingRecord/MeetingRecord.Business/Services/Dashboard/DashboardMetrics.cs`、`DashboardModels.cs`（`DashboardSummary`、`StatCardItem`、`ChartSlice`、`StorageSummary` 等）
- `src/MeetingRecord/MeetingRecord.Web/Components/Commons/Charts/`（`StatCard`、`PieChart`、`BarChart`、`LineChart`、`ChartPalette`）
- `src/MeetingRecord/MeetingRecord.Share/Helpers/FileSizeFormatter.cs`、`DirectorySizeCalculator.cs`
- `src/MeetingRecord/MeetingRecord.Business/Services/AiChat/AiChatStore.cs`（`CountQuestions`）
- `src/MeetingRecord/MeetingRecord.Web/Components/Layout/SidebarMenuService.cs`（`PublicMenuIds`、`FilterAuthorizedMenuItems`）、`src/MeetingRecord/MeetingRecord.Web/Datas/Menu.json`（id 11）
- `src/MeetingRecord/MeetingRecord.Web/Extensions/ServiceCollectionExtensions.cs`（`AddScoped<DashboardService>`）
- `src/MeetingRecord/MeetingRecord.Tests/DashboardServiceTests.cs`、`DashboardMetricsTests.cs`

### 文件

- Changelog：[儀表板](../changelog/2026-09-08-儀表板.md)、[操作欄只留圖示與趨勢範圍](../changelog/2026-09-10-操作欄只留圖示與趨勢範圍.md)、[儀表板範本與儲存空間](../changelog/2026-09-16-儀表板範本與儲存空間.md)、[儀表板移除趨勢圖](../changelog/2026-09-23-儀表板移除趨勢圖.md)、[儀表板全員可看與概況指標](../changelog/2026-09-23-儀表板全員可看與概況指標.md)
- 交叉連結：[首頁與導覽 PRD](首頁與導覽-prd.md)、[AI 用量分析 PRD](AI用量分析-prd.md)、[紀錄分類與團隊權控 PRD](紀錄分類與團隊權控-prd.md)、[系統功能總覽](../architecture/系統功能總覽.md)、[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)

> 返回 [PRD 主控台](README.md)
