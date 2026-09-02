# prd — 產品需求文件主控台

- 文件版本：1.3
- 文件狀態：維護中
- 現行系統版本：0.4.33
- 首次實作版本：0.4.23
- 最後核對日期：2026/09/01

本目錄是產品需求的單一入口。PRD 以**產品能力**為單位；「已實作／部分實作」描述程式現況，「規劃中」必須獨立分區，不代表系統已提供。本專案自 0.4.25 起由通用 Blazor 腳手架轉為「會議紀錄系統」的開發基底，0.4.26 納入「會議紀錄提示詞」能力，**0.4.27 起實際呼叫 Azure OpenAI 完成影音檔的語音轉文字**（見 [會議紀錄](會議紀錄-prd.md)）。**0.4.31 起完成後半段**：套用提示詞範本、呼叫 Azure OpenAI 產生會議紀錄，並把逐字稿歸屬到專案項目（見 [會議紀錄產生流程](會議紀錄產生流程-prd.md)）。系統仍不含 RAG 能力。PRD 內容一律以程式碼、`Menu.json` 與測試為準。

## 一、能力覆蓋矩陣

| 產品能力 | PRD | 入口／路由 | 主要程式來源 | 狀態 | 核對版本 |
|----------|-----|-----------|--------------|------|----------|
| 首頁與導覽 | [首頁與導覽](首頁與導覽-prd.md) | `/`、`/App` | `Pages/Home.razor`、`Pages/HomeAuthed.razor`、`SidebarMenuService`、`Menu.json`、`MainLayout`（含「關於」對話窗） | 已實作 | 0.4.24 |
| 登入與帳號流程 | [登入與帳號流程](登入與帳號流程-prd.md) | `/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Profile`、`/ChangePassword` | `Components/Auths/*`、`MyUserServiceLogin`、`ExternalLoginService`、`AuthController` | 已實作 | 0.4.23 |
| 專案項目 | [專案項目](專案項目-prd.md) | `/projects` | `Pages/Projects/ProjectPage.razor`、`ProjectService`、`ProjectController` | 已實作 | 0.4.23 |
| 待辦事項 | [待辦事項](待辦事項-prd.md) | `/todos` | `Pages/Todos/TodoPage.razor`、`TodoService`、`TodoController` | 已實作 | 0.4.32 |
| 使用者管理 | [使用者管理](使用者管理-prd.md) | `/myusers` | `Pages/Admins/MyUserPage.razor`、`MyUserService` | 已實作 | 0.4.23 |
| 角色管理 | [角色管理](角色管理-prd.md) | `/roleviews` | `Pages/Admins/RoleViewPage.razor`、`RoleViewService`、`RbacWriteService` | 已實作 | 0.4.23 |
| 分類清單 | [分類清單](分類清單-prd.md) | `/categories` | `Pages/Categories/CategoryPage.razor`、`CategoryService`、`CategoryController` | 已實作 | 0.4.23 |
| 團隊清單 | [團隊清單](團隊清單-prd.md) | `/teams` | `Pages/Teams/TeamPage.razor`、`TeamService`、`TeamController` | 已實作 | 0.4.23 |
| 會議紀錄提示詞 | [會議紀錄提示詞](會議紀錄提示詞-prd.md) | `/prompttemplates` | `Pages/PromptTemplates/PromptTemplatePage.razor`、`PromptTemplateService`、`PromptTemplateController` | 已實作 | 0.4.26 |
| 會議紀錄（含影音上傳與語音轉文字）| [會議紀錄](會議紀錄-prd.md) | `/meetings` | `Pages/Meetings/MeetingPage.razor`、`MeetingService`、`MeetingFileStore`、`TranscriptionJobRunner`、`MeetingController` | 已實作 | 0.4.27 |
| 系統健康監控 | [系統健康監控](系統健康監控-prd.md) | `/system-health` | `Pages/SystemHealthPage.razor`、Health services | 已實作 | 0.4.23 |
| 紀錄分類與團隊權控 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md) | 跨功能（所有清單查詢／檔案）| `PermissionChecker`、`EffectiveTeamResolver`、`RecordAccessScopeProvider`、`TagStringHelper` | 已實作 | 0.4.24 |

## 二、無選單入口的核心能力

| 能力 | PRD 歸屬 | 現況 |
|------|----------|------|
| 動作級授權（`[HasPermission("resource:action")]`）與管理員短路 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[角色管理](角色管理-prd.md) | 已實作，UI 與 API 共用單一 RBAC 權威 |
| 稽核軌跡（`AuditLog`：登入、使用者/角色/權限異動）| [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md) | 已實作 |
| 帳號安全（PBKDF2、帳號鎖定、TOTP 骨架）| [登入與帳號流程](登入與帳號流程-prd.md) | 已實作；TOTP 預設關閉 |
| 檔案上傳（專案附件）| [專案項目](專案項目-prd.md) | 已實作 |
| 檔案上傳（會議影音檔，含百分比進度列）| [會議紀錄](會議紀錄-prd.md) | 已實作 |
| 背景工作佇列（行程內 `Channel<T>` + 單一 worker `BackgroundService`）| [會議紀錄](會議紀錄-prd.md) | 已實作；目前唯一的使用者是語音轉錄，佇列不持久化 |
| 外部 AI 供應商抽象（`ITranscriptionProvider`）| [會議紀錄](會議紀錄-prd.md) | 已實作；目前只有 Azure OpenAI 一個實作 |

## 三、規劃中產品藍圖

| 藍圖 | 現況界線 |
|------|----------|
| 二階段驗證（TOTP）強制啟用流程 | 資料模型與服務骨架已實作（`MyUser.TwoFactorEnabled/Secret`、`TotpService`），預設關閉，尚未提供強制啟用 UI 流程 |
| AI 從會議紀錄抽出待辦事項 | 未實作。`Todo.MeetingId` 外鍵已備妥，目前所有待辦皆為手動新增，見 [待辦事項](待辦事項-prd.md) |
| 逐字稿的線上編輯、下載與保留期限政策 | 未實作。逐字稿目前為唯讀預覽，沒有下載端點，也沒有自動清理機制 |
| 各能力後續構想 | 見各 PRD 的「規劃中需求」章節，不屬於 0.4.27 驗收範圍 |

## 四、PRD 維護規則

1. 新增選單頁時，PRD、此覆蓋矩陣、`Menu.json` 與 `SidebarMenuService.MenuPermissionMap` 權限鍵必須同步。
2. 新增無頁面核心能力時，仍須指定一份產品能力 PRD，不得只留實作文件。
3. 現行需求以程式碼、設定與測試為準；文件衝突時校正 PRD 並留下 changelog。
4. 每份 PRD 必須標示文件版本、狀態、現行系統版本、首次實作版本及最後核對日期。
5. 未實作內容只能放在獨立「規劃中需求」章節；部分實作須逐項列出完成／未完成。
6. 文件使用 UTF-8 繁體中文含 BOM；提交前執行 `scripts/Test-DocsEncoding.ps1`。

> 返回 [文件總索引](../README.md)
