<#
.SYNOPSIS
    Local Deployment Controller kaldirma betigi.

.DESCRIPTION
    Zamanlanmis gorevi ve guvenlik duvari kuralini kaldirir, calisan surecleri durdurur.
    Dagitilan projelere (C:\Deployments) ve container'lara DOKUNMAZ; onlari panelden
    ya da "docker compose down -v" ile kendiniz kaldirin.
#>
[CmdletBinding()]
param(
    [int]$Port = 5000
)

$ErrorActionPreference = 'Continue'
$taskName = 'LocalDeploymentController'
$ruleName = "Local Deployment Controller $Port"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host 'Local Deployment Controller kaldiriliyor...' -ForegroundColor White

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "  [OK] Zamanlanmis gorev silindi: $taskName" -ForegroundColor Green
} else {
    Write-Host '  [--] Zamanlanmis gorev yok' -ForegroundColor Gray
}

$processes = Get-Process -Name 'DeployController' -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Stop-Process -Force
    Write-Host "  [OK] $($processes.Count) surec durduruldu" -ForegroundColor Green
} else {
    Write-Host '  [--] Calisan surec yok' -ForegroundColor Gray
}

if ($isAdmin) {
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($rule) {
        $rule | Remove-NetFirewallRule
        Write-Host "  [OK] Guvenlik duvari kurali silindi: $ruleName" -ForegroundColor Green
    } else {
        Write-Host '  [--] Guvenlik duvari kurali yok' -ForegroundColor Gray
    }
} else {
    Write-Host "  [UYARI] Yonetici degil; kurali elle silin:" -ForegroundColor Yellow
    Write-Host "    Remove-NetFirewallRule -DisplayName '$ruleName'" -ForegroundColor Gray
}

Write-Host ''
Write-Host 'Bitti. Dagitilan projeler ve container''lar oldugu gibi birakildi.' -ForegroundColor Cyan
