# security_audit.ps1 - Automated NuGet Dependency Security Scanner
Write-Host "=== OBXODKA SECURITY & VULNERABILITY AUDIT (CVE) ===" -ForegroundColor Cyan

$solutionPath = Join-Path $PSScriptRoot "..\obxodka.slnx"

Write-Host "Auditing all solution dependencies against known CVE databases..." -ForegroundColor Yellow
dotnet list $solutionPath package --vulnerable --include-transitive

Write-Host "`nAudit complete!" -ForegroundColor Green
