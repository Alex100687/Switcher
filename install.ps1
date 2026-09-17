# Собирает LayoutFix и устанавливает в %LocalAppData%\Programs\LayoutFix, включает автозапуск и запускает.
# Запуск:  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LayoutFix'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" } else { $dotnet = $dotnet.Source }
if (-not (Test-Path $dotnet)) { throw "Не найден .NET SDK: winget install Microsoft.DotNet.SDK.8" }

Write-Host "Сборка..." -ForegroundColor Cyan
& $dotnet publish "$PSScriptRoot\LayoutFix.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$PSScriptRoot\publish" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

Get-Process LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$PSScriptRoot\publish\*" $dest -Recurse -Force

$exe = Join-Path $dest 'LayoutFix.exe'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LayoutFix' -Value "`"$exe`""
Start-Process $exe

Write-Host "Установлено: $exe" -ForegroundColor Green
Write-Host "Автозапуск включён. Иконка — в трее. Настройки: $env:APPDATA\LayoutFix\settings.json"
