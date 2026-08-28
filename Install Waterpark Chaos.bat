@echo off
setlocal

echo Waterpark Chaos - fetching the latest installer...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force } catch {}; irm https://raw.githubusercontent.com/musicman0917/WaterparkSimTwitchExpansion/main/get-installer.ps1 | iex"

echo.
echo If a window flashed and closed instead of the installer opening, something went wrong above -
echo scroll up to read the error, or download the installer by hand from:
echo     https://github.com/musicman0917/WaterparkSimTwitchExpansion/releases/latest
echo.
pause
