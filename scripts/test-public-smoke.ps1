[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseUrl,
    [ValidateSet('System Administrator', 'Branch Manager', 'Sales Representative', 'Field Technician')]
    [string]$Role = 'Field Technician',
    [switch]$AllowLocalHttp
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$parsedBaseUrl = [Uri]$BaseUrl
$isLoopbackHttp = $parsedBaseUrl.Scheme -eq 'http' -and $parsedBaseUrl.IsLoopback
if ($parsedBaseUrl.Scheme -ne 'https' -and (-not $AllowLocalHttp -or -not $isLoopbackHttp)) {
    throw 'Public smoke requires HTTPS. HTTP is permitted only for an explicit loopback test.'
}

$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()

foreach ($path in '/health/live', '/health/ready') {
    $response = Invoke-WebRequest -Uri "$BaseUrl$path" -WebSession $session -UseBasicParsing -TimeoutSec 30
    if ($response.StatusCode -ne 200) {
        throw "$path returned HTTP $($response.StatusCode)."
    }
}

$login = Invoke-WebRequest -Uri "$BaseUrl/demo-login" -WebSession $session -UseBasicParsing -TimeoutSec 30
$escapedRole = [Regex]::Escape($Role)
$formMatch = [Regex]::Matches($login.Content, '(?s)<form[^>]*>.*?</form>') |
    Where-Object { $_.Value -match "Continue as $escapedRole" } |
    Select-Object -First 1
if ($null -eq $formMatch) {
    throw "Could not find the $Role demo-login form."
}

$roleTokenMatch = [Regex]::Match(
    $formMatch.Value,
    '<input[^>]+name="roleToken"[^>]+value="(?<token>[^"]+)"[^>]*>')
$antiForgeryMatch = [Regex]::Match(
    $formMatch.Value,
    '<input[^>]+name="__RequestVerificationToken"[^>]+value="(?<token>[^"]+)"[^>]*>')
if (-not $roleTokenMatch.Success -or -not $antiForgeryMatch.Success) {
    throw 'Could not find the demo-login form tokens.'
}

$body = @{
    roleToken = [System.Net.WebUtility]::HtmlDecode($roleTokenMatch.Groups['token'].Value)
    __RequestVerificationToken = [System.Net.WebUtility]::HtmlDecode($antiForgeryMatch.Groups['token'].Value)
}
if ($isLoopbackHttp) {
    try {
        Invoke-WebRequest -Uri "$BaseUrl/demo-login" -Method Post -Body $body -WebSession $session -UseBasicParsing -TimeoutSec 30 -MaximumRedirection 0 | Out-Null
    }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 302) {
            throw
        }
    }

    $secureCookieUri = [UriBuilder]::new($parsedBaseUrl)
    $secureCookieUri.Scheme = 'https'
    $identityCookie = $session.Cookies.GetCookies($secureCookieUri.Uri) |
        Where-Object { $_.Name -eq '.AspNetCore.Identity.Application' } |
        Select-Object -First 1
    if ($null -eq $identityCookie) {
        throw 'Demo login did not issue an authentication cookie.'
    }

    $identityCookie.Secure = $false
    $session.MaximumRedirection = 10
    $dashboard = Invoke-WebRequest -Uri "$BaseUrl/" -WebSession $session -UseBasicParsing -TimeoutSec 30
}
else {
    $dashboard = Invoke-WebRequest -Uri "$BaseUrl/demo-login" -Method Post -Body $body -WebSession $session -UseBasicParsing -TimeoutSec 30
}

if ($dashboard.StatusCode -ne 200 -or $dashboard.Content -notmatch '>Dashboard<') {
    throw "$Role login did not reach the dashboard."
}

$journey = Invoke-WebRequest -Uri "$BaseUrl/work-orders" -WebSession $session -UseBasicParsing -TimeoutSec 30
if ($journey.StatusCode -ne 200 -or $journey.Content -notmatch 'Work orders') {
    throw "$Role read-only work-order journey failed."
}

Write-Host "Public smoke passed for $Role at $BaseUrl"
