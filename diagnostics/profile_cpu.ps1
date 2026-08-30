# profile_cpu.ps1 - Automated CPU Profiling & Flamegraph Collector
Write-Host "=== OBXODKA CPU & FLAMEGRAPH PROFILING ===" -ForegroundColor Cyan

$process = Get-Process -Name "obxodka" -ErrorAction SilentlyContinue

if (-not $process) {
    Write-Warning "Process 'obxodka' is not running. Please start the app before profiling."
    Exit 1
}

$pidNum = $process.Id
$outputTrace = Join-Path $PSScriptRoot "obxodka_cpu_$((Get-Date).ToString('yyyyMMdd_HHmmss'))"

Write-Host "Collecting 15-second CPU trace for PID $pidNum..." -ForegroundColor Yellow
dotnet-trace collect -p $pidNum --duration 00:00:15 --format Speedscope -o "$outputTrace.speedscope.json"

if (Test-Path "$outputTrace.speedscope.json") {
    Write-Host "CPU Flamegraph Trace captured successfully!" -ForegroundColor Green
    Write-Host "Upload '$outputTrace.speedscope.json' to https://www.speedscope.app/ or open in Visual Studio." -ForegroundColor Cyan
} else {
    Write-Error "Failed to capture CPU trace. Ensure 'dotnet-trace' is installed via 'dotnet tool install -g dotnet-trace'."
}
