# MeetingRecord — AI 會議記錄小助手

以 **.NET 10** 與 **Blazor Server** 打造的 AI 會議紀錄系統：上傳會議影音檔，系統以 FFmpeg 轉檔切段後呼叫 **Azure OpenAI 語音轉文字**產生逐字稿，再套用提示詞範本由 LLM 整理成會議紀錄草稿；會議依「專案」歸檔，可從紀錄抽出待辦事項、對專案或單一會議做 AI 問答，並以儀表板與 Token 用量掌握全貌；管理者可在「AI 對話紀錄」查每次 AI 呼叫實際送出與回覆的內容。

本系統源自內部樣板 NET10-Blazor-Starter（0.4.25 更名為 MeetingRecord），沿用其分層架構、Cookie／JWT 認證、RBAC 與 Ant Design Blazor 介面基礎。

當前版本資訊定義在 [`src/MeetingRecord/MeetingRecord.Web/appsettings.json`](src/MeetingRecord/MeetingRecord.Web/appsettings.json) 之 `SystemSettings.SystemInformation.SystemVersion` 欄位。

---

## 1. 專案介紹

- **定位**：公司內部的會議紀錄與追蹤系統 —— 影音轉逐字稿、AI 整理會議紀錄、抽出待辦、專案層級 AI 問答。
- **適用情境**：專案例會、客戶會議、內部討論的紀錄與後續追蹤。
- **資料權限**：角色＝能做什麼、團隊＝誰的資料（專案主責＋協作團隊）、分類＝什麼資料（只是標籤）；會議、待辦、附件、AI 問答跟著專案走，詳見[紀錄分類與團隊權控 PRD](docs/prd/紀錄分類與團隊權控-prd.md)。
- **功能全貌**：見[系統功能總覽](docs/architecture/系統功能總覽.md)；終端使用者操作見[系統使用說明](docs/guides/系統使用說明.md)（也是系統內 `/help` 頁的內容）。

---

## 2. 技術棧

| 類別 | 採用方案 | 版本 |
|------|----------|------|
| 框架 | .NET / ASP.NET Core | 10.0 |
| 前端 | Blazor Server（Interactive Server）+ 全域 SSR 渲染 | — |
| UI 元件庫 | AntDesign Blazor | 1.6.0 |
| 圖示 | BlazorMaterialIcons（Google Material Icons） | 0.0.1 |
| 資料存取 | Entity Framework Core | 10.0.5 |
| 資料庫 | SQLite（唯一支援；檔案位置由 `appsettings.json` 控制） | — |
| 認證 | ASP.NET Core Cookie Authentication（含 RememberMe） | — |
| 授權 | RoleView + Menu.json 二維權限樹 | — |
| 多語系 | `RequestLocalization` + `AntDesignLocaleFactory` | zh-TW / en-US |
| 物件對映 | AutoMapper | — |
| API 文件 | Swashbuckle Swagger UI | 10.1.5 |
| 日誌 | NLog.Web.AspNetCore | 6.1.2 |

---

## 3. 系統架構速覽

```
MeetingRecord.Web ──► MeetingRecord.Business ──► MeetingRecord.AccessDatas
       │                  │                       │
       └──► MeetingRecord.Dtos / MeetingRecord.Models / MeetingRecord.Share
```

| 專案 | 角色 |
|------|------|
| `MeetingRecord.Web` | Blazor Server 宿主、Razor 元件、Web API Controller、登入頁、本地化、靜態資源。 |
| `MeetingRecord.Business` | 商業邏輯：Services、Repositories、AutoMapper Profile、Helpers。 |
| `MeetingRecord.AccessDatas` | EF Core `BackendDBContext`、Entity 定義、Migrations。 |
| `MeetingRecord.Models` | 系統設定、AdapterModel、共用領域模型。 |
| `MeetingRecord.Dtos` | API 對外傳輸物件：`ApiResult<T>`、`PagedResult<T>`、各模組 DTO。 |
| `MeetingRecord.Share` | 跨層共用：Helpers、Extensions（無外部相依）。 |

啟動流程詳見 [docs/architecture/架構總覽.md](docs/architecture/架構總覽.md)。

---

## 4. 主要功能

**核心功能**（側邊欄「核心功能」區）

