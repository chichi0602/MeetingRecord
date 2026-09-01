# 會議記錄流程 Wireframe 設計規格

- 文件版本：1.0
- 文件狀態：設計定案（尚未實作）
- 現行系統版本：0.4.30
- 首次實作版本：未實作
- 最後核對日期：2026/08/31

本規格定義「語音轉文字 → 專案項目 → AI 整理會議記錄 → TodoList」這條主流程的畫面與互動。
產出的高保真 wireframe 為外部 Artifact，本文為對應的決策紀錄。實作結果請另立 changelog。

---

## 一、背景與現況落差

使用者希望在動工前，先把三個功能的呈現方式視覺化確認。盤點後的落差如下：

| 功能 | 現況 |
|------|------|
| 語音轉文字上傳 | **已實作**（0.4.27）。`/meetings` 已有上傳、FFmpeg 切 15 分鐘分段、Azure OpenAI 轉錄、進度條、狀態 pill、逐字稿預覽、重新轉錄 |
| 專案項目 | **已實作**。`/projects` 完整 CRUD 與附件 |
| TodoList | **不存在**。0.4.24 曾有結構相似的 `MyTask` 被移除（migration `20260817031712_RemoveTaskAndMeeting`） |
| AI 整理逐字稿 → 會議記錄 | **未實作**。僅有 `PromptTemplate` CRUD 與 `LlmSettings` 骨架；全 repo 無 `chat/completions` 或文字生成用戶端。`LlmSettings.DefaultProvider` 與 `Model` 目前沒有任何消費者 |

因此本規格的新工程集中在兩處：**AI 整理會議記錄**與**TodoList**；語音轉文字與專案項目是在既有頁面上補功能。

---

## 二、需求決策

以下為與使用者逐項確認後的定案。

| 項目 | 決策 |
|------|------|
| Sidebar 結構 | 三個核心功能（語音轉文字／專案項目／TodoList）獨立成一個置頂區塊，下方保留現有「首頁／系統管理／資料定義／登出」 |
| 專案選擇方式 | 頁面內的專案選擇器，**不**在 sidebar 展開專案樹 |
| 逐字稿與專案的關聯 | 一對多：一份逐字稿只能歸屬一個專案，一個專案可有多份逐字稿 |
| 逐字稿下拉內容 | 列出全部逐字稿；已被**其他**專案取用的灰掉不可選，並標示「已屬：專案名」 |
| 重複生成會議記錄 | 允許重新生成（例如更換提示詞），**覆蓋**該逐字稿既有的會議記錄 |
| 語音轉文字頁功能 | 拖拉上傳、分段進度（第 N/M 段）、逐字稿預覽、複製全文、下載 .txt |
| AI 待辦寫入方式 | 先在確認彈窗列成可勾選清單（可改標題／負責人／截止日／優先度），確認後才寫入 |
| TodoList 版面 | 單一 Ant Table + 專案過濾下拉，與其他管理頁一致，不做看板 |
| Icon | 一律 Material Icons Outlined，**不使用 emoji** |

### 未採用的選項與理由

- **Sidebar 只留三個按鈕**：會讓系統管理與資料定義無處可去，與現有 RBAC 選單機制衝突。
- **看板式 TodoList**：與現有 Ant Design Table 風格落差過大，且拖拉排序不在本次需求內。
- **說話者分離（誰講的）**：Azure OpenAI 的 whisper 轉錄端點不支援 diarization，要做須更換供應商或另接服務，本次不納入。

---

## 三、畫面規格

### 3.1 語音轉文字

- **拖拉上傳區**：虛線框，`cloud_upload` icon，標示「支援 mp3 / wav / m4a / mp4 / mov / mkv / webm 等 16 種格式，單檔上限 1 GB」（沿用 `MeetingMediaPolicy` 的既有政策）。
- **轉錄清單**：檔案名稱（含檔案大小）／時長／上傳時間／轉錄狀態／歸屬專案／操作。
- **轉錄狀態**呈現 `TranscriptionStatus` 五態，以 Ant 語意色 pill 表示；處理中額外顯示進度條與「3 / 8 段」。
- **歸屬專案欄**顯示專案名稱或「— 未歸屬」，並在分頁列摘要「共 12 筆 · 5 份尚未歸屬專案」。
- **逐字稿預覽面板**：顯示帶時間碼的全文，工具列提供「複製全文」「下載 .txt」「全文搜尋」。

### 3.2 專案項目

