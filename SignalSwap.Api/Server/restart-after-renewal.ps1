param(
    [string]$ServiceName = "SignalSwapApi"
)

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "$ServiceName is not installed yet, so there is nothing to restart."
    exit 0
}

Restart-Service -Name $ServiceName -Force -ErrorAction Stop
Write-Host "$ServiceName restarted with the renewed certificate."