- **儀表板**（`/dashboard`）：全員可看、全公司同一份彙總數字（待辦、專案、會議時數、提示詞範本、儲存空間），刻意不放明細（見[儀表板 PRD](docs/prd/儀表板-prd.md)）
- **會議紀錄**（`/meetings`）：上傳一個影音檔（mp3／wma／mp4／mkv… 上限 1GB，可拖拉上傳），以 FFmpeg 轉 mp3 切段後呼叫 **Azure OpenAI 語音轉文字**；套用提示詞範本由 LLM 產生會議紀錄草稿（串流進度、逐字稿雜訊過濾），可在左右同步捲動的編修器修訂，匯出 PDF；轉錄與草稿產生走行程內背景佇列，可追蹤、可取消、失敗可重跑（見[會議紀錄 PRD](docs/prd/會議紀錄-prd.md)、[會議紀錄產生流程 PRD](docs/prd/會議紀錄產生流程-prd.md)）
- **專案項目**（`/projects`）：會議的歸檔單位；主責團隊（必填）＋協作團隊決定誰看得到，常用名詞與與會人員名冊餵給 AI 校正人名，可附加多個附件（見[專案項目 PRD](docs/prd/專案項目-prd.md)）
- **待辦事項**（`/todos`）：手動新增或由 AI 從會議紀錄抽出（標示重複項目），負責人面板與膠囊篩選（見[待辦事項 PRD](docs/prd/待辦事項-prd.md)）
- **AI 問答**：針對整個專案或單一會議提問，多段對話、可附加圖片與檔案、回答以 Markdown 呈現並可下載 PDF；對話存檔案系統（見[AI 問答 PRD](docs/prd/AI問答-prd.md)）
- **使用說明**（`/help`）：系統內的白話操作手冊與 FAQ，內容即 [`docs/guides/系統使用說明.md`](docs/guides/系統使用說明.md)
- **付費動作一律二次確認**：轉錄、產生草稿、抽出待辦、AI 問答等會花錢的動作送出前都會提示

**系統管理與資料定義**

- **帳號與權限**：使用者管理（含首次登入強制改密碼、停用帳號在登入頁擋下）、角色管理（一鍵建立預設角色）、**稽核紀錄**（`/audit-logs`，1.0.32，只限管理者：誰、何時、從哪個 IP、做了什麼，可篩選、匯出 CSV、清除，預設保留 365 天；1.0.33 起也記登出、Google 登入、密碼異動、帳號啟停用、團隊成員異動與權限拒絕，1.0.34 起也記專案、會議、待辦、團隊、分類、提示詞、AI 問答的異動；見[稽核紀錄 PRD](docs/prd/稽核紀錄-prd.md)）
- **監控與診斷**（0.4.120 起；都只限管理者，不能勾給角色）：
  - **系統健康監控**（`/system-health`）：18 項檢查（0.4.123 新增登入金鑰、快取服務、例外紀錄管線；1.0.29 新增 AI 用量上限）的健康分數、紅黃綠燈號，與展開才載入的最近 24 小時警告以上日誌；1.0.35 起可手動「測試 AI 連線」（實際呼叫一次文字生成與語音轉錄，會產生極少費用）（見[系統健康監控 PRD](docs/prd/系統健康監控-prd.md)）
  - **日誌檢視**（`/logs`，0.4.121）：依時間、等級、關鍵字、TraceId 查 NLog 檔案日誌，展開看堆疊、複製、匯出 .log；**AI 日誌分析**把最新 100 筆送給 AI 整理重點與建議（會產生費用，記入 AI 用量）（見[日誌檢視 PRD](docs/prd/日誌檢視-prd.md)）
  - **日誌等級設定**（`/log-level-setting`）：執行期調整 NLog 等級（只在記憶體、重啟即回預設）（見[日誌等級設定 PRD](docs/prd/日誌等級設定-prd.md)）
  - **系統例外紀錄**（`/system-exceptions`，0.4.122，含 AI 例外分析、錯誤追蹤碼）：畫面、Web API、背景作業、系統啟動與瀏覽器的例外自動收集，相同例外合併計次、完整堆疊另存檔案；可篩選、複製、匯出 CSV、清除；**AI 例外分析**把單筆例外送給 AI 找根因與修正建議（會產生費用，記入 AI 用量）；錯誤畫面顯示 8 碼錯誤追蹤碼，可在此頁與日誌檢視查到同一次操作（見[系統例外紀錄 PRD](docs/prd/系統例外紀錄-prd.md)）
  - **資料庫用量**（`/database-usage`）：SQLite 檔案與各資料表用量，以及各外部目錄大小與磁碟剩餘空間（見[資料庫用量 PRD](docs/prd/資料庫用量-prd.md)）
