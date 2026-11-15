<#
.SYNOPSIS
    Живой аудит утечек (IPv4/IPv6/DNS/NRPT/SMHNR) и реальный замер скорости через активный Windows VPN туннель Obxodka.
.DESCRIPTION
    Запускать при ПОДКЛЮЧЕННОМ VPN (желательно из-под пользователя). Не вносит изменений в систему — только проверяет текущее состояние маршрутов, DNS, NRPT, утечек IP/ISP DNS и замеряет реальную скорость Download/Upload/Ping через Cloudflare CDN.
#>
[CmdletBinding()]
param(
    [int]$DownloadBytesPerStream = 10485760,
    [int]$UploadBytesPerStream = 5242880,
    [int]$ParallelStreams = 3
)

$ErrorActionPreference = 'Stop'

function Write-Check([string]$Label, [bool]$Ok, [string]$Details) {
    $status = if ($Ok) { '[OK]  ' } else { '[FAIL]' }
    $color = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host "$status $Label - $Details" -ForegroundColor $color
}

Write-Host "`n=== 1. Проверка адаптера Wintun и фиксации MTU ===" -ForegroundColor Cyan
$wintun = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
    ($_.Name -like '*Obxodka*' -or $_.InterfaceDescription -like '*Wintun*') -and $_.Status -eq 'Up'
} | Select-Object -First 1

if (-not $wintun) {
    Write-Check 'Wintun адаптер' $false 'Активный адаптер Obxodka/Wintun не найден. Сначала включите VPN и повторите скрипт.'
    exit 1
}

$ifIdx = $wintun.ifIndex
$ipIfV4 = Get-NetIPInterface -InterfaceIndex $ifIdx -AddressFamily IPv4 -ErrorAction SilentlyContinue
$ipIfV6 = Get-NetIPInterface -InterfaceIndex $ifIdx -AddressFamily IPv6 -ErrorAction SilentlyContinue
$mtuOk = ($ipIfV4.NlMtu -ge 1240 -and $ipIfV4.NlMtu -le 1500)
Write-Check 'Адаптер Wintun' $true "$($wintun.Name) (ifIndex=$ifIdx)"
Write-Check 'MTU Wintun (IPv4/IPv6)' $mtuOk "IPv4=$($ipIfV4.NlMtu)B, IPv6=$($ipIfV6.NlMtu)B"

Write-Host "`n=== 2. Проверка политики защиты DNS (SMHNR, NRPT, физические NIC) ===" -ForegroundColor Cyan
$smhnr = (Get-ItemProperty -Path 'HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient' -Name 'DisableSmartNameResolution' -ErrorAction SilentlyContinue).DisableSmartNameResolution
Write-Check 'SMHNR DisableSmartNameResolution' ($smhnr -eq 1) "value=$smhnr"

$nrpt = Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq 'ObxodkaVpnDns' }
$nrptOk = $null -ne $nrpt -and ($nrpt.NameServers -contains '1.1.1.1')
Write-Check 'NRPT правило (Namespace .)' $nrptOk ($(if ($nrpt) { "Servers=$($nrpt.NameServers -join ', ')" } else { 'отсутствует' }))

