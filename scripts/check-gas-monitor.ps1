[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$monitorRoot = Join-Path $repositoryRoot 'ops/google-apps-script/fieldops-health-monitor'
$codePath = Join-Path $monitorRoot 'Code.gs'
$manifestPath = Join-Path $monitorRoot 'appsscript.json'

foreach ($path in @($codePath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required GAS monitor file was not found: $path"
    }
}

$code = Get-Content -LiteralPath $codePath -Raw
$requiredPatterns = [ordered]@{
    'production base URL' = [regex]::Escape('https://fieldops-portfolio.onrender.com')
    'fixed production base URL' = 'baseUrl\s*!==\s*CONFIG\.defaultBaseUrl'
    'safe failure-test URL' = [regex]::Escape('https://fieldops-monitor-test.invalid')
    'live health path' = [regex]::Escape('/health/live')
    'ready health path' = [regex]::Escape('/health/ready')
    'five attempts' = 'retryAttempts:\s*5'
    'fifteen-second delay' = 'retryDelayMs:\s*15000'
    'exact healthy body' = "body\s*===\s*'Healthy'"
    'muted HTTP exceptions' = 'muteHttpExceptions:\s*true'
    'script lock' = 'LockService\.getScriptLock\(\)'
    'ten-minute trigger' = 'everyMinutes\(10\)'
    'monitoring start' = 'monitoringStartMinutes:\s*10\s*\*\s*60'
    'monitoring end' = 'monitoringEndMinutes:\s*18\s*\*\s*60'
    'monitoring window guard' = 'isWithinMonitoringWindow_\('
    'state property' = [regex]::Escape("lastStatusProperty: 'LAST_STATUS'")
    'down timestamp property' = [regex]::Escape("lastDownAtProperty: 'LAST_DOWN_AT'")
    'mail sender' = 'MailApp\.sendEmail\('
    'append-only history' = 'appendRow\('
}

$missing = @()
foreach ($item in $requiredPatterns.GetEnumerator()) {
    if ($code -notmatch $item.Value) {
        $missing += $item.Key
    }
}

if ($missing.Count -gt 0) {
    throw "GAS monitor checks failed: $($missing -join ', ')"
}

$prohibitedPatterns = [ordered]@{
    'hourly trigger' = 'everyHours\('
    'demo login ping' = '/demo-login'
    'cookie handling' = '(?i)cookie'
    'authorization header' = '(?i)authorization'
}

foreach ($item in $prohibitedPatterns.GetEnumerator()) {
    if ($code -match $item.Value) {
        throw "GAS monitor contains prohibited behavior: $($item.Key)"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedScopes = @(
    'https://www.googleapis.com/auth/script.external_request'
    'https://www.googleapis.com/auth/script.scriptapp'
    'https://www.googleapis.com/auth/script.send_mail'
    'https://www.googleapis.com/auth/spreadsheets.currentonly'
    'https://www.googleapis.com/auth/userinfo.email'
) | Sort-Object
$actualScopes = @($manifest.oauthScopes) | Sort-Object
if (($actualScopes -join "`n") -ne ($expectedScopes -join "`n")) {
    throw 'GAS manifest OAuth scopes do not match the approved minimum set.'
}

Write-Output "GAS monitor checks passed ($($requiredPatterns.Count) requirements)."