- **AI 管理**（1.0.30 起；都只限管理者，不能勾給角色）：
  - **AI 對話紀錄**（`/ai-call-logs`）：每次付費 AI 呼叫的完整送出內容與回應，可依日期、功能、結果、使用者、模型、關鍵字篩選；明細含用量與費用、「同一趟工作」的其他呼叫，可下載 JSON、匯出 PDF；CSV 不含內容；可手動清除，並依保留天數自動清除（見[AI 對話紀錄 PRD](docs/prd/AI對話紀錄-prd.md)）
  - **Token 用量**（`/token-usage`，舊網址 `/ai-usage` 仍可用；1.0.29 以前叫「AI 用量分析」、在監控與診斷底下）：每次 LLM／轉錄呼叫記入用量帳本，依 `LlmSettings.Pricing` 計價，並以背景服務抓取匯率換算新台幣；1.0.29 起所有付費 AI 呼叫經 `AiCallGateway`，同時寫 AI 對話紀錄，並可設每月用量上限；1.0.31 起頁面有上限列、篩選（按查詢才套用）、圖表／每日／依使用者／依功能／依模型／明細頁籤、單筆明細窗（可跳到同一筆 AI 對話紀錄）、CSV／PDF 匯出與刪除、清除（見[Token 用量 PRD](docs/prd/Token用量-prd.md)）
- 資料定義：分類清單、團隊清單、提示詞清單（內建範本、`{{transcript}}` 等變數、啟用狀態就地切換）
- AI 供應商可換：`ITranscriptionProvider` 與文字生成供應商為擴充點，provider／endpoint／key／model 全部由 `appsettings.json` 決定，目前內建 Azure OpenAI

**平台能力（沿用樣板）**

- 登入 / 登出（Cookie 驗證、記住我、4 位數驗證碼、燕麥奶茶色系登入頁）、Google OAuth2 第三方登入
- RBAC：`RoleView` 權限樹（對應 `Menu.json` 的唯一 `id`），受保護 API 以 `[HasPermission]` 動作級授權
- 每筆紀錄可附加多檔案，自動依年月分目錄存放
- Web API（含 Swagger UI、`ApiResult<T>` 信封、分頁搜尋）
- 平行 API 路由：保留 `/api/...`，新增 `/api/v1/...` 作為新用戶端標準入口
- Health checks：`/health/live`、`/health/ready`
- 系統健康監控頁：`/system-health`，只限管理員，查看 18 項檢查的健康分數、紅黃綠燈號與最近 24 小時警告以上日誌
- 分散式快取：`ICacheService` 統一抽象，透過 `appsettings.json` 在 Memory ↔ Redis 間切換（側邊選單已套用）
- Production 啟動安全檢查：JWT key、預設密碼、Swagger 暴露策略與 Redis 連線字串需明確設定
- Sidebar 導覽：JSON 定義、三層選單（收合時浮出選單同樣支援第三層）、可收合、自動套用使用者角色權限
- 右上角使用者選單「關於」對話窗：顯示系統名稱／描述／版本、執行環境、.NET 版本、啟動與已運作時間
- 多語系：以瀏覽器 `Accept-Language` 自動切換，AntDesign 元件本地化
- 全站請求耗時 / 例外統一寫入 NLog
- 靜態資源外部對應（`/UploadFiles` → 實體下載目錄）

---

## 5. 快速開始

> 需求：Windows 10 / 11、.NET 10 SDK、IDE（Visual Studio 2026 / Rider / VS Code）。
>
> 選用：**FFmpeg**——只有語音轉錄會用到，不裝也能啟動（啟動時會記一則 WARN）。安裝後預設走 PATH：
>
> ```powershell
> winget install --id Gyan.FFmpeg -e   # 裝完開新的終端機讓 PATH 生效
> ffmpeg -version                      # 確認找得到
> ```

```powershell
# 1. 還原與編譯
cd src/MeetingRecord
dotnet restore
dotnet build

# 2. 第一次跑（會自動建立目錄、套用 Migration、Seed 預設角色與帳號）
dotnet run --project MeetingRecord.Web/MeetingRecord.Web.csproj
```

### 之後每次啟動

上面那串只有第一次需要打。日常啟動用倉庫根目錄的 **`run.cmd`**，
它會自己切到正確的目錄，不必先 `cd`：

```powershell
.\run          # http  → http://localhost:5189（預設，會自動開瀏覽器）
.\run https    # https → https://localhost:7044
```

也可以在檔案總管按兩下 `run.cmd`。用 Visual Studio 的話直接按 F5，不需要這個腳本。

> **完整流程見 [docs/operations/啟動與停止系統.md](docs/operations/啟動與停止系統.md)**
> ——包含如何安全停掉正在跑的實例（**不要用映像名稱砍行程**，會連帶砍掉
> Visual Studio）、`run.cmd` 與 VS 為什麼不能同時用，以及哪些檔案改了不必重新編譯。

