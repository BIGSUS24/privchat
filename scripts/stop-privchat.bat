@echo off
rem One-click stop for PrivChat. Stops and disables the service so it cannot come
rem back on boot, then kills every PrivChat process (overlay and any console relay).
rem PrivChat only runs again when start-privchat.bat is used.
setlocal EnableExtensions
set "SYS=%SystemRoot%\System32"
set "ELEVATED="
if /i "%~1"=="--elevated" set "ELEVATED=1"

rem System tools are called by full path; find/tasklist are shadowed by some PATHs.
"%SYS%\net.exe" session >nul 2>&1
if not errorlevel 1 goto elevated

echo Administrator rights are required to control the service.
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs } catch { exit 1 }"
if errorlevel 1 echo Elevation was declined. Nothing was stopped.
exit /b

:elevated
"%SYS%\sc.exe" query PrivChatLocal >nul 2>&1
if errorlevel 1 goto no_service

"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "STOPPED" >nul
if not errorlevel 1 goto disable_service

rem Stop the service before the overlay, otherwise it relaunches it within 5 seconds.
"%SYS%\net.exe" stop PrivChatLocal

rem net stop can report failure (2189) while the stop still completes, so trust the
rem service state rather than the exit code.
"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "STOPPED" >nul
if not errorlevel 1 goto stopped

echo Waiting for the service to stop...
"%SYS%\ping.exe" -n 6 127.0.0.1 >nul
"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "STOPPED" >nul
if not errorlevel 1 goto stopped

echo Service is not responding to the stop request; forcing it down.
call :kill_image PrivChat.Service.exe Relay
"%SYS%\ping.exe" -n 4 127.0.0.1 >nul
"%SYS%\sc.exe" query PrivChatLocal | "%SYS%\find.exe" "STOPPED" >nul
if errorlevel 1 goto stop_failed

:stopped
echo Service PrivChatLocal stopped.

:disable_service
"%SYS%\sc.exe" config PrivChatLocal start= disabled >nul
if errorlevel 1 (
    echo Warning: could not disable PrivChatLocal; it may start again on boot.
) else (
    echo Service disabled. It will not start on boot.
)
goto kill_all

:no_service
echo PrivChatLocal is not installed; closing any PrivChat process left behind.

:kill_all
call :kill_image PrivChat.Overlay.exe Overlay
call :kill_image PrivChat.Service.exe Relay
goto finish

:kill_image
"%SYS%\tasklist.exe" /fi "imagename eq %~1" | "%SYS%\find.exe" /i "%~1" >nul
if errorlevel 1 (
    echo No %~2 process was running.
    exit /b 0
)
"%SYS%\taskkill.exe" /f /im %~1 >nul
if errorlevel 1 (
    echo Warning: could not kill %~1.
    exit /b 0
)
echo %~2 stopped.
exit /b 0

:stop_failed
echo Could not stop PrivChatLocal. Current state:
"%SYS%\sc.exe" query PrivChatLocal
echo Reboot clears a stuck service if this keeps happening.
if defined ELEVATED pause
exit /b 1

:finish
echo.
echo PrivChat is fully stopped. Run start-privchat.bat to bring it back.
if defined ELEVATED pause
exit /b 0
