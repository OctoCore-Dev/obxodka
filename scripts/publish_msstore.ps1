# ==============================================================================
#  Official Microsoft Store Submissions API Publisher (Pure PowerShell)
# ==============================================================================
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$TenantId = $env:STORE_TENANT_ID,
    [string]$ClientId = $env:STORE_CLIENT_ID,
    [string]$ClientSecret = $env:STORE_CLIENT_SECRET,
    [string]$AppId = ($env:STORE_APP_ID, '9NZXP5WR803J' -ne $null)[0]
)

$ErrorActionPreference = 'Stop'

function Log-Info($msg) {
    Write-Host "[MSSTORE] [INFO] $msg" -ForegroundColor Cyan
}

function Log-Success($msg) {
    Write-Host "[MSSTORE] [SUCCESS] $msg" -ForegroundColor Green
}

function Log-Error($msg) {
    Write-Host "[MSSTORE] [ERROR] $msg" -ForegroundColor Red
}

if (-not (Test-Path $PackagePath)) {
    Log-Error "Package file not found: $PackagePath"
    exit 1
}

$pkgItem = Get-Item $PackagePath
$pkgSizeMB = [math]::Round($pkgItem.Length / 1MB, 2)
Log-Info "Target package: $($pkgItem.FullName) ($pkgSizeMB MB)"

if (-not $TenantId -or -not $ClientId -or -not $ClientSecret) {
    Log-Error "Missing Azure AD Credentials (STORE_TENANT_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET)."
    exit 1
}

# 1. Obtain Azure AD OAuth 2.0 Access Token
Log-Info "Authenticating with Azure AD (Client Credentials)..."
$tokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/token"
$tokenBody = @{
    grant_type    = "client_credentials"
    client_id     = $ClientId
    client_secret = $ClientSecret
    resource      = "https://manage.devcenter.microsoft.com"
}

try {
    $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenUri -Body $tokenBody
    $accessToken = $tokenResponse.access_token
    Log-Success "Authenticated with Microsoft Dev Center successfully!"
}
catch {
    Log-Error "Authentication failed: $_"
    exit 1
}

$authHeaders = @{
    "Authorization" = "Bearer $accessToken"
}

# 2. Get Application metadata
Log-Info "Fetching application details for App ID: $AppId..."
$appUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"
$appData = Invoke-RestMethod -Method Get -Uri $appUri -Headers $authHeaders -ContentType "application/json; charset=utf-8"
Log-Info "App Name: $($appData.primaryName)"

$pending = $appData.pendingApplicationSubmission
$subData = $null
$subId = $null

if ($pending -and $pending.id) {
    $subId = $pending.id
    Log-Info "Found active pending submission $subId. Fetching details..."
    try {
        $subData = Invoke-RestMethod -Method Get -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId/submissions/$subId" -Headers $authHeaders -ContentType "application/json; charset=utf-8"
        Log-Info "Existing submission status: $($subData.status)"

        if ($subData.status -in @("Certification", "CommitStarted", "PreProcessing", "Release")) {
            Log-Success "Submission $subId is already being processed by Microsoft (Status: $($subData.status))!"
            exit 0
        }
    }
    catch {
        Log-Info "Could not fetch existing submission $subId, creating fresh draft..."
    }
}

# 3. Create fresh submission if needed
if (-not $subData -or -not $subData.fileUploadUrl) {
    Log-Info "Preparing fresh submission from last published baseline..."
    $lastPub = $appData.lastPublishedApplicationSubmission
    $payload = @{}

    if ($lastPub -and $lastPub.id) {
        $lastId = $lastPub.id
        $lastData = Invoke-RestMethod -Method Get -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId/submissions/$lastId" -Headers $authHeaders -ContentType "application/json; charset=utf-8"
        
        $exclude = @("id", "status", "statusDetails", "applicationPackages", "fileUploadUrl", "resourceLocation")
        foreach ($prop in $lastData.PSObject.Properties) {
            if ($exclude -notcontains $prop.Name) {
                $payload[$prop.Name] = $prop.Value
            }
        }

        if (-not $payload["applicationCategory"] -or $payload["applicationCategory"] -eq "NotSet") {
            $payload["applicationCategory"] = "UtilitiesAndTools"
        }
    }

    $createUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId/submissions"
    $jsonPayload = $payload | ConvertTo-Json -Depth 10
    $subData = Invoke-RestMethod -Method Post -Uri $createUri -Headers $authHeaders -Body ([System.Text.Encoding]::UTF8.GetBytes($jsonPayload)) -ContentType "application/json; charset=utf-8"
    $subId = $subData.id
    Log-Success "Created new draft submission: $subId"
}

# 4. Prepare and Upload Package (ZIP Archive for Azure Blob)
$uploadUrl = $subData.fileUploadUrl
if (-not $uploadUrl) {
    Log-Error "No fileUploadUrl returned by Microsoft Partner Center."
    exit 1
}

Log-Info "Packaging zip archive for Azure Blob SAS upload..."
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) "msstore_package_$([System.Guid]::NewGuid().ToString('N')).zip"

if ($PackagePath.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
    $tempZip = $PackagePath
}
else {
    if (Test-Path $tempZip) { Remove-Item -Force $tempZip }
    Compress-Archive -Path $PackagePath -DestinationPath $tempZip -CompressionLevel Optimal
}

$zipSizeMB = [math]::Round((Get-Item $tempZip).Length / 1MB, 2)
Log-Info "Uploading zip ($zipSizeMB MB) to Microsoft Store SAS URL via BlockBlob..."

# Upload using System.Net.Http.HttpClient with BlockBlob headers
$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.Timeout = [System.TimeSpan]::FromMinutes(15)

$fileBytes = [System.IO.File]::ReadAllBytes($tempZip)
$byteContent = [System.Net.Http.ByteArrayContent]::new($fileBytes)
$byteContent.Headers.Add("x-ms-blob-type", "BlockBlob")
$byteContent.Headers.Add("Content-Type", "application/zip")

$uploadResponse = $httpClient.PutAsync($uploadUrl, $byteContent).GetAwaiter().GetResult()
if (-not $uploadResponse.IsSuccessStatusCode) {
    $errBody = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Log-Error "Upload to Azure SAS failed: $($uploadResponse.StatusCode) - $errBody"
    exit 1
}
Log-Success "Package uploaded successfully to Microsoft Azure Storage!"

# Clean temp zip if created
if ($tempZip -ne $PackagePath -and (Test-Path $tempZip)) {
    Remove-Item -Force $tempZip -ErrorAction SilentlyContinue
}

# 5. Update submission package metadata
Log-Info "Updating submission packages list with $($pkgItem.Name)..."
$subData.applicationPackages = @(
    @{
        fileName   = $pkgItem.Name
        fileStatus = "PendingUpload"
    }
)

$updateUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId/submissions/$subId"
$updateJson = $subData | ConvertTo-Json -Depth 10
$updatedSub = Invoke-RestMethod -Method Put -Uri $updateUri -Headers $authHeaders -Body $updateJson
Log-Success "Submission metadata updated successfully!"

# 6. Commit submission for certification
Log-Info "Submitting to Microsoft certification pipeline (POST /commit)..."
$commitUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId/submissions/$subId/commit"
$commitResp = Invoke-RestMethod -Method Post -Uri $commitUri -Headers $authHeaders
Log-Success "Submission $subId committed for certification!"
Log-Info "Current certification status: $($commitResp.status)"
Log-Success "=== MICROSOFT STORE PUBLICATION COMPLETE ==="
