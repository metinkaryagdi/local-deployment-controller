<#
.SYNOPSIS
    Hedef makineye kopyalanacak, kendi kendine yeten kurulum paketini uretir.

.DESCRIPTION
    dist\LocalDeploymentController altina .NET gerektirmeyen tek dosyalik win-x64
    yayini cikarir ve packaging\ altindaki kurulum betiklerini yanina kopyalar.

.EXAMPLE
    .\build-package.ps1 -Zip
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$Zip,

    # -Zip ile birlikte: olusan arsivi ayrica buraya kopyalar (orn. masaustu).
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DeployController\DeployController.csproj'
$packaging = Join-Path $root 'packaging'
$output = Join-Path $root 'dist\LocalDeploymentController'

if (Get-Process -Name 'DeployController' -ErrorAction SilentlyContinue) {
    throw 'DeployController.exe calisiyor; once durdurun (uninstall.ps1 ya da gorev yoneticisi).'
}

Write-Host "Yayinlaniyor ($Runtime, self-contained, single-file)..." -ForegroundColor Cyan

Remove-Item -Recurse -Force $output -ErrorAction SilentlyContinue

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $output `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz (cikis kodu $LASTEXITCODE)." }

# Kendi kendine barinan pakette ise yaramayan IIS artiklari.
foreach ($junk in @('appsettings.Development.json', 'web.config', 'aspnetcorev2_inprocess.dll')) {
    Remove-Item (Join-Path $output $junk) -ErrorAction SilentlyContinue
}

Copy-Item (Join-Path $packaging '*') $output -Recurse -Force
Write-Host 'Kurulum betikleri kopyalandi.' -ForegroundColor Green

$size = (Get-ChildItem -Recurse -File $output | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Paket hazir: {0} ({1:N1} MB)" -f $output, $size) -ForegroundColor Green

if ($Zip) {
    $zipPath = Join-Path $root 'dist\LocalDeploymentController.zip'
    Remove-Item $zipPath -ErrorAction SilentlyContinue

    # Klasorun kendisi arsivlenir: acildiginda dosyalar ortaliga sacilmaz,
    # duzgun bir LocalDeploymentController\ klasoru olusur.
    Compress-Archive -Path $output -DestinationPath $zipPath

    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Host ("Zip hazir: {0} ({1:N1} MB)" -f $zipPath, $zipSize) -ForegroundColor Green

    if ($Destination) {
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Copy-Item $zipPath $Destination -Force
        Write-Host "Kopyalandi: $(Join-Path $Destination 'LocalDeploymentController.zip')" -ForegroundColor Green
    }
}
