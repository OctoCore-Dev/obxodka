<#
.SYNOPSIS
    Проверка доступности узлов, сертификатов TLS и подсетей CDN из реальных сетей РФ (Globalping API).
.DESCRIPTION
    Позволяет тестировать прохождение TLS 1.3, отсутствие подмены сертификата и скачивание >24 КБ
    через живые зонды провайдеров РФ (Москва, Санкт-Петербург, Казань, Екатеринбург, Новосибирск)
    прямо с рабочей машины за пределами РФ.
#>
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Targets = @(
        "https://104.18.32.7/cdn-cgi/trace",
        "https://162.159.135.42/cdn-cgi/trace",
        "https://172.64.155.2/cdn-cgi/trace",
        "https://198.41.200.53/cdn-cgi/trace"
    ),

    [Parameter(Mandatory = $false)]
    [string]$HostHeader = "",

    [Parameter(Mandatory = $false)]
    [int]$LocationsLimit = 5
)

$ErrorActionPreference = "Stop"
$apiBase = "https://api.globalping.io/v1/measurements"

foreach ($targetUrl in $Targets) {
    $uri = [System.Uri]$targetUrl
    $port = if ($uri.Port -gt 0) { $uri.Port } else { 443 }
    $effectiveHost = if ([string]::IsNullOrWhiteSpace($HostHeader)) { $uri.Host } else { $HostHeader }
    $path = if ([string]::IsNullOrEmpty($uri.PathAndQuery)) { "/" } else { $uri.PathAndQuery }

    $payload = @{
        type      = "http"
        target    = $uri.Host
        limit     = $LocationsLimit
        locations = @(
            @{ country = "RU"; city = "Moscow" },
            @{ country = "RU"; city = "Saint Petersburg" },
            @{ country = "RU"; city = "Kazan" },
            @{ country = "RU"; city = "Yekaterinburg" },
            @{ country = "RU"; city = "Novosibirsk" }
        )
        measurementOptions = @{
            port     = $port
            protocol = "HTTPS"
            request  = @{
                method  = "GET"
                host    = $effectiveHost
                path    = $path
                headers = @{
                    "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36"
                }
            }
        }
    } | ConvertTo-Json -Depth 6

    Write-Host "`n=== Замер из РФ -> $($uri.Host):$port (Host/SNI: $effectiveHost, Path: $path) ===" -ForegroundColor Cyan
    $created = Invoke-RestMethod -Method Post -Uri $apiBase -ContentType "application/json" -Body $payload -Headers @{ "User-Agent" = "Obxodka-Diagnostics/1.0" }
    $id = $created.id

    $result = $null
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        Start-Sleep -Milliseconds 1200
        $result = Invoke-RestMethod -Method Get -Uri "$apiBase/$id" -Headers @{ "User-Agent" = "Obxodka-Diagnostics/1.0" }
        if ($result.status -ne "in-progress") {
            break
        }
    }

    foreach ($probeRes in $result.results) {
        $city = $probeRes.probe.city
        $asn = "AS$($probeRes.probe.asn)"
        $isp = $probeRes.probe.network
        $status = $probeRes.result.status
        $httpCode = $probeRes.result.statusCode
        $tlsSubject = $probeRes.result.tls.subject.CN
        $tlsIssuer = $probeRes.result.tls.issuer.O
        $totalMs = $probeRes.result.timings.total
        $err = $probeRes.result.error

        if ($status -eq "finished" -and $httpCode -gt 0) {
            Write-Host ("[OK]   {0,-18} | {1,-8} {2,-24} | HTTP {3} | {4,4} ms | Cert: {5} ({6})" -f $city, $asn, $isp, $httpCode, $totalMs, $tlsSubject, $tlsIssuer) -ForegroundColor Green
        }
        else {
            Write-Host ("[FAIL] {0,-18} | {1,-8} {2,-24} | {3} {4}" -f $city, $asn, $isp, $status, $err) -ForegroundColor Red
        }
    }
}
