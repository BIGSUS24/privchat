@echo off
rem One-click start for PrivChat: installs the service on first run, then starts
rem the service and the capture-protected overlay.
rem Usage: double-click, or  start-privchat.bat [port]     (default 8787)
setlocal EnableExtensions
set "SYS=%SystemRoot%\System32"
set "ROOT=%~dp0.."
set "INSTALLER=%~dp0install-service.ps1"
set "ELEVATED="
set "PORT=%~1"
if /i "%~1"=="--elevated" (
    set "ELEVATED=1"
    set "PORT=%~2"
)
if "%PORT%"=="" set "PORT=8787"

rem System tools are called by full path; find/tasklist are shadowed by some PATHs.
"%SYS%\net.exe" session >nul 2>&1
if not errorlevel 1 goto elevated

echo Administrator rights are required to control the service.
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath '%~f0' -ArgumentList '--elevated','%PORT%' -Verb RunAs } catch { exit 1 }"
if errorlevel 1 echo Elevation was declined. Nothing was started.
exit /b

:elevated
"%SYS%\sc.exe" query PrivChatLocal >nul 2>&1
if not errorlevel 1 goto service_installed

echo PrivChatLocal is not installed yet. Building and installing it now...
if not exist "%INSTALLER%" goto no_installer
"%SYS%\where.exe" dotnet >nul 2>&1
if errorlevel 1 goto no_dotnet
rem install-service.ps1 publishes when needed, then starts and verifies the service.
powershell -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -Port %PORT% -OpenFirewall
goto verify_running

:service_installed
"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "RUNNING" >nul
if not errorlevel 1 goto service_up

rem stop-privchat.bat disables the service; re-arm it for manual start.
"%SYS%\sc.exe" config PrivChatLocal start= demand >nul
"%SYS%\net.exe" start PrivChatLocal

:verify_running
rem Check the real service state; exit codes from the steps above are not reliable.
"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "RUNNING" >nul
if errorlevel 1 goto start_failed

:service_up
echo Service PrivChatLocal is running.

rem The service launches the overlay itself; wait for it before starting a second one.
set "OVERLAY=%ROOT%\publish\PrivChat\service\PrivChat.Overlay.exe"
if not exist "%OVERLAY%" set "OVERLAY=%ROOT%\publish\PrivChat\overlay\PrivChat.Overlay.exe"
set /a tries=0

:poll_overlay
"%SYS%\tasklist.exe" /fi "imagename eq PrivChat.Overlay.exe" | "%SYS%\find.exe" /i "PrivChat.Overlay.exe" >nul
if not errorlevel 1 goto overlay_up
set /a tries+=1
if %tries% geq 6 goto launch_overlay
"%SYS%\ping.exe" -n 2 127.0.0.1 >nul
goto poll_overlay

:launch_overlay
if not exist "%OVERLAY%" goto no_overlay
start "" "%OVERLAY%" --url http://127.0.0.1:%PORT%
echo Overlay started.
goto done

:overlay_up
echo Overlay is running.
goto done

:no_overlay
echo Overlay not found at "%OVERLAY%". Run scripts\publish.ps1 to build it.
goto done

:done
echo.
echo PrivChat is up on port %PORT%.
echo The pair code and phone URL are shown on the overlay.
goto finish

:no_installer
echo Cannot find "%INSTALLER%". Keep the scripts folder intact.
goto fail

:no_dotnet
echo The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download
goto fail

:start_failed
echo PrivChatLocal is not running. See the messages above and
echo "%ProgramData%\PrivChat\service-error.log".
goto fail

:fail
echo.
echo PrivChat did not start.
if defined ELEVATED pause
exit /b 1

:finish
if defined ELEVATED pause
exit /b 0