預設帳號 / 密碼由 `appsettings.json` 之 `BootstrapSettings`（`SupportAccount` / `SupportPassword`）定義，首次啟動會自動建立 `support` 管理員帳號；`MagicObjectHelper.開發者帳號` 則用於後續識別並保護該開發者帳號。

外部目錄與資料庫檔位置由 `appsettings.json` 之 `SystemSettings.ExternalFileSystem` 控制，預設位於 `C:\temp\MeetingRecord\…`，啟動時若不存在會自動建立。

關於 EF Core Migration 指令請見 [docs/guides/EFCore.md](docs/guides/EFCore.md)。

---

## 6. 專案結構

```
.
├── readme.md                       ← 本檔（系統入口說明）
├── run.cmd                         ← 一鍵啟動（.\run 或按兩下；等同 dotnet run --launch-profile http）
├── AGENTS.md / CLAUDE.md           ← LLM 協作行為準則與專案速查入口
├── docs/                           ← 系統設計與規範文件（依特性分類，見第 9 節）
│   ├── README.md                   ← 文件目錄索引與分類規則
│   ├── planning/                   ← 專案規劃、Meeting Ink 對標、樣板期 TODO（已封存）
│   ├── architecture/               ← 架構、資料模型、API/DTO 規範、開發慣例速查、介面與對話窗設計規範
│   ├── security/                   ← 認證、授權、密碼與機密金鑰
│   ├── features/                   ← 個別功能機制（快取、多語系、上傳、健康監控）
│   ├── guides/                     ← 開發/操作教學（CRUD、EFCore、測試、畫面與欄位字典、系統使用說明）
│   ├── operations/                 ← 啟動停止、維護、部署、設定檔、CI/CD
│   ├── prd/                        ← 產品需求文件（PRD 主控台與各能力 PRD）
│   ├── superpowers/                ← brainstorming 設計規格
│   └── changelog/                  ← 變更紀錄
├── scripts/                        ← Test-DocsEncoding.ps1、Backfill-AiUsageCost.mjs、樣板遺留腳本
└── src/MeetingRecord/
    ├── MeetingRecord.slnx              ← 方案檔（新版 .slnx 格式）
    ├── MeetingRecord.Web/              ← Blazor Server 宿主
    │   ├── Components/             ← Pages / Views / Layout / Auths / Commons
    │   ├── Controllers/            ← Web API（Project / Category / Team / Meeting / Todo / PromptTemplate / Auth …）
    │   ├── BackgroundServices/     ← 轉錄、會議紀錄草稿產生、匯率更新、例外紀錄保存清理四個背景服務
    │   ├── Extensions/             ← 服務註冊與 middleware（ServiceCollectionExtensions 等）
    │   ├── Health/                 ← 系統健康監控檢查項
    │   ├── Diagnostics/            ← 監控與診斷：日誌查詢（LogQueryService）、日誌等級（LogLevelRuntimeState）、資料庫與檔案儲存用量、系統例外紀錄管線（ExceptionLogProvider／ExceptionLogWriter、TraceCode）
    │   ├── Localization/           ← AntDesignLocaleFactory
    │   ├── Datas/Menu.json         ← Sidebar 導覽與權限定義
    │   ├── Filters/                ← ApiValidationFilter 等
    │   ├── Program.cs              ← 啟動主程式
    │   └── appsettings.json        ← 系統設定（含 SystemVersion）
    ├── MeetingRecord.Business/
    │   ├── Services/DataAccess/    ← Domain Service（CRUD）
    │   ├── Services/Other/         ← AuthenticationStateHelper、MeetingFileStore 等
    │   ├── Services/Transcription/ ← 語音轉錄管線（供應商抽象、FFmpeg、佇列、JobRunner）
    │   ├── Services/TextGeneration/← 會議紀錄草稿產生（LLM、串流進度）
    │   ├── Services/TodoExtraction/← AI 從會議紀錄抽出待辦
    │   ├── Services/AiChat/        ← AI 問答（對話存檔案系統、附件）
    │   ├── Services/AiUsage/       ← AI 用量帳本、計價與匯率
    │   ├── Services/Dashboard/     ← 儀表板彙總
    │   ├── Services/Export/        ← 會議紀錄／AI 問答 PDF 匯出
    │   ├── Repositories/           ← API 層使用的 Repository
    │   └── Models/AutoMapping.cs   ← AutoMapper Profile
    ├── MeetingRecord.AccessDatas/
    │   ├── BackendDBContext.cs
    │   ├── Models/                 ← Entity（MyUser、Project、Category、Team、Meeting…）
    │   └── Migrations/
    ├── MeetingRecord.Models/           ← AdapterModel、Systems、AutoMapper 來源
    ├── MeetingRecord.Dtos/             ← API DTO（含 ApiResult/PagedResult）
    └── MeetingRecord.Share/            ← Helpers、Enums、Extensions
```