- **專案選擇器**：頁面頂端的 Ant Select，右側附新增／編輯專案的 icon 按鈕。
- **專案摘要列**：負責人／期程／狀態／完成度／會議記錄份數。
- **AI 整理區塊**（新增）：
  - 「選擇逐字稿」下拉，下拉標頭顯示「共 12 份逐字稿 · 5 份未歸屬 · 已被其他專案取用的無法選取」。
  - 項目分三類：未歸屬（可選，標「未歸屬」）、屬於本專案（可選，標「本專案」並註明「已生成會議記錄，重新生成將覆蓋」）、已屬其他專案（灰掉，標「已屬：專案名」）。
  - 「提示詞」下拉，來源為 `PromptTemplate` 中啟用的項目。
  - 主要按鈕「AI 轉會議記錄」，下方常駐提示說明一對一歸屬與覆蓋行為。
- **歷史會議記錄清單**：標題（含決議／待辦數）／來源逐字稿／使用提示詞／生成時間／待辦寫入狀態／操作（檢視、編輯、刪除）。

### 3.3 AI 待辦確認彈窗

- 標題「AI 從逐字稿抽出 N 項待辦」，副標註明來源逐字稿與「會議記錄已產生」。
- 每列：勾選框／待辦標題（附逐字稿時間碼與原句片段）／負責人／截止日／優先度，後三者可直接編修。
- 未指派負責人或內容不像具體工作項目者，預設**不勾選**，讓使用者主動納入。
- 底部顯示「已選 N 項，M 項不寫入」，按鈕為「取消」與「寫入 TodoList（N 項）」。

### 3.4 TodoList

- **工具列**：左側新增／重新整理；右側專案過濾、狀態過濾、搜尋框、搜尋、清除。
- **表格欄位**：完成勾選／待辦事項／所屬專案／來源會議記錄／負責人／截止日／優先度／狀態／操作。
- 逾期項目的截止日以紅色粗體顯示並附「逾期 N 天」；已完成項目標題加刪除線。
- 「來源會議記錄」可回溯到產生此待辦的那一份會議記錄；手動新增的顯示「— 手動新增」。
- 分頁列摘要「共 18 筆 · 未完成 11 筆 · 逾期 1 筆」。

---

## 四、視覺規格

畫風完全沿用現有系統，色值取自實際 CSS，不另立設計語言。

| 區域 | 規格 | 出處 |
|------|------|------|
| 頁面底色 | `linear-gradient(180deg,#f3f6ff,#eef2ff 52%,#f8faff)` | `MainLayout.razor.css` `.page` |
| Sidebar | 寬 300px、`linear-gradient(180deg,#f5f6f8,#eef1f4 50%,#e7ebf0)`、右框 `rgba(203,213,225,.85)`、陰影 `14px 0 34px rgba(148,163,184,.16)` | `MainLayout.razor.css` `.sidebar` |
| Topbar | `rgba(255,255,255,.72)` + `backdrop-filter: blur(14px)`、`min-height:4.5rem`、標題 `#334155` 700 | `MainLayout.razor.css` `.top-row` |
| 內容卡片 | `rgba(255,255,255,.82)`、圓角 24px、陰影 `0 20px 60px rgba(148,163,184,.16)`、`padding:1.5rem 2rem` | `MainLayout.razor.css` `article` |
| 選單項 | 高 3rem、圓角 14px、字 `#1f2937`、icon `#475569`；選中為 `linear-gradient(90deg,rgba(191,219,254,.92),rgba(226,232,240,.96))` | `NavMenu.razor.css` |
| 主要按鈕 | `linear-gradient(135deg,#4ea8ff,#2f80ed)`、陰影 `0 12px 24px rgba(47,128,237,.28)` | `NavMenu.razor.css` `.sidebar-hamburger` |
| 列操作鈕 | 28×28、圓角 4px、字 `#4b5563`；hover 字 `#1677ff`／框 `#d6e4ff`／底 `#eef5ff`；danger 字 `#b42318` | `CrudActionButton.razor.css` |

狀態 pill（`padding:2px 10px; border-radius:10px; font-size:12px`）：

| 語意 | 文字 | 底色 | 框線 |
|------|------|------|------|
| 完成／啟用 | `#389e0d` | `#f6ffed` | `#b7eb8f` |
| 待處理 | `#d46b08` | `#fff7e6` | `#ffd591` |
| 處理中 | `#096dd9` | `#e6f7ff` | `#91d5ff` |
| 失敗 | `#cf1322` | `#fff1f0` | `#ffa39e` |
| 停用／無 | `#8c8c8c` | `#fafafa` | `#d9d9d9` |

### Icon 慣例變更

