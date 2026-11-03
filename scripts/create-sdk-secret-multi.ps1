# Creates/updates sdkConnections with several companies; password prompted once.
param(
    [string]$TenantCode = "TNT10006",
    [string]$Region = "ap-southeast-1",
    [string]$DcfPath = "C:\eStream\SQLAccounting\Share\SQLACC.DCF",
    [string]$Username = "ADMIN"
)

$ErrorActionPreference = "Stop"
$secretId = "proacc/tenant/$TenantCode/sdkConnections"

$companies = @(
    # Master / MAIN COMPANY must be here or COM cannot return home after sub pushes.
    @{ name = "MAINTEST"; database = "ACC-MAIN.FDB" },
    @{ name = "UAMAT";    database = "ACC-UAMAT.FDB" },
    @{ name = "MIRACLE";  database = "ACC-MIRACLE.FDB" },
    @{ name = "ONYAT";    database = "ACC-ONYAT.FDB" },
    @{ name = "STEPPES";  database = "ACC-STEPPES.FDB" }
)

Write-Host "Secret: $secretId"
Write-Host "Companies: $(($companies | ForEach-Object { $_.name }) -join ', ')"
Write-Host "DCF: $DcfPath"
Write-Host ""

$secure = Read-Host -AsSecureString "SQL Account password for $Username (same for all listed companies)"
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$rows = @()
foreach ($c in $companies) {
    $rows += @{
        name = $c.name
        dcfPath = $DcfPath
        database = $c.database
        username = $Username
        password = $password
    }
}

$payload = @{ companies = $rows } | ConvertTo-Json -Depth 6
$tmp = Join-Path $env:TEMP "sdkConnections-$TenantCode-multi.json"
# Avoid pager hanging the script
$env:AWS_PAGER = ""
[System.IO.File]::WriteAllText($tmp, $payload)

aws sts get-caller-identity --region $Region | Out-Host

# describe-secret writes to stderr when missing; do not treat that as a script failure
$exists = $false
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
aws secretsmanager describe-secret --secret-id $secretId --region $Region 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $exists = $true }
$ErrorActionPreference = $prevEap

if (-not $exists) {
    Write-Host "Secret not found — creating $secretId ..."
    aws secretsmanager create-secret `
        --name $secretId `
        --description "SqlAccountSync SDK company logins for tenant $TenantCode" `
        --secret-string "file://$tmp" `
        --region $Region `
        --tags Key=tenant,Value=$TenantCode Key=app,Value=SqlAccountSync Key=kind,Value=sdkConnections
    if ($LASTEXITCODE -ne 0) { throw "create-secret failed" }
} else {
    Write-Host "Secret exists — updating companies ..."
    aws secretsmanager put-secret-value `
        --secret-id $secretId `
        --secret-string "file://$tmp" `
        --region $Region
    if ($LASTEXITCODE -ne 0) { throw "put-secret-value failed" }
}

Remove-Item -Force $tmp -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Done. Secret has $($rows.Count) companies (password not shown)."
