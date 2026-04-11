# demo.ps1 - WorkerSagaDemo end-to-end demo script
$ErrorActionPreference = "Stop"
$baseUrl = "http://localhost:5041"

Write-Host "`n=== WorkerSagaDemo - Job Saga Demo ===" -ForegroundColor Cyan

# 1. Check if API is reachable
Write-Host "`n[1/4] Checking API availability..." -ForegroundColor Yellow
try {
    $null = Invoke-RestMethod -Uri "$baseUrl/jobs/$([Guid]::NewGuid())" -Method Get -ErrorAction SilentlyContinue
} catch {
    if ($_.Exception.Response.StatusCode -eq 404) {
        Write-Host "  API is reachable." -ForegroundColor Green
    } else {
        Write-Host "  ERROR: API is not reachable at $baseUrl" -ForegroundColor Red
        Write-Host "  Make sure the API is running: cd src\WorkerSagaDemo.Api && dotnet run" -ForegroundColor Red
        exit 1
    }
}

# 2. Create a job
Write-Host "`n[2/4] Creating a new job..." -ForegroundColor Yellow
$createResult = Invoke-RestMethod -Uri "$baseUrl/jobs" -Method Post -ContentType "application/json"
$jobId = $createResult.id
Write-Host "  Job created: $jobId" -ForegroundColor Green
Write-Host "  Initial status: $($createResult.status)" -ForegroundColor White

# 3. Poll for status changes
Write-Host "`n[3/4] Polling job status (every 1s)..." -ForegroundColor Yellow
$lastStatus = ""
$maxPolls = 60
$pollCount = 0
$stepTimings = @{}

while ($pollCount -lt $maxPolls) {
    Start-Sleep -Seconds 1
    $pollCount++

    try {
        $job = Invoke-RestMethod -Uri "$baseUrl/jobs/$jobId" -Method Get
    } catch {
        Write-Host "  Poll $pollCount - Error fetching job" -ForegroundColor Red
        continue
    }

    if ($job.status -ne $lastStatus) {
        $timestamp = Get-Date -Format "HH:mm:ss"
        Write-Host "  [$timestamp] Status: $lastStatus -> $($job.status)" -ForegroundColor White
        $lastStatus = $job.status
    }

    # Track step timings
    foreach ($step in $job.steps) {
        if ($step.status -eq "Completed" -and $step.completedAt -and -not $stepTimings.ContainsKey($step.name)) {
            $stepTimings[$step.name] = @{
                StartedAt = $step.startedAt
                CompletedAt = $step.completedAt
            }
        }
    }

    if ($job.status -eq "Completed" -or $job.status -eq "Failed") {
        break
    }
}

# 4. Print summary
Write-Host "`n[4/4] Job Summary" -ForegroundColor Yellow
Write-Host "  ============================================" -ForegroundColor White
Write-Host "  Job ID:    $jobId" -ForegroundColor White
Write-Host "  Status:    $($job.status)" -ForegroundColor $(if ($job.status -eq "Completed") { "Green" } else { "Red" })
Write-Host "  Created:   $($job.createdAt)" -ForegroundColor White
if ($job.completedAt) {
    Write-Host "  Completed: $($job.completedAt)" -ForegroundColor White
}
Write-Host ""
Write-Host "  Steps:" -ForegroundColor White
foreach ($step in $job.steps) {
    $icon = if ($step.status -eq "Completed") { "[OK]" } elseif ($step.status -eq "InProgress") { "[..]" } else { "[  ]" }
    $color = if ($step.status -eq "Completed") { "Green" } elseif ($step.status -eq "InProgress") { "Yellow" } else { "Gray" }
    $duration = ""
    if ($step.startedAt -and $step.completedAt) {
        $start = [DateTimeOffset]::Parse($step.startedAt)
        $end = [DateTimeOffset]::Parse($step.completedAt)
        $duration = " ({0:N1}s)" -f ($end - $start).TotalSeconds
    }
    Write-Host "    $icon $($step.name) - $($step.status)$duration" -ForegroundColor $color
}
Write-Host "  ============================================" -ForegroundColor White

if ($job.status -eq "Completed") {
    Write-Host "`n  Demo completed successfully!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n  Demo finished with status: $($job.status)" -ForegroundColor Red
    exit 1
}
