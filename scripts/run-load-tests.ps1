param(
    [ValidateSet("baseline", "stress")]
    [string]$Profile,

    [string]$K6Version = "2.2.0",

    [string]$TargetUrl = "http://127.0.0.1:5085",

    [string]$PostgresImage = "postgres:17-alpine",

    [string]$ArtifactsDirectory = "artifacts/load",

    [string]$DurationOverride = "",

    [switch]$ForceThresholdFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-LoopbackTarget {
    param([string]$Value)

    $uri = [Uri]$Value
    if ($uri.Scheme -notin @("http", "https")) {
        throw "Load target must use http or https."
    }

    if ($uri.Host -in @("localhost", "127.0.0.1", "::1", "[::1]")) {
        return
    }

    throw "Refusing load target '$Value'. Task 15 load tests may run only against a loopback target."
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-Checked {
    param([string[]]$Command)

    & $Command[0] @($Command | Select-Object -Skip 1)
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $($Command -join ' ')"
    }
}

Test-LoopbackTarget -Value $TargetUrl
if ($K6Version -ne "2.2.0") {
    throw "Unsupported k6 version '$K6Version'. Task 15 is pinned to grafana/k6:2.2.0."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $repoRoot $ArtifactsDirectory
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$runStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ", [Globalization.CultureInfo]::InvariantCulture)
$runDirectory = Join-Path $artifactRoot "$runStamp-$Profile"
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$postgresPort = Get-FreeTcpPort
$webPort = ([Uri]$TargetUrl).Port
if ($webPort -lt 1) {
    $webPort = Get-FreeTcpPort
    $TargetUrl = "http://127.0.0.1:$webPort"
}

$postgresName = "fieldops-load-postgres-$runStamp-$Profile"
$postgresPassword = "fieldops_load_password"
$connectionString = "Host=127.0.0.1;Port=$postgresPort;Database=fieldops;Username=fieldops;Password=$postgresPassword;Maximum Pool Size=200;Timeout=15;Command Timeout=30"
$webLog = Join-Path $runDirectory "web.log"
$k6Summary = Join-Path $runDirectory "$Profile-summary.json"
$k6Raw = Join-Path $runDirectory "$Profile-raw.jsonl"
$postflight = Join-Path $runDirectory "$Profile-postflight.json"
$preflight = Join-Path $runDirectory "$Profile-preflight.json"
$runMeta = Join-Path $runDirectory "$Profile-run-meta.json"
$webProcess = $null

try {
    Invoke-Checked -Command @("docker", "pull", "grafana/k6:$K6Version")
    Invoke-Checked -Command @("docker", "pull", $PostgresImage)

    Invoke-Checked -Command @(
        "docker", "run", "-d", "--rm",
        "--name", $postgresName,
        "-e", "POSTGRES_DB=fieldops",
        "-e", "POSTGRES_USER=fieldops",
        "-e", "POSTGRES_PASSWORD=$postgresPassword",
        "-p", "127.0.0.1:$postgresPort`:5432",
        $PostgresImage,
        "postgres",
        "-c",
        "max_connections=300")

    $ready = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & docker exec $postgresName pg_isready -U fieldops -d fieldops | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        throw "PostgreSQL did not become ready."
    }

    $projectPath = Join-Path $repoRoot "src/FieldOps.Web/FieldOps.Web.csproj"
    $webInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $webInfo.FileName = "cmd.exe"
    $webInfo.Arguments = "/c dotnet run --project `"$projectPath`" --configuration Release --no-build --no-launch-profile > `"$webLog`" 2>&1"
    $webInfo.UseShellExecute = $false
    $webInfo.CreateNoWindow = $true
    $webInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "LoadTest"
    $webInfo.Environment["ASPNETCORE_URLS"] = $TargetUrl
    $webInfo.Environment["ConnectionStrings__FieldOps"] = $connectionString
    $webInfo.Environment["DemoMode__Enabled"] = "true"
    $webInfo.Environment["DemoMode__DatasetIdentifier"] = "fieldops-portal-fictional-demo"
    $webInfo.Environment["DemoMode__DatasetVersion"] = "1"
    $webProcess = [System.Diagnostics.Process]::Start($webInfo)

    $ready = $false
    for ($attempt = 1; $attempt -le 120; $attempt++) {
        if ($webProcess.HasExited) {
            throw "FieldOps web process exited before readiness. ExitCode=$($webProcess.ExitCode)"
        }

        try {
            $response = Invoke-WebRequest "$TargetUrl/health/ready" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
        }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        throw "FieldOps web did not become ready."
    }

    $vus = if ($Profile -eq "baseline") { 20 } else { 100 }
    Invoke-WebRequest "$TargetUrl/__load-test/preflight?vus=$vus" -Method Post -UseBasicParsing |
        Select-Object -ExpandProperty Content |
        Set-Content -LiteralPath $preflight -NoNewline

    $targetForK6 = $TargetUrl -replace "127\.0\.0\.1", "host.docker.internal" -replace "localhost", "host.docker.internal"
    $scriptPath = "/work/tests/load/$Profile.js"
    $summaryPath = "/work/$($ArtifactsDirectory.Replace('\','/'))/$runStamp-$Profile/$Profile-summary.json"
    $rawPath = "/work/$($ArtifactsDirectory.Replace('\','/'))/$runStamp-$Profile/$Profile-raw.jsonl"
    $durationArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($DurationOverride)) {
        $durationArgs = @("--duration", $DurationOverride)
    }

    $k6Command = @(
        "docker", "run", "--rm",
        "-e", "TARGET_URL=$targetForK6",
        "-e", "FORCE_THRESHOLD_FAILURE=$($ForceThresholdFailure.IsPresent.ToString().ToLowerInvariant())",
        "-v", "$repoRoot`:/work",
        "-w", "/work",
        "grafana/k6:$K6Version",
        "run",
        "--summary-export", $summaryPath,
        "--out", "json=$rawPath") + $durationArgs + @($scriptPath)
    & $k6Command[0] @($k6Command | Select-Object -Skip 1)
    $k6ExitCode = $LASTEXITCODE

    if (Test-Path -LiteralPath $k6Summary) {
        $summaryObject = Get-Content -LiteralPath $k6Summary -Raw | ConvertFrom-Json
        if ($summaryObject.PSObject.Properties.Name -contains "setup_data" -and
            $summaryObject.setup_data.PSObject.Properties.Name -contains "cookieHeader") {
            $summaryObject.setup_data.cookieHeader = "[redacted]"
            $summaryObject | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $k6Summary
        }
    }

    Invoke-WebRequest "$TargetUrl/__load-test/postflight" -UseBasicParsing |
        Select-Object -ExpandProperty Content |
        Set-Content -LiteralPath $postflight -NoNewline

    if ($k6ExitCode -ne 0) {
        throw "Command failed with exit code $k6ExitCode`: $($k6Command -join ' ')"
    }

    $imageId = docker image inspect "grafana/k6:$K6Version" --format "{{.Id}}"
    $repoDigest = docker image inspect "grafana/k6:$K6Version" --format "{{json .RepoDigests}}"
    $postgresImageId = docker image inspect $PostgresImage --format "{{.Id}}"
    $gitSha = git -C $repoRoot rev-parse HEAD
    [ordered]@{
        profile = $Profile
        startedAtUtc = $runStamp
        gitSha = $gitSha
        targetUrl = $TargetUrl
        k6Image = "grafana/k6:$K6Version"
        k6ImageId = $imageId
        k6RepoDigests = $repoDigest
        postgresImage = $PostgresImage
        postgresImageId = $postgresImageId
        summaryPath = $k6Summary
        rawPath = $k6Raw
        preflightPath = $preflight
        postflightPath = $postflight
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $runMeta

    & (Join-Path $PSScriptRoot "summarize-load-results.ps1") -RunDirectory $runDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Load summary validation failed."
    }
}
finally {
    if ($webProcess -ne $null -and -not $webProcess.HasExited) {
        & taskkill /PID $webProcess.Id /T /F 2>$null | Out-Null
        $webProcess.WaitForExit()
    }
    if ($webProcess -ne $null) {
        $webProcess.Dispose()
    }
    & docker rm -f $postgresName 2>$null | Out-Null
}
