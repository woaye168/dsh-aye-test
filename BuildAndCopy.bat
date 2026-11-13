@echo off
setlocal
pushd "%~dp0"

set "TARGET_FRAMEWORK=net9.0"

call :EnsureDir "AppBundle\managed"
if errorlevel 1 goto :fail
call :EnsureDir "ui\AppBundle\managed"
if errorlevel 1 goto :fail

echo Building GameEntry Server-Debug...
dotnet build "src\GameEntry.csproj" -c Server-Debug /m:1
if errorlevel 1 goto :fail

copy /y "src\bin\Server-Debug\%TARGET_FRAMEWORK%\GameEntry.dll" "AppBundle\managed\GameEntry.dll"
if errorlevel 1 goto :fail

echo Building GameEntry Client-Debug...
dotnet build "src\GameEntry.csproj" -c Client-Debug /m:1
if errorlevel 1 goto :fail

copy /y "src\bin\Client-Debug\%TARGET_FRAMEWORK%\GameEntry.dll" "ui\AppBundle\managed\GameEntry.dll"
if errorlevel 1 goto :fail

echo GameEntry.dll copied to AppBundle and ui\AppBundle.
popd
endlocal
exit /b 0

:EnsureDir
if exist "%~1\" exit /b 0
mkdir "%~1"
if errorlevel 1 exit /b %errorlevel%
exit /b 0

:fail
set "ERR=%errorlevel%"
popd
endlocal & exit /b %ERR%
