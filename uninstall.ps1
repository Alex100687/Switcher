# Останавливает Switcher, убирает автозапуск и удаляет программу. Настройки в %AppData%\Switcher остаются (удали -Purge).

param([switch]$Purge)

Get-Process Switcher -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Switcher' -ErrorAction SilentlyContinue

Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Switcher') -Recurse -Force -ErrorAction SilentlyContinue

if ($Purge) { Remove-Item (Join-Path $env:APPDATA 'Switcher') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "Switcher удалён."

