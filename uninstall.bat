@echo off
setlocal
set "GAME=%~1"
if "%GAME%"=="" set "GAME=E:\SteamLibrary\steamapps\common\A Place for Every Potion"

if not exist "%GAME%\A Place for Every Potion.exe" (
  echo game not found at "%GAME%" >&2
  exit /b 1
)

for %%d in (MelonLoader Mods Plugins UserData UserLibs) do if exist "%GAME%\%%d" rmdir /s /q "%GAME%\%%d"
if exist "%GAME%\version.dll" del /q "%GAME%\version.dll"
if exist "%GAME%\steam_appid.txt" del /q "%GAME%\steam_appid.txt"

echo removed ApfepCoop + MelonLoader from "%GAME%"
echo Steam "verify integrity" restores the untouched original files as well
