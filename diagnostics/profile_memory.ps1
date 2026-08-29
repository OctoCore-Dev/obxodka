# profile_memory.ps1 - Automated Memory Leak & GC Diagnostics
Write-Host "=== OBXODKA MEMORY & GC PROFILING ===" -ForegroundColor Cyan

$process = Get-Process -Name "obxodka" -ErrorAction SilentlyContinue

if (-not $process) {
    Write-Warning "Process 'obxodka' is not running. Please start the app before profiling."
    Exit 1
}

$pidNum = $process.Id
Write-Host "Found obxodka PID: $pidNum (WorkingSet: $([math]::Round($process.WorkingSet64 / 1MB, 2)) MB)" -ForegroundColor Green

$outputDump = Join-Path $PSScriptRoot "obxodka_memory_$((Get-Date).ToString('yyyyMMdd_HHmmss')).gcdump"

Write-Host "Capturing GC Dump to $outputDump..." -ForegroundColor Yellow
dotnet-gcdump collect -p $pidNum -o $outputDump

if (Test-Path $outputDump) {
    Write-Host "GC Dump successfully captured: $outputDump" -ForegroundColor Green
    Write-Host "Open in Visual Studio or PerfView to inspect object retention graphs." -ForegroundColor Cyan
} else {
    Write-Error "Failed to capture GC Dump. Ensure 'dotnet-gcdump' is installed via 'dotnet tool install -g dotnet-gcdump'."
}
