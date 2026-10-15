param(
    [string]$RootDir = "$PSScriptRoot\..",
    [string]$Version = ""
)

$RootDir = $RootDir.Trim('"', "'", ' ')
$propsPath = Join-Path $RootDir "version.props"
$svgPath = Join-Path $RootDir ".github\assets\animated_header.svg"

if (-not $Version -and (Test-Path $propsPath)) {
    [xml]$xml = Get-Content $propsPath
    $Version = $xml.Project.PropertyGroup.AppVersion
}

if ($Version -and (Test-Path $svgPath)) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    $svgContent = [System.IO.File]::ReadAllText($svgPath, [System.Text.Encoding]::UTF8)
    $newContent = [System.Text.RegularExpressions.Regex]::Replace($svgContent, 'LIVE CORE ENGINE v[0-9.]+', "LIVE CORE ENGINE v$Version")
    if ($newContent -ne $svgContent) {
        [System.IO.File]::WriteAllText($svgPath, $newContent, $utf8NoBom)
        Write-Host "SyncSvgVersion: Successfully updated animated_header.svg to v$Version"
    }
}
