# prd — 產品需求文件主控台

- 文件版本：1.8
- 文件狀態：維護中
- 現行系統版本：0.4.118
- 首次實作版本：0.4.23
- 最後核對日期：2026/10/08

本目錄是產品需求的單一入口。PRD 以**產品能力**為單位；「已實作／部分實作」描述程式現況，「規劃中」必須獨立分區，不代表系統已提供。本系統源自 NET10-Blazor-Starter 樣板，0.4.25 起更名為 MeetingRecord 並發展為「AI 會議紀錄系統」，0.4.26 納入「會議紀錄提示詞」能力，**0.4.27 起實際呼叫 Azure OpenAI 完成影音檔的語音轉文字**（見 [會議紀錄](會議紀錄-prd.md)）。**0.4.31 起完成後半段**：套用提示詞範本、呼叫 Azure OpenAI 產生會議紀錄，並把逐字稿歸屬到專案項目（見 [會議紀錄產生流程](會議紀錄產生流程-prd.md)）。系統仍不含 RAG 能力。PRD 內容一律以程式碼、`Menu.json` 與測試為準。

## 一、能力覆蓋矩陣

| 產品能力 | PRD | 入口／路由 | 主要程式來源 | 狀態 | 核對版本 |
|----------|-----|-----------|--------------|------|----------|
| 首頁與導覽 | [首頁與導覽](首頁與導覽-prd.md) | `/`（登入後導向 `/meetings`） | `Pages/Home.razor`、`SidebarMenuService`、`Menu.json`、`MainLayout`（含「關於」對話窗） | 已實作 | 0.4.118 |
| 儀表板 | [儀表板](儀表板-prd.md) | `/dashboard` | `Pages/Dashboards/DashboardPage.razor`、`DashboardService`、`DashboardMetrics` | 已實作（0.4.54；0.4.97 起全員可看、全公司彙總、不放明細） | 0.4.118 |
| 登入與帳號流程 | [登入與帳號流程](登入與帳號流程-prd.md) | `/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Profile`、`/ChangePassword` | `Components/Auths/*`、`MyUserServiceLogin`、`ExternalLoginService`、`AuthController` | 已實作（0.4.113 起首次登入強制改密碼） | 0.4.118 |
| 專案項目 | [專案項目](專案項目-prd.md) | `/projects` | `Pages/Projects/ProjectPage.razor`、`ProjectService`、`ProjectController`、`ProjectTeamWriter` | 已實作 | 0.4.118 |
| 待辦事項 | [待辦事項](待辦事項-prd.md) | `/todos` | `Pages/Todos/TodoPage.razor`、`TodoService`、`TodoController` | 已實作 | 0.4.118 |
| AI 問答（含 AI 抽出待辦） | [AI 問答](AI問答-prd.md) | 無獨立路由：`/projects` 工具列與歷史會議紀錄列、`/meetings` 列（`AiChatModal`） | `AiChatModal`、`AiChatService`、`AiChatStore`（`AiChatPath`）、`ChatContextBuilder`、`AttachmentTextExtractor`、`AiChatDocumentExporter`、`TodoExtractionService`／`TodoExtractionModal` | 已實作（0.4.51；0.4.60 起對話存檔案系統） | 0.4.118 |
| 使用說明 | [使用說明](使用說明-prd.md) | `/help` | `Pages/Helps/HelpPage.razor`、`HelpView`（內嵌 `docs/guides/系統使用說明.md` 為資源）、`MarkdownRenderer` | 已實作（0.4.78） | 0.4.118 |
| 使用者管理 | [使用者管理](使用者管理-prd.md) | `/myusers` | `Pages/Admins/MyUserPage.razor`、`MyUserService` | 已實作 | 0.4.118 |
| 角色管理 | [角色管理](角色管理-prd.md) | `/roleviews` | `Pages/Admins/RoleViewPage.razor`、`RoleViewService`、`RbacWriteService`、`DefaultRoleSeeder` | 已實作（0.4.108 起一鍵建立預設角色） | 0.4.118 |
| AI 用量分析 | [AI 用量分析](AI用量分析-prd.md) | `/ai-usage` | `Pages/AiUsages/AiUsagePage.razor`、`AiUsageRecorder`、`AiUsageAnalysisService`、`AiUsagePricing`、`ExchangeRateBackgroundService`、`scripts/Backfill-AiUsageCost.mjs` | 已實作（0.4.80；0.4.88 起換算新台幣） | 0.4.118 |
| 分類清單 | [分類清單](分類清單-prd.md) | `/categories` | `Pages/Categories/CategoryPage.razor`、`CategoryService`、`CategoryController` | 已實作 | 0.4.118 |
| 團隊清單 | [團隊清單](團隊清單-prd.md) | `/teams` | `Pages/Teams/TeamPage.razor`、`TeamService`、`TeamController`、`ProjectAccessService` | 已實作 | 0.4.118 |
| 會議紀錄提示詞 | [會議紀錄提示詞](會議紀錄提示詞-prd.md) | `/prompttemplates` | `Pages/PromptTemplates/PromptTemplatePage.razor`、`PromptTemplateService`、`PromptTemplateController` | 已實作 | 0.4.118 |
| 會議紀錄（含影音上傳與語音轉文字）| [會議紀錄](會議紀錄-prd.md) | `/meetings` | `Pages/Meetings/MeetingPage.razor`、`MeetingService`、`MeetingFileStore`、`TranscriptionJobRunner`、`MeetingController` | 已實作 | 0.4.118 |
| 會議紀錄產生流程 | [會議紀錄產生流程](會議紀錄產生流程-prd.md) | 跨 `/meetings`、`/projects`（上傳→轉錄→套用提示詞→草稿） | `TranscriptionJobRunner`、`MeetingDraftJobRunner`、`ITextGenerationProvider`、`MeetingDraftProgressNotifier` | 已實作（前半段 0.4.27、後半段 0.4.31） | 0.4.118 |
| 系統健康監控 | [系統健康監控](系統健康監控-prd.md) | `/system-health` | `Pages/SystemHealthPage.razor`、Health services | 已實作 | 0.4.118 |
| 紀錄分類與團隊權控 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md) | 跨功能（所有清單查詢／檔案）| `PermissionChecker`、`ProjectAccessService`、`EffectiveTeamResolver`、`RecordAccessScopeProvider`、`TagStringHelper` | 已實作（團隊標籤權控 0.4.99 起不作用、0.4.103 欄位與相關方法已刪除，現行為專案的主責＋協作團隊） | 0.4.118 |

