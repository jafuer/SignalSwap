param(
    [Parameter(Mandatory = $true)][string]$HostName,
    [string]$CertificateFolder = "C:\SignalSwap\certs",
    [string]$ServiceName = "SignalSwapApi",
    [int]$Port = 5080
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Open PowerShell with 'Run as administrator' and run this again."
}

$installDir = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $installDir "SignalSwap.Api.exe"
if (-not (Test-Path $exe)) {
    throw "SignalSwap.Api.exe is not in $installDir. Copy the whole published folder there first."
}
if ($installDir -match '\s' -or $CertificateFolder -match '\s') {
    throw "Use folders whose paths have no spaces, such as C:\SignalSwap\Api and C:\SignalSwap\certs."
}

$certificate = Join-Path $CertificateFolder "certificate.pem"
$privateKey = Join-Path $CertificateFolder "private-key.pem"
if (-not (Test-Path $certificate) -or -not (Test-Path $privateKey)) {
    $chain = @(Get-ChildItem -Path $CertificateFolder -Filter "*-chain.pem" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*-chain-only.pem" })
    if ($chain.Count -ne 1) {
        throw "Expected one certificate in $CertificateFolder (certificate.pem, or a single *-chain.pem from win-acme) but found $($chain.Count). Get the certificate first, then run this again."
    }
    $certificate = $chain[0].FullName
    $privateKey = $certificate -replace '-chain\.pem$', '-key.pem'
    if (-not (Test-Path $privateKey)) {
        throw "Found $certificate but not its private key $privateKey."
    }
}
Write-Host "Using certificate $certificate"

$account = "NT SERVICE\$ServiceName"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($service) {
    if ($service.Status -ne "Stopped") {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
    }
    & sc.exe config $ServiceName binPath= $exe start= delayed-auto | Out-Null
} else {
    Write-Host "Creating the $ServiceName service..."
    & sc.exe create $ServiceName binPath= $exe start= delayed-auto DisplayName= "SignalSwap API" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe could not create the service (exit code $LASTEXITCODE)." }
    & sc.exe description $ServiceName "SignalSwap Web API for the Networks database." | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

& sc.exe config $ServiceName obj= $account | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe could not set the service account to $account (exit code $LASTEXITCODE)." }

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$environment = @((Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue).Environment | Where-Object { $_ })
if (-not ($environment | Where-Object { $_ -like "Jwt__Key=*" })) {
    Write-Host "Generating the token signing key..."
    $bytes = New-Object byte[] 48
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $environment += "Jwt__Key=$([Convert]::ToBase64String($bytes))"
}
$environment = @($environment | Where-Object { $_ -notlike "Kestrel__Endpoints__Https__*" })
$environment += "Kestrel__Endpoints__Https__Url=https://0.0.0.0:$Port"
$environment += "Kestrel__Endpoints__Https__Certificate__Path=$certificate"
$environment += "Kestrel__Endpoints__Https__Certificate__KeyPath=$privateKey"
New-ItemProperty -Path $regPath -Name Environment -PropertyType MultiString -Value ([string[]]$environment) -Force | Out-Null

& icacls.exe $installDir /grant "${account}:(OI)(CI)RX" /T /Q | Out-Null
& icacls.exe $CertificateFolder /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "${account}:(OI)(CI)R" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not lock down $CertificateFolder (icacls exit code $LASTEXITCODE)." }
& icacls.exe "$CertificateFolder\*" /reset /T /Q | Out-Null

$ruleName = "SignalSwap API"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    Write-Host "Opening port $Port for your home network and Tailscale..."
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -RemoteAddress LocalSubnet, "100.64.0.0/10" -Profile Any -Action Allow | Out-Null
}

Set-Content -Path (Join-Path (Split-Path -Parent $installDir) "hostname.txt") -Value $HostName

Write-Host "Starting $ServiceName..."
try {
    Start-Service -Name $ServiceName
} catch {
    $lastError = $_.Exception.Message
}

$url = "https://${HostName}:$Port/api/health"
$healthy = $false
for ($i = 0; $i -lt 15 -and -not $healthy; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod -Uri $url -TimeoutSec 5
        $healthy = $health.status -eq "ok"
    } catch {
        $lastError = $_.Exception.Message
    }
}

Write-Host ""
if ($healthy) {
    Write-Host "SignalSwap API is running at https://${HostName}:$Port and connected to the $($health.database) database." -ForegroundColor Green
} elseif ((Get-Service -Name $ServiceName).Status -eq "Running") {
    Write-Host "The service is running but $url failed: $lastError" -ForegroundColor Yellow
    Write-Host "If it mentions SQL or a 500 error, run Server\grant-sql-access.sql in SSMS."
    Write-Host "If it mentions the name, trust or SSL, check that $HostName points to this server and matches the certificate."
} else {
    Write-Host "The service did not start. Look in Event Viewer > Windows Logs > Application for errors from SignalSwap.Api." -ForegroundColor Red
    Write-Host "A wrong certificate file or a key that does not match the certificate is the usual cause."
}
