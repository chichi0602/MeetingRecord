# 第一次 Migration 

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/10/08

本系統**只支援 SQLite**（0.4.24 起已移除 SQL Server 軌道），所有 migration 都放在 `MeetingRecord.AccessDatas/Migrations/`，DbContext 只有一個：`BackendDBContext`。

| 參數 | 本專案的值 |
|------|-----------|
| 目標專案（`-Project`／`--project`）| `MeetingRecord.AccessDatas`（含 `BackendDBContext`、`Migrations/` 與 `Microsoft.EntityFrameworkCore.Tools`）|
| 啟動專案（`-StartupProject`／`--startup-project`）| `MeetingRecord.Web`（含 `Microsoft.EntityFrameworkCore.Design`，DI 註冊在 `ServiceCollectionExtensions.AddConfiguredDatabase()`）|
| DbContext（`-Context`／`--context`）| `BackendDBContext`（只有一個，可省略）|

> 下列 `dotnet ef` 指令的相對路徑以 `src/MeetingRecord/` 為工作目錄；Package Manager Console 請先把預設專案選為 `MeetingRecord.AccessDatas`。
> 設計階段沒有 `IDesignTimeDbContextFactory`，EF 工具會啟動 `MeetingRecord.Web` 的 host 取得 DbContext，連線字串因此來自 `appsettings.json` 的 `SystemSettings:ExternalFileSystem:DatabasePath`（`<DatabasePath>/BackendDB.db`）。

# 新增 Migration

改完 `MeetingRecord.AccessDatas/Models/` 的 Entity 或 `BackendDBContext.OnModelCreating` 後，產生一個以 PascalCase 描述異動的 migration。以下以既有的 `AddMustChangePassword`（0.4.113，`MyUser` 加「首次登入須改密碼」欄位）為例：

```
Add-Migration AddMustChangePassword -Context BackendDBContext -Project MeetingRecord.AccessDatas -StartupProject MeetingRecord.Web
```

```
dotnet ef migrations add AddMustChangePassword --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
```

產出 `Migrations/<時間戳>_AddMustChangePassword.cs`、`.Designer.cs`，並更新 `BackendDBContextModelSnapshot.cs`，三者都要進版控。命名可參考 `Migrations/` 內既有檔案（例：`AddProjectPrimaryTeamAndCategories`、`RemoveLegacyTeamTags`）。

⚠️ SQLite 不支援以 `ALTER TABLE` 加外鍵、刪欄等操作，EF Core 會改以 **table-rebuild** 產生 migration；請檢查產出的 `Up`／`Down` 是否會丟資料（說明見 [資料模型與資料庫](../architecture/資料模型與資料庫.md) §4）。

常用參數：

* -Context <String>

  The DbContext class to use. Class name only or fully qualified with namespaces. If this parameter is omitted, EF Core finds the context class. If there are multiple context classes, this parameter is required.
* -Project <String>

  The target project. If this parameter is omitted, the Default project for Package Manager Console is used as the target project.
* -StartupProject <String>

  The startup project. If this parameter is omitted, the Startup project in Solution properties is used as the target project.
* -Args <String>

  Arguments passed to the application.
* -Verbose

  Show verbose output.

# 套用到資料庫

**一般情況不需要手動套用**：應用程式啟動時，`Program.cs` 的 `#region 資料庫的 Migration` 會呼叫 `dbContext.Database.Migrate()` 自動套用尚未執行的 migration（專案若完全沒有 migration 才改走 `EnsureCreated()`）。部署新版本時直接啟動即可。

開發時若想不啟動網站就先套用：

```
Update-Database -Context BackendDBContext -Project MeetingRecord.AccessDatas -StartupProject MeetingRecord.Web
```

```
dotnet ef database update --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
```

# 移除 Migration

只能移除**最後一個且尚未套用到任何資料庫**的 migration；已套用的請先 `Update-Database <前一個 migration 名稱>` 退回。

```
Remove-Migration -Context BackendDBContext -Project MeetingRecord.AccessDatas -StartupProject MeetingRecord.Web
```

```
dotnet ef migrations remove --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
```

# 產生 SQL 腳本

只輸出 SQL、不實際執行（例如要人工審閱 table-rebuild 的內容）：

```
Script-Migration -Project MeetingRecord.AccessDatas -StartupProject MeetingRecord.Web
```

```
dotnet ef migrations script --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
```