$wintunDnsV4 = (Get-DnsClientServerAddress -InterfaceIndex $ifIdx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
$wintunDnsV6 = (Get-DnsClientServerAddress -InterfaceIndex $ifIdx -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses
Write-Check 'Wintun DNS IPv4' ($wintunDnsV4 -contains '1.1.1.1') "$($wintunDnsV4 -join ', ')"
Write-Check 'Wintun DNS IPv6' ($wintunDnsV6 -contains '2606:4700:4700::1111') "$(if ($wintunDnsV6) { $wintunDnsV6 -join ', ' } else { 'не назначен' })"

$physNics = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
    $_.Status -eq 'Up' -and
    $_.ifIndex -ne $ifIdx -and
    $_.Name -notlike '*Loopback*' -and
    $_.Name -notlike '*vEthernet*' -and
    $_.Name -notlike '*Radmin*' -and
    $_.InterfaceDescription -notmatch 'Hyper-V|VMware|VirtualBox|Radmin|TAP-Windows|Wintun'
}
$trustedSet = @('1.1.1.1', '1.0.0.1', '2606:4700:4700::1111', '2001:4860:4860::8888')
$leakingDns = @()
foreach ($nic in $physNics) {
    $allDns = (Get-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ErrorAction SilentlyContinue).ServerAddresses
    foreach ($srv in $allDns) {
        if ($srv -and ($srv -notlike 'fec0:0:0:ffff::*') -and ($srv -notin $trustedSet)) {
            $leakingDns += "$($nic.Name): $srv"
        }
    }
}
Write-Check 'Физические адаптеры без DNS провайдера' ($leakingDns.Count -eq 0) $(if ($leakingDns.Count -eq 0) { 'утечек в конфиге DNS адаптеров нет' } else { $leakingDns -join '; ' })

Write-Host "`n=== 3. Проверка маршрутов (0.0.0.0/1, 128.0.0.0/1, speedtest.net, DNS /32 & /128) ===" -ForegroundColor Cyan
$rV4_1 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '0.0.0.0/1' -ErrorAction SilentlyContinue
$rV4_2 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '128.0.0.0/1' -ErrorAction SilentlyContinue
Write-Check 'IPv4 Default-Over-Tunnel' ($null -ne $rV4_1 -and $null -ne $rV4_2) '0.0.0.0/1 & 128.0.0.0/1 -> Wintun'

$dnsRouteV4 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '1.1.1.1/32' -ErrorAction SilentlyContinue
$dnsRouteV6 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '2606:4700:4700::1111/128' -ErrorAction SilentlyContinue
Write-Check 'DNS маршруты /32 и /128 в туннель' ($null -ne $dnsRouteV4) "IPv4=$($null -ne $dnsRouteV4), IPv6=$($null -ne $dnsRouteV6)"

$rV6_1 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '::/1' -ErrorAction SilentlyContinue
$rV6_2 = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '8000::/1' -ErrorAction SilentlyContinue
$rTeredo = Get-NetRoute -InterfaceIndex $ifIdx -DestinationPrefix '2001::/32' -ErrorAction SilentlyContinue
if ($null -ne $rV6_1 -or $null -ne $rV6_2) {
    Write-Check 'IPv6 Default & Teredo перехват' ($null -ne $rV6_1 -and $null -ne $rV6_2 -and $null -ne $rTeredo) "::/1=$($null -ne $rV6_1), 8000::/1=$($null -ne $rV6_2), Teredo 2001::/32=$($null -ne $rTeredo)"
}
else {
    Write-Host '  IPv6 маршруты неактивны (хост без IPv6 и режим IPv4-only выключен)' -ForegroundColor Gray
}

$stIp = ([System.Net.Dns]::GetHostAddresses('speedtest.net') | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString
if ($stIp) {
    $stRoute = (Find-NetRoute -RemoteIPAddress $stIp -ErrorAction SilentlyContinue | Select-Object -First 1)
    Write-Check "Маршрут до speedtest.net ($stIp)" ($stRoute.InterfaceIndex -eq $ifIdx) "Interface=$($stRoute.InterfaceAlias) (ifIndex=$($stRoute.InterfaceIndex))"
}

Write-Host "`n=== 4. Проверка реального внешнего IP и замер скорости VPN ($ParallelStreams потоков) ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.Net.Http
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$handler = [System.Net.Http.HttpClientHandler]::new()
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(45)

try {
    $extV4 = ($http.GetStringAsync('https://api.ipify.org').GetAwaiter().GetResult()).Trim()
    Write-Check 'Внешний IPv4 через туннель' ($extV4 -ne '213.230.92.83') "Egress IPv4=$extV4"
}
catch {
    Write-Check 'Внешний IPv4 через туннель' $false $_.Exception.Message
}

try {
    $extV6 = ($http.GetStringAsync('https://api6.ipify.org').GetAwaiter().GetResult()).Trim()
    Write-Check 'Внешний IPv6 через туннель' ($extV6 -match ':') "Egress IPv6=$extV6"
}
catch {
    Write-Host "  Внешний IPv6: недоступен / заблокирован ($($_.Exception.Message))" -ForegroundColor Gray
}

try {
    $traceText = $http.GetStringAsync('https://cloudflare.com/cdn-cgi/trace').GetAwaiter().GetResult()
    $ipLine = ($traceText -split "`n" | Where-Object { $_ -like 'ip=*' } | Select-Object -First 1)
    $locLine = ($traceText -split "`n" | Where-Object { $_ -like 'loc=*' } | Select-Object -First 1)
    Write-Check 'Cloudflare Edge Trace' ($null -ne $ipLine) "$ipLine, $locLine"
}
catch {
    Write-Check 'Cloudflare Edge Trace' $false $_.Exception.Message
}

# Замер unloaded RTT
$pings = @()
for ($i = 0; $i -lt 5; $i++) {
    $swPing = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $http.GetByteArrayAsync('https://speed.cloudflare.com/__down?bytes=0').GetAwaiter().GetResult()
    $swPing.Stop()
    $pings += $swPing.Elapsed.TotalMilliseconds
}
$avgPing = ($pings | Measure-Object -Average).Average
Write-Host ("  Unloaded HTTP RTT: {0:F1} ms" -f $avgPing) -ForegroundColor Gray

# Замер Download
$swDown = [System.Diagnostics.Stopwatch]::StartNew()
$downTasks = @(1..$ParallelStreams | ForEach-Object {
    $http.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=$DownloadBytesPerStream")
})
[System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$downTasks)
$swDown.Stop()
$totalDownBytes = 0L
foreach ($t in $downTasks) {
    $totalDownBytes += $t.GetAwaiter().GetResult().Length
}
$downMbps = ($totalDownBytes * 8.0) / ($swDown.Elapsed.TotalSeconds * 1000000.0)
Write-Host ("  Download ({0} streams, {1:F1} MB): {2:F2} Mbps за {3:F2}s" -f $ParallelStreams, ($totalDownBytes / 1MB), $downMbps, $swDown.Elapsed.TotalSeconds) -ForegroundColor Cyan

# Замер Upload
$payload = New-Object byte[] $UploadBytesPerStream
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($payload)
$rng.Dispose()

$swUp = [System.Diagnostics.Stopwatch]::StartNew()
$upTasks = @(1..$ParallelStreams | ForEach-Object {
    $content = [System.Net.Http.ByteArrayContent]::new($payload)
    $http.PostAsync('https://speed.cloudflare.com/__up', $content)
})
[System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$upTasks)
$swUp.Stop()
$upFailed = @($upTasks | Where-Object { -not $_.GetAwaiter().GetResult().IsSuccessStatusCode }).Count
$upAllOk = $upFailed -eq 0
$totalUpBytes = [long]$UploadBytesPerStream * $ParallelStreams
$upMbps = ($totalUpBytes * 8.0) / ($swUp.Elapsed.TotalSeconds * 1000000.0)
Write-Check 'Upload HTTP статус' $upAllOk "успешно=$($ParallelStreams - $upFailed)/$ParallelStreams"
Write-Host ("  Upload   ({0} streams, {1:F1} MB): {2:F2} Mbps за {3:F2}s" -f $ParallelStreams, ($totalUpBytes / 1MB), $upMbps, $swUp.Elapsed.TotalSeconds) -ForegroundColor Cyan

$http.Dispose()

Write-Host "`n=== 5. Проверка свежих записей client.log на ошибки утечек и сокетов ===" -ForegroundColor Cyan
$logPath = Join-Path $env:LOCALAPPDATA 'obxodka\logs\client.log'
if (Test-Path $logPath) {
    $tailLines = Get-Content -Path $logPath -Tail 250 -ErrorAction SilentlyContinue
    $dnsLeakErrors = @($tailLines | Where-Object { $_ -like '*[DNS-LEAK ERROR]*' }).Count
    $socketBindWarns = @($tailLines | Where-Object { $_ -like '*[SOCKET BIND WARN]*' }).Count
    Write-Check 'Лог client.log (последние 250 строк)' ($dnsLeakErrors -eq 0 -and $socketBindWarns -eq 0) "DNS-LEAK ERROR=$dnsLeakErrors, SOCKET BIND WARN=$socketBindWarns"
}
else {
    Write-Host "  Файл лога не найден: $logPath" -ForegroundColor Gray
}

Write-Host "`nГотово.`n" -ForegroundColor Green
