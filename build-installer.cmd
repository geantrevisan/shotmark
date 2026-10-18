@echo off
rem Gera os arquivos de release em .\dist:
rem   ShotMark-Setup-<versao>.exe   (instalador, precisa do Inno Setup 6)
rem   ShotMark-<versao>-win-x64-portable.zip
rem Uso: build-installer.cmd [versao]     ex.: build-installer.cmd 1.0.0
setlocal
cd /d "%~dp0"
set VERSION=%~1
if "%VERSION%"=="" set VERSION=1.0.0

echo === ShotMark %VERSION% ===> installer.log
taskkill /im ShotMark.exe /f >nul 2>&1
if exist publish rmdir /s /q publish
if not exist dist mkdir dist

echo Publicando (self-contained, arquivo unico)...
dotnet publish src\ShotMark\ShotMark.csproj -c Release -r win-x64 -p:Version=%VERSION% -o publish\win-x64 >> installer.log 2>&1
if errorlevel 1 goto fail

echo Criando ZIP portatil...
powershell -NoProfile -Command "Compress-Archive -Path 'publish\win-x64\*' -DestinationPath 'dist\ShotMark-%VERSION%-win-x64-portable.zip' -Force" >> installer.log 2>&1
if errorlevel 1 goto fail

set ISCC=
for %%P in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do if exist %%P set ISCC=%%P
if not defined ISCC (
  echo Inno Setup 6 nao encontrado - instalador .exe nao gerado. >> installer.log
  echo Instale com: winget install JRSoftware.InnoSetup >> installer.log
  echo NOSETUP >> installer.log
  goto done
)
echo Gerando instalador...
%ISCC% /DAppVersion=%VERSION% installer\ShotMark.iss >> installer.log 2>&1
if errorlevel 1 goto fail

:done
echo EXITCODE=0 >> installer.log
type installer.log
dir /b dist
exit /b 0

:fail
echo EXITCODE=1 >> installer.log
type installer.log
exit /b 1
