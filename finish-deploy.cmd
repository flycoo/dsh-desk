@echo off
rem ============================================================
rem  DSH Desk deploy script (ASCII-only, no encoding issues)
rem  Copies the fresh build over D:\app\DSHDesk.
rem  Run ONLY after DSH Desk has fully exited.
rem ============================================================
setlocal
set "SRC=E:\dsh-desk\artifacts\DSHDesk-win-x64"
set "DST=D:\app\DSHDesk"

if not exist "%SRC%\DshDesk.exe" (
  echo [ERROR] Source build not found: %SRC%
  goto :end
)

if not exist "%SRC%\DshDesk.dll" (
  echo [ERROR] Source DLL not found: %SRC%\DshDesk.dll
  goto :end
)

tasklist /FI "IMAGENAME eq DshDesk.exe" 2>nul | find /I "DshDesk.exe" >nul
if %ERRORLEVEL% EQU 0 (
  echo [ERROR] DSH Desk is still running and locks the exe/dll.
  echo         Please exit it first: tray icon -^> "Exit DSH Desk".
  goto :end
)

echo Copying fresh build to %DST% ...
robocopy "%SRC%" "%DST%" /E /R:1 /W:1
set "ROBOEXIT=%ERRORLEVEL%"
if %ROBOEXIT% GEQ 8 (
  echo [ERROR] Copy failed with code %ROBOEXIT%.
  goto :end
)

if not exist "%DST%\DshDesk.dll" (
  echo [ERROR] Deployed DLL not found: %DST%\DshDesk.dll
  goto :end
)

for /f "skip=1 delims=" %%H in ('certutil -hashfile "%SRC%\DshDesk.dll" SHA256') do if not defined SRCDLLHASH set "SRCDLLHASH=%%H"
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%DST%\DshDesk.dll" SHA256') do if not defined DSTDLLHASH set "DSTDLLHASH=%%H"
for %%F in ("%DST%\DshDesk.dll") do set "DSTDLLSIZE=%%~zF"

if not defined SRCDLLHASH (
  echo [ERROR] Could not hash source DLL: %SRC%\DshDesk.dll
  goto :end
)
if not defined DSTDLLHASH (
  echo [ERROR] Could not hash deployed DLL: %DST%\DshDesk.dll
  goto :end
)
if /i not "%SRCDLLHASH%"=="%DSTDLLHASH%" (
  echo [ERROR] DshDesk.dll hash mismatch: source %SRCDLLHASH%, destination %DSTDLLHASH%.
  goto :end
)

echo [OK] Deployment finished. DshDesk.dll: %DSTDLLSIZE% bytes, SHA-256 %DSTDLLHASH%.
echo      Then start DshDesk.exe - expected: http://127.0.0.1:3080

:end
echo.
pause
endlocal