---

## 7. 設定檔說明（`appsettings.json`）

| 區段 | 用途 |
|------|------|
| `Logging` | .NET 預設記錄等級設定（NLog 啟動後會接管實際輸出）。 |
| `Swagger.EnabledInProduction` | 是否在 Production 環境暴露 Swagger UI，預設 `false`。 |
| `Security.ReturnExceptionDetails` | 是否於 `ApiResult.Exception` 回傳例外細節；`null` 時依環境決定。 |
| `Cors.AllowedOrigins` | CORS 允許來源白名單（陣列）；留空表示不額外開放跨來源。 |
| `CacheSettings.Provider` | 快取 provider，支援 `Memory` 與 `Redis`，預設 `Memory`（見 [分散式快取機制](docs/features/分散式快取機制.md)）。 |
| `CacheSettings.RedisConnection` | `Provider=Redis` 時的 Redis 連線字串；Production 使用 Redis 時必須設定。 |
| `CacheSettings.InstanceName` | Redis 快取鍵前綴，預設 `MeetingRecord:`。 |
| `CacheSettings.DefaultExpirationMinutes` | 快取項目預設存活時間（分鐘），預設 `30`。 |
| `NLog.BasePath` | NLog 寫入的根目錄；專案會在其下建立 `MeetingRecord.Web` 子目錄並輸出檔案日誌。 |
| `JwtSettings` | Web API JWT 設定：`Issuer`、`Audience`、`SigningKey`、`AccessTokenMinutes`、`RefreshTokenDays`、`ClockSkewMinutes`；Production 啟動時若仍為開發用 `SigningKey` 會中止啟動。 |
| `CookieSettings` | 網頁登入 Cookie 效期（0.4.119）：`ExpireMinutes`（沒勾記住我，預設 480＝8 小時）、`RememberMeDays`（勾了記住我與 Google 登入，預設 30 天）、`SlidingExpiration`（預設 `true`，期間回來使用會自動延長）。 |
| `LogRetentionSettings` | 系統例外紀錄保存天數（0.4.122）：`ExceptionLogDays`（預設 90，「最後發生」超過的每天自動刪除，`0`＝不自動清理）；AI 用量帳本保存天數（1.0.29）：`AiUsageLogDays`（預設 `0`＝永久保留，啟用時至少 62）；稽核紀錄保存天數（1.0.32）：`AuditLogDays`（預設 365，`0`＝不自動清理）。 |
| `AiCallLogSettings` | AI 對話紀錄（1.0.29）：`Enabled`（預設 `true`，記錄每次付費 AI 呼叫的送出內容與回應）、`RetentionDays`（預設 90，1～3650，沒有永久保留）。 |
| `AiQuotaSettings` | AI 用量上限（1.0.29，新台幣）：`GlobalMonthlyTwd`（全系統每月）、`PerUserMonthlyTwd`（每人每月），`0`＝不限制（預設）；改設定檔即生效、不必重啟。 |
| `ClientErrorReporting` | 瀏覽器端錯誤回報（0.4.122）：`Enabled`（預設 `true`）、`MaxPerCircuitPerMinute`（每個連線每分鐘上限，預設 10）。 |
| `BootstrapSettings` | 預設 `support` 帳號種子設定：`SupportAccount` / `SupportName` / `SupportEmail` / `SupportPassword`（首次啟動建立，重啟時更新密碼）。 |
| `GoogleOAuthSettings` | Google OAuth2 第三方登入：`Enabled`、`ClientId`、`ClientSecret`、`DefaultRoleName`（見 [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md)）。 |
| `LlmSettings` | LLM 與語音轉錄供應商設定：`DefaultProvider`（文字生成：會議紀錄草稿、抽出待辦、AI 問答）與 `TranscriptionProvider`（語音轉錄，留空則沿用前者），`Providers.<供應商>` 下有 `Endpoint`／`ApiKey`／`Model`／`TranscriptionModel`（1.0.28 起文字生成走 Azure OpenAI v1 API，不需要 `ApiVersion`；轉錄的 api-version 內建，`TranscriptionApiVersion` 選填）；`Currency` 與 `Pricing.<模型>`（每百萬 token 或每分鐘音訊單價）供 AI 用量計價。`ApiKey` 請以 user-secrets／環境變數提供（見 [日誌與設定檔說明](docs/operations/日誌與設定檔說明.md)）。 |
| `ExchangeRateSettings` | AI 用量換算新台幣的匯率來源：`Enabled`、`SourceUrl`、`TargetCurrency`（預設 `TWD`）、`RefreshIntervalHours`（預設 24）、`FallbackRate`（抓不到時的備援匯率）；由 `ExchangeRateBackgroundService` 定期更新。 |
| `MediaSettings.FfmpegPath` | FFmpeg 執行檔路徑，預設 `ffmpeg`（走 PATH）。語音轉錄前一律用它把影音檔轉成 mp3 並切段。啟動時會驗證存在性：Production 找不到就中止，其他環境記 WARN 後照常啟動。 |
| `ExportSettings.BrowserPath` | 產生會議紀錄 PDF 用的瀏覽器執行檔（Edge 或 Chrome）。**留空即自動偵測**常見安裝位置，Windows 內建 Edge 因此通常不必設定。找不到時啟動記 WARN，匯出當下會回明確錯誤。 |
| `SystemSettings.ConnectionStrings.SQLiteDefaultConnection` | SQLite 連線範本；實際連線字串由 `MagicObjectHelper.GetSQLiteConnectionString` 結合 `DatabasePath` 產生。 |
| `SystemSettings.SystemInformation.SystemName` | 顯示用系統名稱。 |
| `SystemSettings.SystemInformation.SystemDescription` | 顯示用系統描述。 |
| **`SystemSettings.SystemInformation.SystemVersion`** | **唯一版本來源**，每次完成異動必須遞增（見第 8 節）。 |
| `SystemSettings.ExternalFileSystem.DatabasePath` | SQLite 資料庫檔放置目錄。 |
| `SystemSettings.ExternalFileSystem.DownloadPath` | `/UploadFiles` 對應的實體目錄（靜態資源外掛）。 |
| `SystemSettings.ExternalFileSystem.UploadPath` | 通用上傳暫存目錄。 |
| `SystemSettings.ExternalFileSystem.ProjectFilePath` | 專案附件根目錄（再依年/月細分）。 |
| `SystemSettings.ExternalFileSystem.MeetingMediaPath` | 會議影音檔根目錄（再依年/月細分）；不對外服務。 |
| `SystemSettings.ExternalFileSystem.MeetingTranscriptPath` | 會議逐字稿根目錄（再依年/月細分）；不對外服務，也沒有下載端點。 |
| `SystemSettings.ExternalFileSystem.AiChatPath` | AI 問答對話與附件的存放根目錄（0.4.60 起對話改存檔案系統）；不對外服務。 |
| `SystemSettings.ExternalFileSystem.DataProtectionKeyPath` | Data Protection 金鑰環目錄（0.4.119），登入 Cookie 靠它加密，存在這裡才撐得過 IIS 回收與重啟；留空啟動失敗、不可放在網站目錄內、刪掉＝所有人重登。 |
| `SystemSettings.ExternalFileSystem.ExceptionPath` | 系統例外紀錄的完整堆疊檔目錄（0.4.122，`yyyyMM/` 細分），底下 `pending/` 放啟動失敗的補登檔；不對外服務。 |
| `SystemSettings.ExternalFileSystem.AiCallLogPath` | AI 對話紀錄的內容檔目錄（1.0.29，`yyyyMM/{CallId}.json`），含逐字稿、會議紀錄與 AI 問答全文；只開放給應用程式身分、不可放在網站或下載目錄內；不對外服務。 |
| `AutoMapper:LicenseKey` | AutoMapper 商業授權金鑰（可留空）。 |

