# 會議紀錄產生流程 PRD

- 文件版本：1.0
- 文件狀態：規劃中
- 現行系統版本：0.4.26
- 首次實作版本：—（尚未實作）
- 最後核對日期：2026/08/19

> ⚠️ 本文件描述的是**尚未實作**的產品能力。0.4.26 只完成提示詞範本的維護（見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)）與 `LlmSettings` 強型別設定骨架；系統**不會呼叫任何 LLM 或語音轉錄 API**，也沒有音檔上傳與會議紀錄資料表。本文件不屬於 0.4.26 驗收範圍。

## 一、目標與範圍

目標是讓使用者上傳會議語音檔後，套用一組事先維護好的提示詞，自動產出可編修的會議紀錄草稿。

```
音檔上傳 ─► 轉錄（STT）─► 逐字稿 ─► 套用提示詞範本 ─► LLM ─► 會議紀錄草稿 ─► 人工編修／保存
```

非範圍（即使實作也不打算納入）：
- 不做即時（會議進行中）轉錄與逐字稿串流。
- 不做說話者聲紋辨識與身分綁定。
- 不做多語言翻譯輸出。

## 二、各階段現況界線

| 階段 | 現況 |
| --- | --- |
| 提示詞範本維護 | **已實作**（0.4.26，見 [會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)） |
| `LlmSettings` provider-aware 強型別設定 | **已實作骨架**（`LlmSettings` + `Providers` 字典 + Production 啟動檢查），**無任何呼叫端** |
| 音檔上傳與保存 | 未實作。0.4.24 已移除舊「會議記錄」的附件機制，現存附件只有專案附件（見 [檔案上傳機制](../features/檔案上傳機制.md)） |
| 音檔格式／時長／大小的允收政策 | 未實作。目前系統的上傳路徑沒有任何 MIME 或副檔名白名單 |
| 語音轉錄（STT） | 未實作，供應商未定 |
| 變數代入與 LLM 呼叫 | 未實作。專案目前完全沒有 `AddHttpClient` 註冊，連對外 HTTP 管線都要新建 |
| 會議紀錄 Entity 與頁面 | 未實作。目前資料庫沒有會議紀錄資料表 |
| 非同步作業、進度回報、失敗重試 | 未實作。專案目前沒有任何 `BackgroundService`、佇列或工作狀態表 |

## 三、未決議題

實作前必須先有結論的項目：

- **轉錄與生成的供應商選型**：生成端與轉錄端可以是不同廠商；`LlmSettings.Providers` 已預留多廠商結構，但轉錄是否共用同一組設定尚未決定。
- **長音檔處理**：切段策略、逐字稿的 token 上限、超長會議是否分段摘要後再合併。
- **逐字稿是否落庫**：落庫可支援重跑不同提示詞，但逐字稿是高敏感內容，須決定保存位置與保留期限。
- **成本與速率限制**：單次產出的成本上限、每日配額、失敗重試是否重複計費。
- **同步等待或背景作業**：Blazor Server 的 circuit 不應被長時間轉錄阻塞。若採背景作業，需要新增工作狀態實體、`IServiceScopeFactory` 取用 Scoped 服務、以及進度回報機制。
- **失敗語意**：部分成功（轉錄完成但生成失敗）如何呈現與續跑。
- **產出物歸屬**：會議紀錄是獨立實體，或掛在專案項目之下（0.4.24 前的舊 `Meeting` 實體是 `Project` 的子資料，`ProjectId` 為必填）。
- **合規界線**：會議逐字稿外送第三方 LLM 的資料處理、留存與跨境政策。

## 四、對現有設計的預期影響

實作時預期需要動到：

- `SystemSettings.ExternalFileSystem` 新增音檔存放路徑，並比照 `ProjectService.SavePhysicalFileAsync` 的年／月目錄 + GUID 檔名策略。
- 新增會議紀錄與音檔附件 Entity、對應 migration（SQLite）。
- 新增頁面權限鍵，並同步 `Menu.json`、`SidebarMenuService.MenuPermissionMap`、`MagicObjectHelper`、`RolePermissionService` 四處。
- 新增 `AddHttpClient` 註冊與 LLM／STT 用戶端，服務一律以 `IOptions<LlmSettings>` 取設定（**禁止**直接讀 `IConfiguration`）。
- 若採背景作業，需新增工作狀態實體與 `BackgroundService`；本專案目前是單一實例假設（SQLite 檔案資料庫 + 本機磁碟儲存），行程內 `Channel<T>` 較符合現有架構。
- 屆時須同步更新 [架構總覽](../architecture/架構總覽.md)、[資料模型與資料庫](../architecture/資料模型與資料庫.md)、[檔案上傳機制](../features/檔案上傳機制.md)、[認證授權與權限機制](../security/認證授權與權限機制.md) 與 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。

## 五、相關程式與文件

- `src/MeetingRecord/MeetingRecord.Models/Systems/LlmSettings.cs:22`（provider-aware 設定骨架）
- `src/MeetingRecord/MeetingRecord.Business/Helpers/PromptVariableHelper.cs:15`（產生時要代入的固定變數集）
- `src/MeetingRecord/MeetingRecord.Business/Services/DataAccess/ProjectService.cs:504`（可複用的實體檔案儲存樣板）
- `src/MeetingRecord/MeetingRecord.AccessDatas/Migrations/20260817031712_RemoveTaskAndMeeting.cs:1`（舊 `Meeting`／`MeetingFile` 資料表結構，可作為新實體的參考起點）
- 交叉連結：[會議紀錄提示詞 PRD](會議紀錄提示詞-prd.md)、[../features/檔案上傳機制.md](../features/檔案上傳機制.md)、[../operations/日誌與設定檔說明.md](../operations/日誌與設定檔說明.md)、[../architecture/開發慣例與限制速查.md](../architecture/開發慣例與限制速查.md)
