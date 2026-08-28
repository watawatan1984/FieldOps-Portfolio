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

function Get-ResponseText {
    param([Parameter(Mandatory)]$Response)

    if ($null -ne $Response.RawContentStream) {
        if ($Response.RawContentStream.CanSeek) {
            $Response.RawContentStream.Position = 0
        }

        $reader = [System.IO.StreamReader]::new($Response.RawContentStream, [System.Text.Encoding]::UTF8, $true, 1024, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }

    return [string]$Response.Content
}

foreach ($path in '/health/live', '/health/ready') {
    $response = Invoke-WebRequest -Uri "$BaseUrl$path" -WebSession $session -UseBasicParsing -TimeoutSec 30
    if ($response.StatusCode -ne 200) {
        throw "$path returned HTTP $($response.StatusCode)."
    }
}

$login = Invoke-WebRequest -Uri "$BaseUrl/demo-login" -WebSession $session -UseBasicParsing -TimeoutSec 30
$loginContent = [System.Net.WebUtility]::HtmlDecode((Get-ResponseText -Response $login))
$escapedRole = [Regex]::Escape($Role)
$formMatch = [Regex]::Matches($loginContent, '(?s)<form[^>]*>.*?</form>') |
    Where-Object { $_.Value -match "data-role=`"$escapedRole`"" } |
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

$dashboardContent = [System.Net.WebUtility]::HtmlDecode((Get-ResponseText -Response $dashboard))
if ($dashboard.StatusCode -ne 200 -or $dashboardContent -notmatch '>今日やること<') {
    throw "$Role login did not reach the Japanese home page."
}

$journey = Invoke-WebRequest -Uri "$BaseUrl/work-orders" -WebSession $session -UseBasicParsing -TimeoutSec 30
$journeyContent = [System.Net.WebUtility]::HtmlDecode((Get-ResponseText -Response $journey))
if ($journey.StatusCode -ne 200 -or $journeyContent -notmatch '作業予定') {
    throw "$Role read-only Japanese work-order journey failed."
}

Write-Host "Public smoke passed for $Role at $BaseUrl"