各區段詳解見 [docs/operations/日誌與設定檔說明.md](docs/operations/日誌與設定檔說明.md)。

---

## 8. 版本管理與維護規範（**重要**）

本專案採用單一版本來源策略。請務必遵守下列規範：

1. **每次更新完成必須 bump 版本**：
   每次異動一律將 `appsettings.json` 之 `SystemSettings.SystemInformation.SystemVersion` 的**最後一碼（Patch）+1**（例：`0.4.0 → 0.4.1`，不進位、不依異動性質區分），並把括號內日期更新為當天。

   ```json
   "SystemVersion": "0.4.1 (2026/06/22)"
   ```

   `SystemVersion` 為任何提交都應 bump 的最小單位。版號顯示格式為 `Major.Minor.Patch (YYYY/MM/DD)`；遞增規則詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md) 第 1.2 節。

2. **影響到既有文件就必須同步更新**：
   只要本次異動會改變既有功能、模組、API、資料表、設定欄位或 UI 行為，必須同步修訂相關文件。對應表詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md)。

3. **檔案編碼**：
   `docs/` 下所有 `.md` 一律使用 **UTF-8 含 BOM**（CI 以 [`scripts/Test-DocsEncoding.ps1`](scripts/Test-DocsEncoding.ps1) 遞迴強制，缺 BOM 或含亂碼即失敗）；其餘原始碼、設定檔採 UTF-8 即可。提交前需自行確認繁體中文無亂碼。

