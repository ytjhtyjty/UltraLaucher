<#
.SYNOPSIS
    Воспроизводимая релизная сборка UltraLauncher.
.DESCRIPTION
    1. Запускает автотесты (без интеграционных — они качают сотни МБ).
    2. Публикует self-contained single-file сборку для win-x64 (на целевом ПК .NET не нужен).
    3. Кладёт результат в dist\UltraLauncher.exe и пишет SHA-256 в dist\SHA256SUMS.txt.
    В релиз НЕ попадают токены, настройки, логи и игровые файлы: они хранятся вне папки проекта
    (%LOCALAPPDATA%\UltraLauncher и папка игры).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$publish = Join-Path $root "artifacts\publish"
$project = Join-Path $root "src\UltraLauncher.App\UltraLauncher.App.csproj"

Write-Host "== UltraLauncher: релизная сборка ==" -ForegroundColor Green
Write-Host "Папка проекта: $root"
& dotnet --version
if ($LASTEXITCODE -ne 0) { throw ".NET SDK не найден. Установите .NET SDK 10 (https://dotnet.microsoft.com/download)." }

if (-not $SkipTests) {
    Write-Host "`n-- Автотесты --" -ForegroundColor Cyan
    $env:ULTRALAUNCHER_E2E = $null   # интеграционные тесты в релизной сборке не запускаются
    & dotnet test (Join-Path $root "tests\UltraLauncher.Tests\UltraLauncher.Tests.csproj") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Тесты не пройдены — релиз не собран." }
}

Write-Host "`n-- Публикация --" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:ContinuousIntegrationBuild=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой." }

Write-Host "`n-- Копирование в dist --" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -File | Remove-Item -Force
Copy-Item (Join-Path $publish "*") $dist -Recurse -Force

$files = Get-ChildItem $dist -File
$unexpected = $files | Where-Object { $_.Name -ne "UltraLauncher.exe" }
if ($unexpected) {
    Write-Warning ("Кроме UltraLauncher.exe в dist есть файлы: " + ($unexpected.Name -join ", ") + ". Распространяйте всю папку dist.")
}

$exe = Join-Path $dist "UltraLauncher.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  UltraLauncher.exe" | Set-Content (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nГотово: $exe ($sizeMb МБ)" -ForegroundColor Green
Write-Host "SHA-256: $hash"
