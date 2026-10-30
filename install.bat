@echo off
setlocal
set "GAME=%~1"
if "%GAME%"=="" set "GAME=E:\SteamLibrary\steamapps\common\A Place for Every Potion"

if not exist "%GAME%\A Place for Every Potion.exe" (
  echo game not found at "%GAME%" >&2
  exit /b 1
)

set "DLL=%~dp0mod\ApfepCoop\bin\Debug\net6.0\ApfepCoop.dll"
if not exist "%DLL%" (
  echo built mod missing - run: dotnet build in mod\ApfepCoop >&2
  exit /b 1
)

set "LOADER=%~dp0install\MelonLoader"
set "INJECTOR=%~dp0install\version.dll"
if not exist "%LOADER%\net6" (
  echo MelonLoader copy missing at "%LOADER%" >&2
  exit /b 1
)
if not exist "%INJECTOR%" (
  echo MelonLoader injector version.dll missing - copy it from a MelonLoader release >&2
  exit /b 1
)

robocopy "%LOADER%" "%GAME%\MelonLoader" /E /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (echo MelonLoader copy failed >&2 & exit /b 1)
copy /Y "%INJECTOR%" "%GAME%\version.dll" >nul
if errorlevel 1 (echo injector copy failed >&2 & exit /b 1)

if not exist "%GAME%\Plugins" mkdir "%GAME%\Plugins"
copy /Y "%DLL%" "%GAME%\Plugins\ApfepCoop.dll" >nul
if errorlevel 1 (echo mod copy failed >&2 & exit /b 1)

echo 5189500>"%GAME%\steam_appid.txt"

echo installed into "%GAME%"
echo host: launch the game; room code appears in MelonLoader\Latest.log
echo client: set APFEP_JOIN=^<room code^> before launching the game
