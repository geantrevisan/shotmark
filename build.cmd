@echo off
cd /d "%~dp0"
taskkill /im ShotMark.exe /f >nul 2>&1
echo Compilando ShotMark...
dotnet build -c Release > build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build.log
type build.log
findstr /c:"EXITCODE=0" build.log >nul && start "" "src\ShotMark\bin\Release\net8.0-windows10.0.19041.0\ShotMark.exe"