4. **撰寫文件時請對應實際 codebase**：
   引用程式檔請寫「相對路徑＋類別／方法／區塊名稱」的錨點，**不要寫 `檔案:行號`**（行號會隨程式漂移）；表頭與封存規則見 [維護規範](docs/operations/維護規範.md) 第 6 節。

---

## 9. 文件索引

完整分類規則與清單見 [docs/README.md](docs/README.md)；新增文件請先依特性歸入既有子目錄，無適用分類時自動新增英文小寫目錄並同步該索引與本節。

### 架構與設計（architecture）

- [開發慣例與限制速查](docs/architecture/開發慣例與限制速查.md) — **AI/開發者必讀**：分層、SQLite migration、追蹤清理、權限同步、資料權控等不變量速查。
- [架構總覽](docs/architecture/架構總覽.md) — 6 個專案分層、依賴方向、啟動流程、DI 註冊清單、背景服務。
- [系統功能總覽](docs/architecture/系統功能總覽.md) — 功能面全貌：端到端流程、每個選單頁在做什麼、權限與背景工作、已知問題與限制。
- [資料模型與資料庫](docs/architecture/資料模型與資料庫.md) — `BackendDBContext`、主要 Entity、關聯與刪除政策。
- [DTO 與模型邊界規範](docs/architecture/DTO%20與模型邊界規範.md) — API / UI / Business / Entity 資料邊界原則與新 CRUD 模組待辦。
- [Web API 設計慣例](docs/architecture/Web%20API%20設計慣例.md) — Controller 樣板、`ApiResult<T>`、`PagedResult<T>`、Search DTO。
- [Web API 端點目錄](docs/architecture/Web%20API%20端點目錄.md) — 全部 controller 的實際路由、授權與回傳型別對照表。
- [介面視覺設計規範](docs/architecture/介面視覺設計規範.md) — 常駐介面：燕麥奶茶色票（`--oat-*`）、樣式位置與載入順序、側邊欄、表格、按鈕與狀態膠囊。
- [對話窗 UI 設計規範](docs/architecture/對話窗%20UI%20設計規範.md) — 浮層：`FormModalHelper` 尺寸類別與雙欄版型、取消確認、付費二次確認、確認窗與通知。
- [API Versioning 策略](docs/architecture/API%20Versioning%20策略.md) — `/api/...` 與 `/api/v1/...` 平行路由、Swagger v1 分組與後續導入策略。

### 認證與安全（security）

- [權限授權現況評估與改善路線](docs/security/權限授權現況評估與改善路線.md) — 角色／團隊／分類分工後的權控現況評估與後續改善項目。
- [認證授權與權限機制](docs/security/認證授權與權限機制.md) — Cookie scheme、Claims、`RoleView` JSON、`Menu.json` 權限樹、`ProjectAccessService` 資料權控。
- [密碼種類與儲存機制](docs/security/密碼種類與儲存機制.md) — 密碼種類盤點、`MyUser.Password` 雜湊、首次登入強制改密碼、種子密碼與機密金鑰。
- [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md) — Google SSO 設定、自動建帳與審核、串接權控與 API（JWT）。
- [記住我登入原理說明](docs/security/記住我登入原理說明.md) — Cookie + RememberMe 完整原理。

### 功能機制（features）

- [分散式快取機制](docs/features/分散式快取機制.md) — `ICacheService`、Memory ↔ Redis 切換、選單快取與失效行為。
- [多語系與本地化](docs/features/多語系與本地化.md) — `RequestLocalization` 設定、`AntDesignLocaleFactory`、支援文化。
- [檔案上傳機制](docs/features/檔案上傳機制.md) — 專案附件、會議影音、AI 問答附件、年月目錄、刪除同步、容量上限。
- [系統健康監控](docs/features/系統健康監控.md) — 18 項檢查的健康分數、紅黃綠燈號、部署探針與最近 24 小時警告以上日誌。

### 開發與操作指南（guides）

