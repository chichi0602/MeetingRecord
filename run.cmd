@echo off
rem ---------------------------------------------------------------
rem  Start the MeetingRecord web app.
rem
rem  Usage (from anywhere - the script cd's to its own folder):
rem      run              -> http profile  (http://localhost:5189)
rem      run https        -> https profile (https://localhost:7044)
rem
rem  NOTE: comments here are ASCII on purpose. cmd.exe parses batch
rem  files using the active console codepage, so non-ASCII bytes in a
rem  .cmd can break parsing on machines with a different codepage.
rem  The Chinese explanation lives in docs/operations/, not here.
rem ---------------------------------------------------------------
setlocal

set "LAUNCH_PROFILE=%~1"
if "%LAUNCH_PROFILE%"=="" set "LAUNCH_PROFILE=http"

pushd "%~dp0"
dotnet run --project "src\MeetingRecord\MeetingRecord.Web\MeetingRecord.Web.csproj" --launch-profile %LAUNCH_PROFILE%
set "EXITCODE=%ERRORLEVEL%"
popd

rem Keep the window open when launched by double-clicking from Explorer,
rem otherwise a startup failure would flash past unreadably.
if not "%EXITCODE%"=="0" (
    echo.
    echo [run.cmd] dotnet run exited with code %EXITCODE%.
    echo [run.cmd] If the port is already in use, the app is probably
    echo [run.cmd] already running from Visual Studio.
    pause
)

exit /b %EXITCODE%
