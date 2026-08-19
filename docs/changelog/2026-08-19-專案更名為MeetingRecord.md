# 專案更名：MyProject → MeetingRecord（0.4.25）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.25
- 首次實作版本：0.4.25
- 最後核對日期：2026/08/19

## 目的

本 repo 原為 .NET 10 Blazor Server 的**開發啟動範本**，所有專案、命名空間與組件名一律使用佔位符 `MyProject`。現決定不再把它當通用範本維護，而是**就地**轉為「會議紀錄系統」的實際開發基底，因此把佔位符全面換成正式名稱 `MeetingRecord`。

> 使用者原始輸入為 `MettingRecord`（英文誤拼），確認後採用正確拼法 **`MeetingRecord`**。

本次為**純更名**，不新增任何會議紀錄領域的功能或 Entity。

## 變更範圍

### 一、命名空間與組件名

`MyProject` → `MeetingRecord` 全面字面取代，共 **248 個受追蹤檔案**：

| 類型 | 檔數 | 內容 |
|------|------|------|
| `.cs` | 175 | `namespace` / `using`；含 `Migrations/` 的 Designer 與 ModelSnapshot 內的實體型別字串 |
| `docs/*.md` | 45 | 架構圖、路徑範例、程式碼片段 |
| `.razor` | 8 | `@using`、`Components/_Imports.razor` |
| `.yml` | 7 | CI workflow 的 slnx 路徑、`.playwright-cli/` 舊頁面快照 |
| `.csproj` / `.slnx` | 7 | `ProjectReference` 路徑、方案內 `Path` |
| 其他 `.md` | 5 | `readme.md`、`CLAUDE.md`、`AGENTS.md` 等 |
| `.ps1` | 2 | `New-CrudModule.ps1` 樣板命名空間、`New-StarterProject.ps1` 的 `$SourceProjectName` 預設值 |
| `.json` | 1 | `appsettings.json` |

各專案皆無明確 `<RootNamespace>` / `<AssemblyName>`，兩者由 csproj 檔名推導，故改檔名即完成組件更名。

### 二、檔案與目錄改名（以 `git mv` 保留歷史）

```
src/MyProject/                          →  src/MeetingRecord/
src/MyProject/MyProject.slnx            →  src/MeetingRecord/MeetingRecord.slnx
src/MyProject/MyProject.<Layer>/        →  src/MeetingRecord/MeetingRecord.<Layer>/
src/MyProject/…/MyProject.<Layer>.csproj →  …/MeetingRecord.<Layer>.csproj
```

`<Layer>` = `Web`、`Business`、`AccessDatas`、`Models`、`Dtos`、`Share`、`Tests`（共 7 個目錄 + 7 個 csproj + 1 個 slnx）。

**刻意不動**：`MeetingRecord.Web.csproj` 的 `UserSecretsId` GUID（保留既有 user-secrets）、`.slnx` 內各專案的 `Id` GUID。

### 三、執行期設定值（`appsettings.json`）

| 鍵 | 原值 | 新值 |
|----|------|------|
| `CacheSettings.InstanceName` | `MyProject:` | `MeetingRecord:` |
| `JwtSettings.Issuer` | `MyProject` | `MeetingRecord` |
| `JwtSettings.Audience` | `MyProject.WebApi` | `MeetingRecord.WebApi` |
| `ExternalFileSystem.DatabasePath` | `C:\temp\MyProject\DB` | `C:\temp\MeetingRecord\DB` |
| `ExternalFileSystem.DownloadPath` | `C:\temp\MyProject\Download` | `C:\temp\MeetingRecord\Download` |
| `ExternalFileSystem.UploadPath` | `C:\temp\MyProject\Upload` | `C:\temp\MeetingRecord\Upload` |
| `ExternalFileSystem.ProjectFilePath` | `C:\temp\MyProject\ProjectFile` | `C:\temp\MeetingRecord\ProjectFile` |
| `SystemInformation.SystemName` | `Blazor開發啟動範本專案` | `會議紀錄系統` |
| `SystemInformation.SystemDescription` | `Blazor開發啟動範本專案` | `會議紀錄系統` |
| `SystemInformation.SystemVersion` | `0.4.24 (2026/08/17)` | `0.4.25 (2026/08/19)` |

## 驗證

改名前先取得綠燈基準線，改名後逐項對照：

| 檢查 | 改名前 | 改名後 |
|------|--------|--------|
| `dotnet build -c Release` | 0 錯誤 / 4 警告 | 0 錯誤 / 4 警告（皆為既有 `Microsoft.OpenApi` NU1903，與本次無關）|
| `dotnet test -c Release` | 131 通過 / 0 失敗 | **131 通過 / 0 失敗** |
| `scripts/Test-DocsEncoding.ps1` | 通過 | 66 份 `.md` 全部 OK（BOM 未破壞）|
| 全樹殘留掃描（排除 `.git`／`bin`／`obj`）| — | `MyProject` 檔名與內容皆 **0 筆** |

取代採「讀 bytes → 判斷 BOM → 以相同 BOM 旗標寫回」，確保 `docs/*.md` 的 UTF-8 BOM 與各檔 CRLF 換行完全不受影響。

## 注意事項

- **本機資料需手動搬移**：`ExternalFileSystem` 四個路徑已改指向 `C:\temp\MeetingRecord\*`。既有的 `BackendDB.db` 與已上傳檔案仍留在 `C:\temp\MyProject\*`，若不搬移，程式會在新路徑建立空的 SQLite DB 並重跑 Seed。保留舊資料請先把整個 `C:\temp\MyProject\` 複製為 `C:\temp\MeetingRecord\`。
- **既有 JWT 全部失效**：`Issuer` / `Audience` 已變更，先前簽發的 access / refresh token 驗證會失敗，需重新登入。
- **Redis 舊鍵成為孤兒**：快取鍵前綴由 `MyProject:` 變為 `MeetingRecord:`（預設 `Memory` provider 不受影響）。
- **EF Core Migrations 相容**：`__EFMigrationsHistory` 只存 MigrationId，不含命名空間，既有 DB 仍可正常套用；Designer / ModelSnapshot 內的型別字串一併更名，**無須重建 migration**。
- **需重開 IDE**：舊 `.slnx` 路徑已不存在，請改開 `src/MeetingRecord/MeetingRecord.slnx`。
- **git repo 目錄名不變**：仍為 `NET10-Blazor-Starter`，本次未更動（涉及本機路徑與 remote 設定，另行決定）。

## 文件同步

- **新增**：本篇。
- **更新**：`readme.md`、`docs/README.md`、`docs/changelog/README.md`（索引）；`docs/operations/日誌與設定檔說明.md`（`SystemName` / `SystemDescription` 範例值）；`docs/architecture/開發慣例與限制速查.md`、`docs/architecture/架構總覽.md`、`docs/README.md`（標頭「現行系統版本 / 最後核對日期」）。其餘文件僅隨全域取代更新路徑與命名空間字樣。
- **未改寫**：`readme.md` 的產品定位敘述（`§1 專案介紹` 等段落仍以「Blazor 開發啟動範本」自述）。若要整份重新定位為「會議紀錄系統」，屬另一次異動。
- `appsettings.json` `SystemVersion` 0.4.24 → 0.4.25。
