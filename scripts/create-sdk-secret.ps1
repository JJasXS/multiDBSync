# Creates/updates the single SDK connections secret for a tenant.
# Password is prompted securely and never echoed.
param(
    [string]$TenantCode = $(if ($env:TENANT_CODE) { $env:TENANT_CODE } else { "TNT10006" }),
    [string]$Region = $(if ($env:AWS_REGION) { $env:AWS_REGION } else { "ap-southeast-1" }),
    [string]$CompanyName = "UAMAT",
    [string]$DcfPath = "C:\eStream\SQLAccounting\Share\SQLACC.DCF",
    [string]$Database = "ACC-UAMAT.FDB",
    [string]$Username = "ADMIN"
)

$ErrorActionPreference = "Stop"
$secretId = "proacc/tenant/$TenantCode/sdkConnections"

Write-Host "Secret: $secretId"
Write-Host "Company: $CompanyName / $Database"
Write-Host "DCF: $DcfPath"
Write-Host ""

$secure = Read-Host -AsSecureString "SQL Account password for $Username@$Database"
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$payload = @{
    companies = @(
        @{
            name = $CompanyName
            dcfPath = $DcfPath
            database = $Database
            username = $Username
            password = $password
        }
    )
} | ConvertTo-Json -Depth 5

$tmp = Join-Path $env:TEMP "sdkConnections-$TenantCode.json"
[System.IO.File]::WriteAllText($tmp, $payload)

aws sts get-caller-identity --region $Region | Out-Host
$exists = $true
aws secretsmanager describe-secret --secret-id $secretId --region $Region 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $exists = $false }

if (-not $exists) {
    aws secretsmanager create-secret `
        --name $secretId `
        --description "SqlAccountSync SDK company logins for tenant $TenantCode" `
        --secret-string "file://$tmp" `
        --region $Region `
        --tags Key=tenant,Value=$TenantCode Key=app,Value=SqlAccountSync Key=kind,Value=sdkConnections
} else {
    aws secretsmanager put-secret-value `
        --secret-id $secretId `
        --secret-string "file://$tmp" `
        --region $Region
}

Remove-Item -Force $tmp -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Done. Add more companies to the same secret JSON when ready."
Write-Host "Match SYNC_DATABASE.DBCODE to companies[].name (e.g. UAMAT)."
