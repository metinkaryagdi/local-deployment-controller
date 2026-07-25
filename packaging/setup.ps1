<#
.SYNOPSIS
    Local Deployment Controller kurulum betigi.

.DESCRIPTION
    - git ve docker on kosullarini dogrular
    - dagitim klasorunu olusturur
    - appsettings.json icindeki port ve dagitim klasorunu gunceller
    - panel portu icin gelen baglanti guvenlik duvari kurali ekler (yonetici gerekir)
    - kullanici oturum actiginda otomatik baslamasi icin Zamanlanmis Gorev olusturur

.EXAMPLE
    .\setup.ps1
    Varsayilanlarla kurar (port 5000, C:\Deployments, otomatik baslatma acik).

.EXAMPLE
    .\setup.ps1 -Port 5050 -BaseDirectory D:\Deployments -SkipAutoStart
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 5000,

    [string]$BaseDirectory = 'C:\Deployments',

    [ValidateSet('Private', 'Public', 'Domain', 'Any')]
    [string]$FirewallProfile = 'Private',

    [switch]$SkipFirewall,

    [switch]$SkipAutoStart
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'DeployController.exe'
$taskName = 'LocalDeploymentController'
$ruleName = "Local Deployment Controller $Port"

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-Warn($text) { Write-Host "  [UYARI] $text" -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "  [HATA] $text" -ForegroundColor Red }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "Local Deployment Controller kurulumu" -ForegroundColor White
Write-Host "Klasor: $root"

# --------------------------------------------------------------- on kosullar

Write-Step 'On kosullar'

if (-not (Test-Path $exe)) {
    Write-Fail "DeployController.exe bulunamadi: $exe"
    exit 1
}
Write-Ok "DeployController.exe bulundu"

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Fail 'git PATH uzerinde bulunamadi. https://git-scm.com/download/win adresinden kurun.'
    exit 1
}
Write-Ok "git: $((git --version) -replace 'git version ', '')"

$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    Write-Fail 'docker PATH uzerinde bulunamadi. Docker Desktop kurun ve WSL2 arka ucunu etkinlestirin.'
    exit 1
}

$serverVersion = $null
try { $serverVersion = (docker version --format '{{.Server.Version}}' 2>$null) } catch { }

if ([string]::IsNullOrWhiteSpace($serverVersion)) {
    Write-Warn 'Docker CLI var ama daemon yanit vermiyor. Docker Desktop calismiyor olabilir.'
    Write-Warn 'Panel acilir, ancak Docker Desktop baslatilmadan dagitim yapilamaz.'
} else {
    Write-Ok "docker daemon: $serverVersion"
}

$composeOk = $false
try { $null = docker compose version 2>$null; $composeOk = $LASTEXITCODE -eq 0 } catch { }
if ($composeOk) { Write-Ok 'docker compose v2 kullanilabilir' }
else { Write-Warn 'docker compose v2 dogrulanamadi. Docker Desktop guncel olmayabilir.' }

# --------------------------------------------------------------- yapilandirma

Write-Step 'Yapilandirma'

New-Item -ItemType Directory -Force -Path $BaseDirectory | Out-Null
Write-Ok "Dagitim klasoru hazir: $BaseDirectory"

$settingsPath = Join-Path $root 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$settings.Urls = "http://0.0.0.0:$Port"
$settings.Deployment.BaseDirectory = $BaseDirectory

$json = $settings | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($settingsPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Ok "appsettings.json guncellendi (port $Port)"

# ------------------------------------------------------------ guvenlik duvari

Write-Step 'Guvenlik duvari'

if ($SkipFirewall) {
    Write-Warn 'Atlandi (-SkipFirewall).'
} elseif (-not $isAdmin) {
    Write-Warn 'Yonetici degil, kural eklenemedi. Yonetici PowerShell''de sunu calistirin:'
    Write-Host "    New-NetFirewallRule -DisplayName '$ruleName' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile $FirewallProfile" -ForegroundColor Gray
} else {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Action Allow -Profile $FirewallProfile | Out-Null
    Write-Ok "Gelen TCP $Port kurali eklendi (profil: $FirewallProfile)"

    $active = Get-NetConnectionProfile | Where-Object { $_.IPv4Connectivity -ne 'Disconnected' }
    foreach ($netProfile in $active) {
        if ($FirewallProfile -ne 'Any' -and $netProfile.NetworkCategory -ne $FirewallProfile) {
            Write-Warn "'$($netProfile.Name)' agi '$($netProfile.NetworkCategory)' profilinde; kural bu agda gecerli olmaz."
            Write-Host "    Set-NetConnectionProfile -InterfaceAlias '$($netProfile.InterfaceAlias)' -NetworkCategory $FirewallProfile" -ForegroundColor Gray
        }
    }
}

Write-Host '  Not: dagittiginiz projelerin portlari (8080 vb.) icin ayri kural gerekir:' -ForegroundColor Gray
Write-Host "    New-NetFirewallRule -DisplayName 'LDC app 8080' -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile $FirewallProfile" -ForegroundColor Gray

# ------------------------------------------------------------ otomatik baslat

Write-Step 'Otomatik baslatma'

if ($SkipAutoStart) {
    Write-Warn 'Atlandi (-SkipAutoStart). Elle baslatmak icin start.bat kullanin.'
} else {
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

        $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $root
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $settingsSet = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settingsSet `
            -Description 'Local Deployment Controller - panel ve dagitim motoru' | Out-Null

        Write-Ok "Zamanlanmis gorev olusturuldu: $taskName (oturum acildiginda)"
        Write-Host '  Docker Desktop yalnizca kullanici oturumu acikken calistigi icin' -ForegroundColor Gray
        Write-Host '  Windows Servisi yerine oturum acilisinda baslatma tercih edildi.' -ForegroundColor Gray
    } catch {
        Write-Warn "Zamanlanmis gorev olusturulamadi: $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------- ozet

Write-Step 'Ozet'

$ips = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.InterfaceAlias -notlike '*WSL*' -and $_.InterfaceAlias -notlike '*Default Switch*' } |
    Select-Object -ExpandProperty IPAddress

Write-Host "  Bu makinede : http://localhost:$Port"
foreach ($ip in $ips) { Write-Host "  Agdan       : http://${ip}:$Port" -ForegroundColor White }
Write-Host "  Dagitim yeri: $BaseDirectory"
Write-Host ''
Write-Host '  Simdi baslatmak icin: .\start.bat' -ForegroundColor Cyan
Write-Host '  Kaldirmak icin      : .\uninstall.ps1' -ForegroundColor Cyan
Write-Host ''
Write-Host '  UYARI: Panelde kimlik dogrulama yoktur ve verdiginiz depoyu bu makinede' -ForegroundColor Yellow
Write-Host '  klonlayip calistirir. Yalnizca guvendiginiz yerel agda kullanin.' -ForegroundColor Yellow
