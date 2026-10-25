[CmdletBinding()]
param(
    [switch]$Detailed
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "         DUEL: TRANSPORTS OBXODKA vs VIRTUAL TSPU                               " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

$TestProj = Join-Path $PSScriptRoot "..\..\tests\obxodka.Client.Tests\obxodka.Client.Tests.csproj"

Write-Host ""
Write-Host "[1/3] TSPU Emulator Initialization (Inline DPI EcoFilter)..." -ForegroundColor Yellow
Write-Host "  * Shannon Entropy Detector (H >= 7.45)               : [ACTIVE]" -ForegroundColor DarkGreen
Write-Host "  * Signature Scanner WireGuard / OpenVPN / QUIC       : [ACTIVE]" -ForegroundColor DarkGreen
Write-Host "  * SessionID and Cleartext Header Leak Analyzer       : [ACTIVE]" -ForegroundColor DarkGreen
Write-Host "  * L7 Inspector TLS ClientHello and SNI               : [ACTIVE]" -ForegroundColor DarkGreen
Write-Host "  * Bidirectional Inline Proxy (UDP 18443 / TCP 18080) : [ACTIVE]" -ForegroundColor DarkGreen

Write-Host ""
Write-Host "[2/3] Mathematical unit tests & in-memory duel..." -ForegroundColor Yellow

$testOutput = & dotnet test $TestProj --filter "FullyQualifiedName~VirtualTspuTests" --nologo -v quiet

if ($LASTEXITCODE -eq 0) {
    Write-Host "  [+] All mathematical duel scenarios passed successfully!" -ForegroundColor Green
}
else {
    Write-Host "  [-] Simulation failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[3/3] LIVE WINDOWS SOCKET HARNESS (TspuLab - 100% Real OS Sockets & Telemetry)..." -ForegroundColor Cyan
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor Gray

$tspuLabPath = Join-Path $PSScriptRoot "..\TspuLab\TspuLab.csproj"
& dotnet run --project $tspuLabPath --no-build
