param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.]*$')]
    [string]$ProjectName,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [string]$SourceProjectName = "MeetingRecord",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)

if ((Test-Path -LiteralPath $destinationFullPath) -and -not $Force) {
    throw "Destination already exists. Use -Force to overwrite: $destinationFullPath"
}

if (Test-Path -LiteralPath $destinationFullPath) {
    Remove-Item -LiteralPath $destinationFullPath -Recurse -Force
}

$excludedDirectories = @(".git", "bin", "obj", ".vs", ".playwright-cli", "output")
$excludedFiles = @("*.user", "*.suo")

New-Item -ItemType Directory -Path $destinationFullPath | Out-Null

Get-ChildItem -LiteralPath $repoRoot -Force | ForEach-Object {
    if ($excludedDirectories -contains $_.Name) {
        return
    }

    Copy-Item -LiteralPath $_.FullName -Destination $destinationFullPath -Recurse -Force -Exclude $excludedFiles
}

$textExtensions = @(
    ".cs", ".csproj", ".slnx", ".json", ".md", ".razor", ".css", ".js",
    ".ps1", ".yml", ".yaml", ".config", ".xml"
)

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $content = $content.Replace($SourceProjectName, $ProjectName)
        if ($_.Name -eq "appsettings.json") {
            $content = $content.Replace("DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars", "$ProjectName-ChangeThisJwtSigningKey-AtLeast32Chars")
            $content = $content.Replace('"SupportPassword": "support"', '"SupportPassword": "change-me"')
        }
        Set-Content -LiteralPath $_.FullName -Value $content -Encoding UTF8
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -Directory |
    Sort-Object FullName -Descending |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

$remainingMatches = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    Select-String -Pattern $SourceProjectName, "DevelopmentOnly-ChangeThisJwtSigningKey", '"SupportPassword": "support"' -SimpleMatch

if ($remainingMatches) {
    Write-Warning "Scaffold completed, but safety checks found values that still need review:"
    $remainingMatches | ForEach-Object {
        Write-Warning "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }
}

Write-Host "Created starter project at $destinationFullPath"
