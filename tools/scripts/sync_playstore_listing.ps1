param(
    [string]$ServiceAccountJson = $env:PLAY_CONFIG_JSON,
    [string]$PackageName = "com.octocore.obxodka"
)

$ErrorActionPreference = 'Stop'

function Log-Info($msg) { Write-Host "[PLAYSTORE-LISTING] [INFO] $msg" -ForegroundColor Cyan }
function Log-Success($msg) { Write-Host "[PLAYSTORE-LISTING] [SUCCESS] $msg" -ForegroundColor Green }
function Log-Warning($msg) { Write-Host "[PLAYSTORE-LISTING] [WARN] $msg" -ForegroundColor Yellow }

if ([string]::IsNullOrWhiteSpace($ServiceAccountJson)) {
    Log-Warning "PLAY_CONFIG_JSON not set. Skipping Google Play Store listing update."
    exit 0
}

$storeDir = Join-Path $PSScriptRoot "../../packaging/store"
if (-not (Test-Path $storeDir)) {
    $storeDir = Join-Path (Get-Location) "packaging/store"
}

$ruFull = Join-Path $storeDir "android/ru-RU/full_description.txt"
if (-not (Test-Path $ruFull)) { $ruFull = Join-Path $storeDir "ru-RU/full_description.txt" }
$ruShort = Join-Path $storeDir "android/ru-RU/short_description.txt"
if (-not (Test-Path $ruShort)) { $ruShort = Join-Path $storeDir "ru-RU/short_description.txt" }
$enFull = Join-Path $storeDir "android/en-US/full_description.txt"
if (-not (Test-Path $enFull)) { $enFull = Join-Path $storeDir "en-US/full_description.txt" }
$enShort = Join-Path $storeDir "android/en-US/short_description.txt"
if (-not (Test-Path $enShort)) { $enShort = Join-Path $storeDir "en-US/short_description.txt" }

if (-not (Test-Path $ruFull) -and -not (Test-Path $enFull)) {
    Log-Warning "No store listing files found in $storeDir. Skipping."
    exit 0
}

try {
    $sa = $ServiceAccountJson | ConvertFrom-Json
    if (-not $sa.client_email -or -not $sa.private_key) {
        Log-Warning "Invalid service account JSON. Skipping listing update."
        exit 0
    }

    Log-Info "Acquiring Google OAuth2 access token for $($sa.client_email)..."

    # Base64Url encode helper
    function Base64UrlEncode([byte[]]$bytes) {
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }

    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $headerJson = '{"alg":"RS256","typ":"JWT"}'
    $headerB64 = Base64UrlEncode ([System.Text.Encoding]::UTF8.GetBytes($headerJson))

    $claimObj = @{
        iss   = $sa.client_email
        scope = "https://www.googleapis.com/auth/androidpublisher"
        aud   = "https://oauth2.googleapis.com/token"
        exp   = $now + 3600
        iat   = $now
    }
    $claimJson = $claimObj | ConvertTo-Json -Compress
    $claimB64 = Base64UrlEncode ([System.Text.Encoding]::UTF8.GetBytes($claimJson))

    $sigInput = "$headerB64.$claimB64"
    $sigInputBytes = [System.Text.Encoding]::UTF8.GetBytes($sigInput)

    # Clean RSA private key (PEM PKCS#8)
    $cleanKey = $sa.private_key -replace "-----BEGIN PRIVATE KEY-----", "" -replace "-----END PRIVATE KEY-----", "" -replace "`r", "" -replace "`n", "" -replace "\s", ""
    $keyBytes = [Convert]::FromBase64String($cleanKey)

    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportPkcs8PrivateKey($keyBytes, [ref]0)
    $sigBytes = $rsa.SignData($sigInputBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $sigB64 = Base64UrlEncode $sigBytes

    $jwt = "$sigInput.$sigB64"

    $tokenBody = @{
        grant_type = "urn:ietf:params:oauth:grant-type:jwt-bearer"
        assertion  = $jwt
    }

    $tokenResp = Invoke-RestMethod -Method Post -Uri "https://oauth2.googleapis.com/token" -Body $tokenBody
    $accessToken = $tokenResp.access_token
    Log-Success "Google OAuth2 token acquired successfully!"

    $headers = @{
        "Authorization" = "Bearer $accessToken"
        "Content-Type"  = "application/json"
    }

    # 1. Create Edit
    Log-Info "Creating Google Play edit for $PackageName..."
    $editResp = Invoke-RestMethod -Method Post -Uri "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/$PackageName/edits" -Headers $headers -Body "{}"
    $editId = $editResp.id
    Log-Info "Edit created: $editId"

    # 2. Update Russian listing
    if (Test-Path $ruFull) {
        $fullText = (Get-Content $ruFull -Raw).Trim()
        $shortText = if (Test-Path $ruShort) { (Get-Content $ruShort -Raw).Trim() } else { "Быстрый и надежный VPN нового поколения." }
        $listingBody = @{
            fullDescription  = $fullText
            shortDescription = $shortText
        } | ConvertTo-Json -Compress

        Log-Info "Updating ru-RU listing..."
        Invoke-RestMethod -Method Put -Uri "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/$PackageName/edits/$editId/listings/ru-RU" -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($listingBody))
        Log-Success "ru-RU listing updated!"
    }

    # 3. Update English listing
    if (Test-Path $enFull) {
        $fullText = (Get-Content $enFull -Raw).Trim()
        $shortText = if (Test-Path $enShort) { (Get-Content $enShort -Raw).Trim() } else { "Fast and resilient next-generation VPN." }
        $listingBody = @{
            fullDescription  = $fullText
            shortDescription = $shortText
        } | ConvertTo-Json -Compress

        Log-Info "Updating en-US listing..."
        Invoke-RestMethod -Method Put -Uri "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/$PackageName/edits/$editId/listings/en-US" -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($listingBody))
        Log-Success "en-US listing updated!"
    }

    # 4. Commit Edit
    Log-Info "Committing listing changes to Google Play..."
    Invoke-RestMethod -Method Post -Uri "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/$PackageName/edits/$editId:commit" -Headers $headers
    Log-Success "Google Play listings committed successfully!"
}
catch {
    Log-Warning "Google Play listing sync skipped or failed non-critically: $_"
}
