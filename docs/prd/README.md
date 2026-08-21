# prd — 產品需求文件主控台

- 文件版本：1.1
- 文件狀態：維護中
- 現行系統版本：0.4.26
- 首次實作版本：0.4.23
- 最後核對日期：2026/08/19

本目錄是產品需求的單一入口。PRD 以**產品能力**為單位；「已實作／部分實作」描述程式現況，「規劃中」必須獨立分區，不代表系統已提供。本專案自 0.4.25 起由通用 Blazor 腳手架轉為「會議紀錄系統」的開發基底，0.4.26 起納入「會議紀錄提示詞」能力，並在 `appsettings.json` 保留 provider-aware 的 `LlmSettings` 強型別設定；但**程式目前尚未呼叫任何 LLM 或語音轉錄 API，亦不含 RAG 能力**——音檔轉錄與自動產出會議紀錄屬規劃中，見 [會議紀錄產生流程](會議紀錄產生流程-prd.md) 與下方第三節藍圖。PRD 內容一律以程式碼、`Menu.json` 與測試為準。

## 一、能力覆蓋矩陣

| 產品能力 | PRD | 入口／路由 | 主要程式來源 | 狀態 | 核對版本 |
|----------|-----|-----------|--------------|------|----------|
| 首頁與導覽 | [首頁與導覽](首頁與導覽-prd.md) | `/`、`/App` | `Pages/Home.razor`、`Pages/HomeAuthed.razor`、`SidebarMenuService`、`Menu.json`、`MainLayout`（含「關於」對話窗） | 已實作 | 0.4.24 |
| 登入與帳號流程 | [登入與帳號流程](登入與帳號流程-prd.md) | `/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Profile`、`/ChangePassword` | `Components/Auths/*`、`MyUserServiceLogin`、`ExternalLoginService`、`AuthController` | 已實作 | 0.4.23 |
| 專案項目 | [專案項目](專案項目-prd.md) | `/projects` | `Pages/Projects/ProjectPage.razor`、`ProjectService`、`ProjectController` | 已實作 | 0.4.23 |
| 使用者管理 | [使用者管理](使用者管理-prd.md) | `/myusers` | `Pages/Admins/MyUserPage.razor`、`MyUserService` | 已實作 | 0.4.23 |
| 角色管理 | [角色管理](角色管理-prd.md) | `/roleviews` | `Pages/Admins/RoleViewPage.razor`、`RoleViewService`、`RbacWriteService` | 已實作 | 0.4.23 |
| 分類清單 | [分類清單](分類清單-prd.md) | `/categories` | `Pages/Categories/CategoryPage.razor`、`CategoryService`、`CategoryController` | 已實作 | 0.4.23 |
| 團隊清單 | [團隊清單](團隊清單-prd.md) | `/teams` | `Pages/Teams/TeamPage.razor`、`TeamService`、`TeamController` | 已實作 | 0.4.23 |
| 會議紀錄提示詞 | [會議紀錄提示詞](會議紀錄提示詞-prd.md) | `/prompttemplates` | `Pages/PromptTemplates/PromptTemplatePage.razor`、`PromptTemplateService`、`PromptTemplateController` | 已實作 | 0.4.26 |
| 系統健康監控 | [系統健康監控](系統健康監控-prd.md) | `/system-health` | `Pages/SystemHealthPage.razor`、Health services | 已實作 | 0.4.23 |
| 紀錄分類與團隊權控 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md) | 跨功能（所有清單查詢／檔案）| `PermissionChecker`、`EffectiveTeamResolver`、`RecordAccessScopeProvider`、`TagStringHelper` | 已實作 | 0.4.24 |

## 二、無選單入口的核心能力

| 能力 | PRD 歸屬 | 現況 |
|------|----------|------|
| 動作級授權（`[HasPermission("resource:action")]`）與管理員短路 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[角色管理](角色管理-prd.md) | 已實作，UI 與 API 共用單一 RBAC 權威 |
| 稽核軌跡（`AuditLog`：登入、使用者/角色/權限異動）| [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md) | 已實作 |
| 帳號安全（PBKDF2、帳號鎖定、TOTP 骨架）| [登入與帳號流程](登入與帳號流程-prd.md) | 已實作；TOTP 預設關閉 |
| 檔案上傳（專案附件）| [專案項目](專案項目-prd.md) | 已實作 |

## 三、規劃中產品藍圖

| 藍圖 | 現況界線 |
|------|----------|
| 二階段驗證（TOTP）強制啟用流程 | 資料模型與服務骨架已實作（`MyUser.TwoFactorEnabled/Secret`、`TotpService`），預設關閉，尚未提供強制啟用 UI 流程 |
| 音檔上傳 → 轉錄 → 套用提示詞 → 產出會議紀錄 | 提示詞範本 CRUD 與 provider-aware `LlmSettings` 強型別設定已實作（0.4.26），**尚未串接任何 LLM／轉錄 API**，系統亦無音檔上傳與會議紀錄 Entity；完整流程、各階段界線與未決議題見 [會議紀錄產生流程](會議紀錄產生流程-prd.md) |
| 各能力後續構想 | 見各 PRD 的「規劃中需求」章節，不屬於 0.4.26 驗收範圍 |

## 四、PRD 維護規則

1. 新增選單頁時，PRD、此覆蓋矩陣、`Menu.json` 與 `SidebarMenuService.MenuPermissionMap` 權限鍵必須同步。
2. 新增無頁面核心能力時，仍須指定一份產品能力 PRD，不得只留實作文件。
3. 現行需求以程式碼、設定與測試為準；文件衝突時校正 PRD 並留下 changelog。
4. 每份 PRD 必須標示文件版本、狀態、現行系統版本、首次實作版本及最後核對日期。
5. 未實作內容只能放在獨立「規劃中需求」章節；部分實作須逐項列出完成／未完成。
6. 文件使用 UTF-8 繁體中文含 BOM；提交前執行 `scripts/Test-DocsEncoding.ps1`。

> 返回 [文件總索引](../README.md)