- [系統使用說明](docs/guides/系統使用說明.md) — ⚠️ 寫給終端使用者，同時是系統內 `/help` 頁的內容來源。
- [畫面與欄位字典](docs/guides/畫面與欄位字典.md) — 每個畫面、每個欄位的意義、對應程式屬性與坑（給開發與測試）。
- [上架前人工測試清單](docs/guides/上架前人工測試清單.md) — 上線前逐項人工驗收的流程清單。
- [建立一個新 CRUD 操作網頁說明](docs/guides/建立一個新%20CRUD%20操作網頁說明.md) — 以 `RoleViewView` 為藍本複刻新 CRUD 頁面。
- [EFCore 指令備忘](docs/guides/EFCore.md) — Migration 指令範本（SQLite 單軌）。
- [測試指南](docs/guides/測試指南.md) — 測試類別涵蓋矩陣、本機執行、整合測試與覆蓋率。
- [腳手架新專案啟動流程](docs/guides/腳手架新專案啟動流程.md) — 🗄️ 已封存：樣板時期「複製成新系統」的檢查清單。
- `scripts/New-StarterProject.ps1`、`scripts/New-CrudModule.ps1` — 樣板遺留工具，本系統日常開發不需使用。

### 維運與部署（operations）

- [啟動與停止系統](docs/operations/啟動與停止系統.md) — `run.cmd` 一鍵啟動、安全停止實例、與 Visual Studio 並用的注意事項。
- [維護規範](docs/operations/維護規範.md) — 版本 bump、文件同步表、表頭與封存規則、commit 前自我檢查清單。
- [正式部署與安全檢查清單](docs/operations/正式部署與安全檢查清單.md) — 上線前 JWT、預設帳號、Swagger、例外揭露等必查項目。
- [日誌與設定檔說明](docs/operations/日誌與設定檔說明.md) — NLog 配置、各層級用法、`appsettings.json` 全表、AI 用量回補腳本。
- [CI-CD 與品質檢查](docs/operations/CI-CD與品質檢查.md) — GitHub Actions 流程、文件編碼檢查、弱點掃描。

### 產品需求文件（prd）

- [PRD 主控台（能力覆蓋矩陣）](docs/prd/README.md) — 以產品能力為單位的單一入口：能力→入口→程式來源→狀態。
- 核心功能：[儀表板](docs/prd/儀表板-prd.md)、[會議紀錄](docs/prd/會議紀錄-prd.md)、[會議紀錄產生流程](docs/prd/會議紀錄產生流程-prd.md)、[會議紀錄提示詞](docs/prd/會議紀錄提示詞-prd.md)、[專案項目](docs/prd/專案項目-prd.md)、[待辦事項](docs/prd/待辦事項-prd.md)、[AI 問答](docs/prd/AI問答-prd.md)、[使用說明](docs/prd/使用說明-prd.md)。
- 系統管理與平台：[首頁與導覽](docs/prd/首頁與導覽-prd.md)、[登入與帳號流程](docs/prd/登入與帳號流程-prd.md)、[使用者管理](docs/prd/使用者管理-prd.md)、[角色管理](docs/prd/角色管理-prd.md)、[稽核紀錄](docs/prd/稽核紀錄-prd.md)、[AI 對話紀錄](docs/prd/AI對話紀錄-prd.md)、[Token 用量](docs/prd/Token用量-prd.md)、[系統健康監控](docs/prd/系統健康監控-prd.md)、[日誌檢視](docs/prd/日誌檢視-prd.md)、[日誌等級設定](docs/prd/日誌等級設定-prd.md)、[系統例外紀錄](docs/prd/系統例外紀錄-prd.md)、[資料庫用量](docs/prd/資料庫用量-prd.md)、[分類清單](docs/prd/分類清單-prd.md)、[團隊清單](docs/prd/團隊清單-prd.md)、[紀錄分類與團隊權控](docs/prd/紀錄分類與團隊權控-prd.md)。

### 設計規格（superpowers）

- [docs/superpowers/](docs/superpowers/README.md) — 以 brainstorming 流程產出的設計規格（會議記錄流程 Wireframe；樣板時期的分類/團隊頁面與紀錄權控規格已封存）。

### 變更紀錄（changelog）

- [docs/changelog/](docs/changelog/README.md) — 全部改版紀錄的單一索引（依版本新→舊）。
  此處刻意不重複列舉，避免三處索引長期不同步。

### 專案規劃（planning）

- [docs/planning/](docs/planning/README.md) — Meeting Ink 對標分析與決策清單（進行中）；樣板時期的 `01`～`06` TODO 為已封存快照。

---

## 10. 參考連結

- AntDesign Blazor — https://antblazor.com/
- BlazorMaterialIcons — https://github.com/dimohy/BlazorMaterialIcons
- Google Material Icons — https://fonts.google.com/icons
- NLog — https://nlog-project.org/
- AutoMapper — https://automapper.org/

---

## 11. 授權與貢獻

本專案為公司內部系統，請依團隊約定條款使用。提交異動前請確認已遵守第 8 節「版本管理與維護規範」全部要求。
