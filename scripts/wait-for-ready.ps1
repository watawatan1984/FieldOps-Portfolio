[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:8080",
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$lastError = $null

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
        $ready = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -UseBasicParsing -TimeoutSec 10
        if ($ready.StatusCode -eq 200) {
            $login = Invoke-WebRequest -Uri "$BaseUrl/demo-login" -UseBasicParsing -TimeoutSec 10
            if ($login.StatusCode -eq 200 -and $login.Content -match '<title>\s*Demo sign in - FieldOps Portal\s*</title>') {
                Write-Host "FieldOps is ready at $BaseUrl"
                exit 0
            }

            $lastError = "Login page did not contain the expected FieldOps Portal title."
        }
    }
    catch {
        $lastError = $_.Exception.Message
    }

    Start-Sleep -Seconds 2
}

throw "FieldOps did not become ready within $TimeoutSeconds seconds. Last error: $lastError"
