[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$lastError = $null

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

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
        $ready = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -UseBasicParsing -TimeoutSec 10
        if ($ready.StatusCode -eq 200) {
            $login = Invoke-WebRequest -Uri "$BaseUrl/demo-login" -UseBasicParsing -TimeoutSec 10
            $loginContent = [System.Net.WebUtility]::HtmlDecode((Get-ResponseText -Response $login))
            if ($login.StatusCode -eq 200 -and $loginContent -match '<title>\s*担当する仕事を選んでください - FieldOps 業務ポータル\s*</title>') {
                Write-Host "FieldOps is ready at $BaseUrl"
                exit 0
            }

            $lastError = "Login page did not contain the expected Japanese FieldOps title."
        }
    }
    catch {
        $lastError = $_.Exception.Message
    }

    Start-Sleep -Seconds 2
}

throw "FieldOps did not become ready within $TimeoutSeconds seconds. Last error: $lastError"
