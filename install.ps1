# Собирает Switcher и устанавливает в %LocalAppData%\Programs\Switcher, включает автозапуск и запускает.
# Запуск:  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Switcher'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" } else { $dotnet = $dotnet.Source }
if (-not (Test-Path $dotnet)) { throw "Не найден .NET SDK: winget install Microsoft.DotNet.SDK.8" }

Write-Host "Сборка..." -ForegroundColor Cyan
# инкрементальный publish не докладывает файлы в частично пустую папку — чистим
Remove-Item "$PSScriptRoot\publish" -Recurse -Force -ErrorAction SilentlyContinue
$publishArgs = @('publish', "$PSScriptRoot\Switcher.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                 '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', "$PSScriptRoot\publish")
& $dotnet @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

Get-Process Switcher, LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# программа раньше называлась LayoutFix — убираем ту установку
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LayoutFix' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\LayoutFix') -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $dest | Out-Null
# robocopy: Copy-Item "src\*" -Recurse в PS 5.1 теряет вложенные файлы
& robocopy "$PSScriptRoot\publish" $dest /MIR /NJH /NJS /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Копирование не удалось (robocopy $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $dest 'dict/ru_RU.dic'))) { throw "Словари не скопировались" }

$exe = Join-Path $dest 'Switcher.exe'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Switcher' -Value "`"$exe`""
Start-Process $exe

Write-Host "Установлено: $exe" -ForegroundColor Green
Write-Host "Автозапуск включён. Иконка — в трее. Настройки: $env:APPDATA\Switcher\settings.json"
