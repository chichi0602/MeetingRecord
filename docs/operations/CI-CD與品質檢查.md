# CI-CD 與品質檢查

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.24
- 首次實作版本：0.2.8
- 最後核對日期：2026/08/17

本專案以 **GitHub Actions** 在每次 push 與 PR 時自動建置、測試與品質檢查。工作流程定義於 [`.github/workflows/dotnet-ci.yml`](../../.github/workflows/dotnet-ci.yml)。

---

## 1. 觸發條件

| 事件 | 分支 |
|------|------|
| `push` | `main`、`codex/**` |
| `pull_request` | 目標為 `main` |

---

## 2. 工作流程（job：`build-test`）

執行環境：`windows-latest`，.NET SDK `10.0.x`。依序執行下列步驟，任一失敗即中止並讓 PR 無法合併：

| 步驟 | 指令 / 動作 | 目的 |
|------|-------------|------|
| Checkout | `actions/checkout@v6` | 取出原始碼 |
| Setup .NET | `actions/setup-dotnet@v5`（`10.0.x`） | 安裝 SDK |
| Restore | `dotnet restore src/MeetingRecord/MeetingRecord.slnx` | 還原相依套件 |
| Build | `dotnet build ... --configuration Release --no-restore` | Release 編譯 |
| Test | `dotnet test ... --configuration Release --no-build --verbosity normal` | 執行 xUnit 測試（見 [測試指南](../guides/測試指南.md)） |
| Documentation encoding check | `./scripts/Test-DocsEncoding.ps1`（pwsh） | 檢查 `docs/` 文件編碼 |
| Vulnerability scan | `dotnet list ... package --vulnerable --include-transitive` | 掃描已知弱點套件 |

---

## 3. 文件編碼檢查 ⚠️

`scripts/Test-DocsEncoding.ps1` 會**遞迴**掃描 `docs/` 下所有 `.md`，逐檔驗證：

- **必須含 UTF-8 BOM**（檔頭 `EF BB BF`），缺少即失敗。
- **不得含取代字元**（`U+FFFD`），出現代表編碼轉換時已產生亂碼。

> 注意：檔案移入子目錄後，此腳本以 `-Recurse` 涵蓋所有層級。以 PowerShell 建立／另存文件時請使用含 BOM 的 UTF-8（例如 `Set-Content -Encoding utf8BOM`），避免被擋下。編碼規定詳見 [維護規範 §3](維護規範.md)。

本機可先自行執行：

```powershell
pwsh ./scripts/Test-DocsEncoding.ps1
```

---

## 4. 弱點掃描

`dotnet list package --vulnerable --include-transitive` 會列出含已知弱點的直接與遞移相依套件。為避免大型還原逾時，步驟設定環境變數 `NUGET_HTTP_TIMEOUT_SECONDS=180`。發現弱點時應升級對應套件版本。

> 此步驟僅「列出」弱點，指令回傳 0、**不會讓 CI 失敗**；它與 restore/build 階段的 `NU1903` 稽核警告是兩條獨立路徑。

### 4.1 已知並已抑制的弱點：CVE-2025-6965

| 項目 | 內容 |
|------|------|
| 套件 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（bundled SQLite < 3.50.2） |
| Advisory | [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) / CVE-2025-6965（High，CVSS 7.2） |
| 引入來源 | 由 `Microsoft.EntityFrameworkCore.Sqlite 10.0.5` **遞移**引入（EF Core Sqlite → Microsoft.Data.Sqlite.Core → SQLitePCLRaw.bundle_e_sqlite3 → lib.e_sqlite3） |
| 為何不升級 | NuGet 上 `SQLitePCLRaw.*` 最新即 2.1.11，**尚無修補版**（無 2.1.12 / 2.2.x），EF Core 亦未帶入新版，目前無從升級 |
| 風險評估 | 低：EF Core 採參數化查詢，無未受信任的原始 SQL 進入 SQLite（0.4.24 起 SQLite 為唯一支援的資料庫） |
| 處置 | 於 [`src/MeetingRecord/Directory.Build.props`](../../src/MeetingRecord/Directory.Build.props) 以 `NuGetAuditSuppress` 抑制該 advisory，消除 restore/build 的 `NU1903` 警告 |

**重要行為差異**：`NuGetAuditSuppress` 只抑制 **restore/build 的 `NU1903` 警告**；上方的 `dotnet list package --vulnerable` 步驟為獨立查詢，**仍會列出**此 advisory（屬資訊性輸出，指令仍回傳 0、不阻斷 CI）。

**移除條件**：待 `SQLitePCLRaw`（或 `Microsoft.EntityFrameworkCore.Sqlite`）釋出 bundled SQLite ≥ 3.50.2 的版本後，升級套件、移除 `Directory.Build.props` 內的 `NuGetAuditSuppress`、並刪除本小節。

---

## 5. 延伸閱讀

- [測試指南](../guides/測試指南.md) — 測試類別、本機執行與覆蓋率。
- [維護規範](維護規範.md) — 版本 bump、文件同步與 commit 前檢查清單。
- [正式部署與安全檢查清單](正式部署與安全檢查清單.md) — 上線前必查項目。