## 二、無選單入口的核心能力

| 能力 | PRD 歸屬 | 現況 |
|------|----------|------|
| 動作級授權（`[HasPermission("resource:action")]`）與管理員短路 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[角色管理](角色管理-prd.md) | 已實作，UI 與 API 共用單一 RBAC 權威 |
| 稽核軌跡（`AuditLog`：登入、使用者/角色/權限異動）| [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md) | 已實作 |
| 帳號安全（PBKDF2、帳號鎖定、TOTP 骨架）| [登入與帳號流程](登入與帳號流程-prd.md) | 已實作；TOTP 預設關閉 |
| 檔案上傳（專案附件）| [專案項目](專案項目-prd.md) | 已實作 |
| 檔案上傳（會議影音檔，含百分比進度列）| [會議紀錄](會議紀錄-prd.md) | 已實作 |
| 背景工作佇列（行程內 `Channel<T>` + 單一 worker `BackgroundService`）| [會議紀錄](會議紀錄-prd.md)、[會議紀錄產生流程](會議紀錄產生流程-prd.md) | 已實作；語音轉錄與會議紀錄草稿產生各一條佇列，佇列不持久化（重啟時殘留工作改判失敗） |
| 匯率更新背景服務（`ExchangeRateBackgroundService`）| [AI 用量分析](AI用量分析-prd.md) | 已實作（0.4.88）；定期抓匯率供 AI 用量換算新台幣，抓不到時用 `FallbackRate` |
| 付費動作二次確認 | [AI 問答](AI問答-prd.md)、[會議紀錄](會議紀錄-prd.md) | 已實作（0.4.65）；轉錄、產生草稿、抽出待辦、AI 問答送出前一律提示 |
| 外部 AI 供應商抽象（`ITranscriptionProvider`、`ITextGenerationProvider`）| [會議紀錄](會議紀錄-prd.md)、[會議紀錄產生流程](會議紀錄產生流程-prd.md) | 已實作；目前各只有 Azure OpenAI 一個實作 |

## 三、規劃中產品藍圖

| 藍圖 | 現況界線 |
|------|----------|
| 二階段驗證（TOTP）強制啟用流程 | 資料模型與服務骨架已實作（`MyUser.TwoFactorEnabled/Secret`、`TotpService`），預設關閉，尚未提供強制啟用 UI 流程 |
| 逐字稿的下載端點與保留期限政策 | 未實作。**0.4.82 起連線上預覽與編修都移除了**（0.4.55 曾提供），逐字稿只作為生成與 AI 問答的輸入；仍沒有下載端點，也沒有自動清理機制 |
| 各能力後續構想 | 見各 PRD 的「規劃中需求」章節，以及 [Meeting Ink 對標項目決策清單](../planning/08-MeetingInk-對標項目決策清單.md) |

## 四、PRD 維護規則

1. 新增選單頁時，PRD、此覆蓋矩陣、`Menu.json` 與 `SidebarMenuService.MenuPermissionMap` 權限鍵必須同步。
2. 新增無頁面核心能力時，仍須指定一份產品能力 PRD，不得只留實作文件。
3. 現行需求以程式碼、設定與測試為準；文件衝突時校正 PRD 並留下 changelog。
4. 每份 PRD 必須標示文件版本、狀態、現行系統版本、首次實作版本及最後核對日期。
5. 未實作內容只能放在獨立「規劃中需求」章節；部分實作須逐項列出完成／未完成。
6. 文件使用 UTF-8 繁體中文含 BOM；提交前執行 `scripts/Test-DocsEncoding.ps1`。

> 返回 [文件總索引](../README.md)
