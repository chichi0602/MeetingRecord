# 部署後的上傳實測（1.0.64）：對已經部署好的網站（例如 IIS 正式機）實際上傳一個大檔，確認分塊上傳可用。
#
# 不留任何資料、不觸發付費 AI：在專案的編輯視窗選附件（選檔當下就會以 HTTP 分塊傳到伺服器暫存），
# 等「待上傳檔案」出現後取消並放棄，暫存檔隨之刪除，專案沒有任何變更。
#
# 需求：本機已安裝 Microsoft Edge；帳號要看得到至少一個專案、且有該專案的編輯權限。
# 密碼互動輸入，不會出現在命令列歷史（自動化執行時可預先設環境變數 MEETINGRECORD_SMOKE_PASSWORD）。
#
# 用法：
#   pwsh scripts/Test-IisUpload.ps1 -BaseUrl https://meeting.example.com -Account support
#   pwsh scripts/Test-IisUpload.ps1 -BaseUrl https://meeting.example.com -Account alice -SizeMB 500 -Project "週會專案"
#   加 -Headed 開出瀏覽器視窗看它操作；內部自簽憑證加 -IgnoreTlsErrors。
#
# 結果：
#   通過 → 印出檔案大小、分塊數、耗時與平均速度。
#   405  → IIS 的 WebDAV 模組攔截了 PUT，依《正式部署與安全檢查清單》處理。
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Account,
    [int]$SizeMB = 200,
    [string]$Project = "",
    [switch]$Headed,
    [switch]$IgnoreTlsErrors
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
# ⚠️ PowerShell 變數不分大小寫，不能叫 $project（會蓋掉參數 -Project）。
$testProject = Join-Path $repoRoot "src/MeetingRecord/MeetingRecord.E2ETests/MeetingRecord.E2ETests.csproj"

# 密碼：已經用環境變數 MEETINGRECORD_SMOKE_PASSWORD 提供（例如自動化排程）就直接用，否則互動輸入。
if ($env:MEETINGRECORD_SMOKE_PASSWORD) {
    $password = $env:MEETINGRECORD_SMOKE_PASSWORD
}
else {
    $securePassword = Read-Host -AsSecureString "請輸入 $Account 的密碼"
    $password = ConvertFrom-SecureString -SecureString $securePassword -AsPlainText
}

$env:MEETINGRECORD_SMOKE_URL = $BaseUrl
$env:MEETINGRECORD_SMOKE_ACCOUNT = $Account
$env:MEETINGRECORD_SMOKE_PASSWORD = $password
$env:MEETINGRECORD_SMOKE_SIZE_MB = "$SizeMB"
if ($Project) { $env:MEETINGRECORD_SMOKE_PROJECT = $Project }
if ($Headed) { $env:SMOKE_HEADED = "1" }
if ($IgnoreTlsErrors) { $env:MEETINGRECORD_SMOKE_IGNORE_TLS = "1" }

try {
    Write-Host "== 對 $BaseUrl 上傳 $SizeMB MB 測試檔（不會存任何資料）"
    dotnet test $testProject -c Release --nologo --filter "Category=DeploymentSmoke" --logger "console;verbosity=detailed"
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:MEETINGRECORD_SMOKE_URL, Env:MEETINGRECORD_SMOKE_ACCOUNT, Env:MEETINGRECORD_SMOKE_PASSWORD, `
        Env:MEETINGRECORD_SMOKE_SIZE_MB, Env:MEETINGRECORD_SMOKE_PROJECT, Env:SMOKE_HEADED, Env:MEETINGRECORD_SMOKE_IGNORE_TLS `
        -ErrorAction SilentlyContinue
    $password = $null
}

if ($exitCode -ne 0) {
    throw "上傳實測沒有通過，原因見上方輸出。"
}
