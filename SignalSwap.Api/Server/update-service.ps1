param(
    [Parameter(Mandatory = $true)][string]$From,
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
if (-not (Test-Path (Join-Path $From "SignalSwap.Api.exe"))) {
    throw "$From does not contain SignalSwap.Api.exe. Point -From at the new published folder."
}

Write-Host "Stopping $ServiceName..."
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

Write-Host "Copying the new version into $installDir..."
& robocopy.exe $From $installDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit code $LASTEXITCODE)." }

Write-Host "Starting $ServiceName..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$hostFile = Join-Path (Split-Path -Parent $installDir) "hostname.txt"
$hostName = if (Test-Path $hostFile) { (Get-Content $hostFile -TotalCount 1).Trim() } else { "localhost" }
try {
    $health = Invoke-RestMethod -Uri "https://${hostName}:$Port/api/health" -TimeoutSec 10
    Write-Host "Updated. The API is running and connected to the $($health.database) database." -ForegroundColor Green
} catch {
    Write-Host "Updated, but the health check failed: $($_.Exception.Message)" -ForegroundColor Yellow
}
