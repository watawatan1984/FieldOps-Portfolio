param(
    [Parameter(Mandatory)]
    [string]$RunDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$metaPath = Get-ChildItem -LiteralPath $RunDirectory -Filter "*-run-meta.json" | Select-Object -First 1
if ($null -eq $metaPath) {
    throw "Run metadata was not found in $RunDirectory."
}

$meta = Get-Content -LiteralPath $metaPath.FullName -Raw | ConvertFrom-Json
$summary = Get-Content -LiteralPath $meta.summaryPath -Raw | ConvertFrom-Json
$postflight = Get-Content -LiteralPath $meta.postflightPath -Raw | ConvertFrom-Json
$preflight = Get-Content -LiteralPath $meta.preflightPath -Raw | ConvertFrom-Json

$profile = [string]$meta.profile
$durationMetric = "http_req_duration{profile:$profile}"
$durationValues = if ($summary.metrics.PSObject.Properties.Name -contains $durationMetric) {
    $metric = $summary.metrics.$durationMetric
    if ($metric.PSObject.Properties.Name -contains "values") { $metric.values } else { $metric }
} else {
    $metric = $summary.metrics.http_req_duration
    if ($metric.PSObject.Properties.Name -contains "values") { $metric.values } else { $metric }
}
$p95 = [double]$durationValues.'p(95)'
$threshold = if ($profile -eq "baseline") { 1000.0 } else { 2000.0 }
$checkMetric = $summary.metrics.checks
$checkValues = if ($checkMetric.PSObject.Properties.Name -contains "values") { $checkMetric.values } else { $checkMetric }
$failedChecks = if ($checkValues.PSObject.Properties.Name -contains "fails") {
    [int]$checkValues.fails
} else {
    0
}
$httpFailedMetric = $summary.metrics.http_req_failed
$httpFailedValues = if ($httpFailedMetric.PSObject.Properties.Name -contains "values") { $httpFailedMetric.values } else { $httpFailedMetric }
$httpFailedRate = if ($httpFailedValues.PSObject.Properties.Name -contains "rate") {
    [double]$httpFailedValues.rate
} else {
    [double]$httpFailedValues.value
}
$exceptionCount = 0
$statusCounts = @{}
if (Test-Path -LiteralPath $meta.rawPath) {
    Get-Content -LiteralPath $meta.rawPath | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_)) {
            return
        }
        $line = $_ | ConvertFrom-Json
        if ($line.type -eq "Point" -and $line.metric -eq "http_req_duration") {
            $status = [string]$line.data.tags.status
            if (-not [string]::IsNullOrWhiteSpace($status)) {
                if (-not $statusCounts.ContainsKey($status)) {
                    $statusCounts[$status] = 0
                }
                $statusCounts[$status]++
            }
        }
    }
}

if ($summary.metrics.PSObject.Properties.Name -contains "exceptions") {
    $exceptionCount = [int]$summary.metrics.exceptions.values.count
}

$passed = $preflight.ready -eq $true -and
    $postflight.integrity.passed -eq $true -and
    [int]$postflight.activeResetCount -eq 0 -and
    $failedChecks -eq 0 -and
    $httpFailedRate -eq 0.0 -and
    $p95 -le $threshold -and
    $exceptionCount -eq 0

$result = [ordered]@{
    profile = $profile
    status = if ($passed) { "PASS" } else { "FAIL" }
    gitSha = $meta.gitSha
    k6Image = $meta.k6Image
    k6ImageId = $meta.k6ImageId
    k6RepoDigests = $meta.k6RepoDigests
    postgresImage = $meta.postgresImage
    postgresImageId = $meta.postgresImageId
    requestTotals = [ordered]@{
        httpRequests = [int]$(if ($summary.metrics.http_reqs.PSObject.Properties.Name -contains "values") { $summary.metrics.http_reqs.values.count } else { $summary.metrics.http_reqs.count })
        iterations = [int]$(if ($summary.metrics.iterations.PSObject.Properties.Name -contains "values") { $summary.metrics.iterations.values.count } else { $summary.metrics.iterations.count })
    }
    latencyMs = [ordered]@{
        p50 = [double]$durationValues.med
        p95 = $p95
        p99 = [double]$durationValues.'p(99)'
    }
    failedChecks = $failedChecks
    httpFailedRate = $httpFailedRate
    statusDistribution = $statusCounts
    exceptionCount = $exceptionCount
    activeResetCount = [int]$postflight.activeResetCount
    resetCount = [int]$postflight.resetCount
    integrity = $postflight.integrity
    counts = $postflight.counts
}

$summaryPath = Join-Path $RunDirectory "$profile-sanitized-summary.json"
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath

if (-not $passed) {
    Write-Error "Load profile $profile failed validation. See $summaryPath"
}

Write-Output "Load profile $profile passed validation. Summary: $summaryPath"