現有管理頁的工具列按鈕使用 **emoji 文字**（`PromptTemplateViewView.razor` 的 ➕ 🔄 ❌ 🔍）。
本規格改為 **Material Icons Outlined**，與 sidebar 及 `CrudActionButton` 的既有模式一致。

| 用途 | icon | 用途 | icon |
|------|------|------|------|
| 新增 | `add` | 檢視 | `visibility` |
| 重新整理 | `refresh` | 編輯 | `edit` |
| 搜尋 | `search` | 刪除 | `delete` |
| 清除條件 | `close` | 上傳區 | `cloud_upload` |
| 逐字稿 | `description` | 下載 | `download` |
| 複製 | `content_copy` | 重新轉錄 | `restart_alt` |
| AI 生成 | `auto_awesome` | 失敗提示 | `error_outline` |
| 寫入待辦 | `playlist_add_check` | 展開 | `expand_more` |

這是對現有慣例的刻意改進；實作時應一併把既有管理頁的 emoji 工具列鈕換掉，避免兩套並存。

Sidebar 選單 icon 沿用 `Datas/Menu.json` 既有值；新增的 TodoList 使用 `checklist`。

---

## 五、待確認：對既有設計決策的轉向

`Meeting` 目前**刻意沒有任何外鍵**，`docs/prd/會議紀錄產生流程-prd.md` 明文記載「產出物歸屬＝獨立實體」。

本規格要求逐字稿歸屬到專案，等同推翻該決策。實作前需確認：

1. 於 `Meeting` 新增可空的 `ProjectId` 外鍵並產生 migration（僅 SQLite 軌道）。
2. 刪除專案時的行為：建議 `ProjectId` 設為 null（保留逐字稿），而非串連刪除。
3. 同步更新 `docs/prd/會議紀錄產生流程-prd.md` 與 `docs/architecture/資料模型與資料庫.md`。

---

## 六、後續實作待辦

### 資料層

- `Meeting` 新增 `ProjectId`（可空外鍵）、會議記錄草稿欄位、使用的提示詞 Id、生成時間。
- 新增 Todo 實體：`ProjectId`（必填外鍵）、`MeetingId`（可空外鍵，標示來源會議記錄）、Title、Description、DueDate、Owner、Priority、Status、IsCompleted、Categories、Teams、CreatedAt、UpdatedAt。
- 於 `MeetingRecord.AccessDatas/Migrations/` 產生對應 migration。

### 業務層

- 新增文字生成用戶端（呼叫 `chat/completions`），消費 `LlmSettings.DefaultProvider` 與 `Model`。
- 補上 `PromptVariableHelper` 的變數代入端（`{{transcript}}`、`{{meetingTitle}}`、`{{meetingDate}}` 目前只有定義、沒有代入）。
- 轉錄佇列需區分工作種類（轉錄／文字生成），或另開一條佇列。
- Todo 的 Service 與 Repository，可參考 `ProjectService` 與 git 歷史中的 `MyTasService`。

### Web 層

- Todo 的 Controller（回傳 `ApiResult<T>`，分頁包 `PagedResult<T>`）、DTO 與 AdapterModel。
- Todo 的 Page 與 View 元件，比照 `ProjectViewView` 的結構。
- 專案項目頁新增 AI 整理區塊與歷史會議記錄清單。
- 語音轉文字頁改為拖拉上傳，並把分段進度回傳到 UI。

### 新頁面權限的宣告式設定（四處缺一不可）

- `MagicObjectHelper` 新增權限鍵常數。
- `RolePermissionService` 的權限矩陣。
- `SidebarMenuService.MenuPermissionMap`（id → 權限鍵）。
- `Datas/Menu.json` 新增選單項（唯一 id、icon `checklist`）。
- `MenuIconTests.AllowedIcons` 加入 `checklist`，否則整套測試會失敗。

---

## 七、相關文件

- [會議紀錄產生流程 PRD](../../prd/會議紀錄產生流程-prd.md) — 本規格會推翻其中的「產出物歸屬＝獨立實體」決策
- [會議紀錄 PRD](../../prd/會議紀錄-prd.md)
- [專案項目 PRD](../../prd/專案項目-prd.md)
- [會議紀錄提示詞 PRD](../../prd/會議紀錄提示詞-prd.md)
- [開發慣例與限制速查](../../architecture/開發慣例與限制速查.md)
- [建立一個新 CRUD 操作網頁說明](../../guides/建立一個新%20CRUD%20操作網頁說明.md) — TodoList 實作時的步驟依據

> 返回 [設計規格索引](README.md)｜[文件總索引](../../README.md)
