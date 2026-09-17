# Останавливает LayoutFix, убирает автозапуск и удаляет программу. Настройки в %AppData%\LayoutFix остаются (удали -Purge).
param([switch]$Purge)
Get-Process LayoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LayoutFix' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\LayoutFix') -Recurse -Force -ErrorAction SilentlyContinue
if ($Purge) { Remove-Item (Join-Path $env:APPDATA 'LayoutFix') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "LayoutFix удалён."
