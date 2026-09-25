# Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Visual Studio Turbo & Memory Unlocker (32GB+ RAM & GPU)  " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Elevate if not running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[*] Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# 2. Locate Visual Studio installation
$vsPaths = @(
    "C:\Program Files\Microsoft Visual Studio\26\Community",
    "C:\Program Files\Microsoft Visual Studio\2022\Community",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise"
)

$targetVs = $null
foreach ($p in $vsPaths) {
    if (Test-Path "$p\Common7\IDE\devenv.exe") {
        $targetVs = $p
        break
    }
}

if ($null -eq $targetVs) {
    Write-Host "[-] Visual Studio installation not found!" -ForegroundColor Red
    pause
    exit 1
}

Write-Host "[+] Found Visual Studio at: $targetVs" -ForegroundColor Green

# 3. Patch Roslyn Code Analysis Service config (Enable Server GC)
$roslynConfigs = Get-ChildItem -Path "$targetVs\Common7" -Recurse -Filter "*RoslynCodeAnalysisService*.exe.config" -ErrorAction SilentlyContinue

foreach ($cfg in $roslynConfigs) {
    try {
        [xml]$xml = Get-Content $cfg.FullName
        $runtime = $xml.configuration.runtime
        if ($runtime) {
            $gcServerNode = $runtime.SelectSingleNode("gcServer")
            if ($gcServerNode) {
                $gcServerNode.SetAttribute("enabled", "true")
            } else {
                $newNode = $xml.CreateElement("gcServer")
                $newNode.SetAttribute("enabled", "true")
                [void]$runtime.AppendChild($newNode)
            }

            $gcConcurrentNode = $runtime.SelectSingleNode("gcConcurrent")
            if ($gcConcurrentNode) {
                $gcConcurrentNode.SetAttribute("enabled", "true")
            } else {
                $newNode = $xml.CreateElement("gcConcurrent")
                $newNode.SetAttribute("enabled", "true")
                [void]$runtime.AppendChild($newNode)
            }

            $xml.Save($cfg.FullName)
            Write-Host "[+] Unlocked Server GC in: $($cfg.Name)" -ForegroundColor Green
        }
    } catch {
        Write-Host "[-] Could not update $($cfg.Name): $_" -ForegroundColor DarkYellow
    }
}

# 4. Patch devenv.exe.config
$devenvConfig = "$targetVs\Common7\IDE\devenv.exe.config"
if (Test-Path $devenvConfig) {
    try {
        [xml]$xml = Get-Content $devenvConfig
        $runtime = $xml.configuration.runtime
        if ($runtime) {
            $gcServerNode = $runtime.SelectSingleNode("gcServer")
            if ($gcServerNode) {
                $gcServerNode.SetAttribute("enabled", "true")
            } else {
                $newNode = $xml.CreateElement("gcServer")
                $newNode.SetAttribute("enabled", "true")
                [void]$runtime.AppendChild($newNode)
            }

            $gcConcurrentNode = $runtime.SelectSingleNode("gcConcurrent")
            if ($gcConcurrentNode) {
                $gcConcurrentNode.SetAttribute("enabled", "true")
            } else {
                $newNode = $xml.CreateElement("gcConcurrent")
                $newNode.SetAttribute("enabled", "true")
                [void]$runtime.AppendChild($newNode)
            }

            $xml.Save($devenvConfig)
            Write-Host "[+] Unlocked Server GC in: devenv.exe.config" -ForegroundColor Green
        }
    } catch {
        Write-Host "[-] Could not update devenv.exe.config: $_" -ForegroundColor DarkYellow
    }
}

# 5. Set System-wide High Performance .NET Environment Variables
Write-Host "[*] Setting high-performance environment variables for 32GB RAM..." -ForegroundColor Yellow
[Environment]::SetEnvironmentVariable("DOTNET_gcServer", "1", "User")
[Environment]::SetEnvironmentVariable("DOTNET_GCConserveMemory", "0", "User")
[Environment]::SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-gpu-rasterization --enable-zero-copy --ignore-gpu-blocklist", "User")

# 6. Add Windows Defender exclusions to eliminate disk I/O lag
Write-Host "[*] Configuring Windows Defender exclusions..." -ForegroundColor Yellow
$foldersToExclude = @(
    "C:\BuildCache",
    "c:\Users\irovb\Documents\code"
)

foreach ($f in $foldersToExclude) {
    if (Test-Path $f) {
        try {
            Add-MpPreference -ExclusionPath $f -ErrorAction SilentlyContinue
            Write-Host "[+] Excluded folder: $f" -ForegroundColor Green
        } catch { }
    }
}

$procsToExclude = @(
    "devenv.exe",
    "ServiceHub.RoslynCodeAnalysisService.exe",
    "ServiceHub.RoslynCodeAnalysisServiceS.exe",
    "ServiceHub.Host.Dotnet.x64.exe"
)

foreach ($p in $procsToExclude) {
    try {
        Add-MpPreference -ExclusionProcess $p -ErrorAction SilentlyContinue
        Write-Host "[+] Excluded process: $p" -ForegroundColor Green
    } catch { }
}

Write-Host "`n[SUCCESS] Visual Studio environment fully unlocked!" -ForegroundColor Cyan
Write-Host "Restart Visual Studio for all memory and GPU acceleration changes to take full effect." -ForegroundColor Cyan
