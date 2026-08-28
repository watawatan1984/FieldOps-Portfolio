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
    "non-engineer Japanese opening" = "FieldOps Portalは、複数の支店で行う「顧客対応」「営業案件」「現場作業」を、1つの画面で確認できる架空の業務システムです"
    "public demo URL" = "公開デモ:\s*\[https://fieldops-portfolio\.onrender\.com\]\(https://fieldops-portfolio\.onrender\.com\)"
    "what this demo can do" = "## このデモでできること"
    "Japanese four roles" = "## 4つの役割[\s\S]*システム管理者[\s\S]*支店管理者[\s\S]*営業担当者[\s\S]*現場担当者"
    "fictional data plain-language note" = "このデモに出てくる会社名、氏名、支店名、現場名、作業記録はすべて架空データです"
    "PC tablet support" = "PCとタブレットでの利用を主な対象"
    "developer table of contents" = "## 開発者向け目次"
    "source repository URL" = "github\.com/watawatan1984/FieldOps-Portfolio"
    "verified live demo URL" = "Live demo:\s*\[fieldops-portfolio\.onrender\.com\]\(https://fieldops-portfolio\.onrender\.com\)"
    "four roles" = "System Administrator[\s\S]*Branch Manager[\s\S]*Sales Representative[\s\S]*Field Technician"
    "one-click login" = "ワンクリックログイン"
    "architecture overview" = "## アーキテクチャとドメイン"
    "local start commands" = "dotnet run --project src/FieldOps\.Web"
    "reset safety" = "## デモ初期化の安全性"
    "test matrix" = "## テストと検証結果"
    "current verification date" = "2026-08-29、Windows / \.NET 10 / PostgreSQL 17 / Chromiumでローカル検証を再実行しました"
    "current test counts" = "Domain tests\s*\|\s*63/63[\s\S]*Integration tests\s*\|\s*212/212[\s\S]*Playwright E2E\s*\|\s*27/27[\s\S]*Full solution\s*\|\s*302/302"
    "load evidence" = "docs/evidence/load-test-results\.md"
    "free-host limitation" = "コールドスタート|scale-to-zero"
    "manual release verification operation" = "CIが成功[\s\S]*Renderの自動デプロイが完了[\s\S]*Release verificationを手動dispatch"
    "Japanese summary" = "## 日本語概要"
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
    if ($readme -notmatch "スクリーンショットは現在未掲載") {
        throw "README must state that screenshots are pending while no approved screenshot files exist."
    }
}
else {
    if ($readme -match "スクリーンショットは現在未掲載") {
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
    Write-Output "Screenshot capture remains pending and is not reported as complete."
}
