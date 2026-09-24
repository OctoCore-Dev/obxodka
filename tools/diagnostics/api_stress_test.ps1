# api_stress_test.ps1 - Asynchronous Load & Latency Stress Test
param(
    [string]$TargetUrl = "https://obxodka.one/api/Auth/me",
    [int]$TotalRequests = 50,
    [int]$Concurrency = 10
)

Write-Host "=== OBXODKA API LOAD & STRESS TEST ===" -ForegroundColor Cyan
Write-Host "Target: $TargetUrl | Total Requests: $TotalRequests | Concurrency: $Concurrency" -ForegroundColor Yellow

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$results = [System.Collections.Concurrent.ConcurrentBag[long]]::new()
$errors = [System.Collections.Concurrent.ConcurrentBag[string]]::new()

$options = [System.Threading.Tasks.ParallelOptions]@{ MaxDegreeOfParallelism = $Concurrency }

[System.Threading.Tasks.Parallel]::For(0, $TotalRequests, $options, [Action[int]]{
    param($i)
    $reqSw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $handler = [System.Net.Http.SocketsHttpHandler]::new()
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(5)
        $response = $client.GetAsync($TargetUrl).GetAwaiter().GetResult()
        $reqSw.Stop()
        $results.Add($reqSw.ElapsedMilliseconds)
    }
    catch {
        $reqSw.Stop()
        $errors.Add($_.Exception.Message)
    }
})

$sw.Stop()

$latencies = $results.ToArray() | Sort-Object
$successCount = $latencies.Length
$errorCount = $errors.Count

Write-Host "`n--- TEST SUMMARY ---" -ForegroundColor Green
Write-Host "Total Duration: $([math]::Round($sw.ElapsedMilliseconds / 1000, 2))s"
Write-Host "Successful Responses: $successCount"
Write-Host "Failed Requests: $errorCount"

if ($successCount -gt 0) {
    $avg = [math]::Round(($latencies | Measure-Object -Average).Average, 1)
    $p50 = $latencies[[math]::Floor($successCount * 0.50)]
    $p95 = $latencies[[math]::Floor($successCount * 0.95)]
    $p99 = $latencies[[math]::Min([math]::Floor($successCount * 0.99), $successCount - 1)]

    Write-Host "Throughput: $([math]::Round($successCount / ($sw.ElapsedMilliseconds / 1000), 1)) req/sec" -ForegroundColor Cyan
    Write-Host "Latency Avg: ${avg}ms | P50: ${p50}ms | P95: ${p95}ms | P99: ${p99}ms" -ForegroundColor Cyan
}
