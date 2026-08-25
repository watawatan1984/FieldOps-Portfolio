[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$readmePath = Join-Path $repositoryRoot "README.md"

if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    throw "README.md was not found."
}

$readme = Get-Content -LiteralPath $readmePath -Raw
$requiredPatterns = [ordered]@{
    "fictional reconstruction disclosure" = "架空の再構成|fictional reconstruction"
    "source repository URL" = "github\.com/watawatan1984/FieldOps-Portfolio"
    "live demo status" = "Live demo:.*未デプロイ"
    "four roles" = "System Administrator[\s\S]*Branch Manager[\s\S]*Sales Representative[\s\S]*Field Technician"
    "one-click login" = "ワンクリックログイン"
    "architecture overview" = "## アーキテクチャとドメイン"
    "local start commands" = "dotnet run --project src/FieldOps\.Web"
    "reset safety" = "## デモ初期化の安全性"
    "test matrix" = "## テストと検証結果"
    "load evidence" = "docs/evidence/load-test-results\.md"
    "free-host limitation" = "コールドスタート|scale-to-zero"
    "English summary" = "## English summary"
    "license status" = "ライセンスはまだ付与していません"
}

$missing = @()
foreach ($item in $requiredPatterns.GetEnumerator()) {
    if ($readme -notmatch $item.Value) {
        $missing += $item.Key
    }
}

if ($missing.Count -gt 0) {
    throw "README acceptance checks failed: $($missing -join ', ')"
}

if ($readme -match "real employer|production source|bug.?free|zero bugs") {
    throw "README contains a prohibited or misleading quality/source claim."
}

$screenshotDirectory = Join-Path $repositoryRoot "docs/evidence/screenshots"
$screenshotFiles = @()
if (Test-Path -LiteralPath $screenshotDirectory -PathType Container) {
    $screenshotFiles = @(
        Get-ChildItem -LiteralPath $screenshotDirectory -File |
            Where-Object { $_.Extension -in @(".png", ".jpg", ".jpeg", ".webp") }
    )
}

if ($screenshotFiles.Count -eq 0) {
    if ($readme -notmatch "スクリーンショット収集はTask 17の未完了項目です" -or $readme -notmatch "現在は未掲載") {
        throw "README must state that screenshots are pending while no approved screenshot files exist."
    }
}
else {
    if ($readme -match "スクリーンショット収集はTask 17の未完了項目です|現在は未掲載") {
        throw "README still reports screenshots as pending after approved files were added."
    }

    foreach ($screenshot in $screenshotFiles) {
        $relativePath = "docs/evidence/screenshots/$($screenshot.Name)"
        $markdownLinkPattern = '!?' + '\[[^\]]*\]\(' + [regex]::Escape($relativePath) + '\)'
        if ($readme -notmatch $markdownLinkPattern) {
            throw "README does not link to approved screenshot: $relativePath"
        }
    }
}

Write-Output "README content checks passed ($($requiredPatterns.Count) requirements)."
if ($screenshotFiles.Count -eq 0) {
    Write-Output "Task 17 screenshot capture remains pending and is not reported as complete."
}
