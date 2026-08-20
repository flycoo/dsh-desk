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

tasklist /FI "IMAGENAME eq DshDesk.exe" 2>nul | find /I "DshDesk.exe" >nul
if %ERRORLEVEL% EQU 0 (
  echo [ERROR] DSH Desk is still running and locks the exe/dll.
  echo         Please exit it first: tray icon -^> "Exit DSH Desk".
  goto :end
)

echo Copying fresh build to %DST% ...
robocopy "%SRC%" "%DST%" /E /R:1 /W:1
if %ERRORLEVEL% GEQ 8 (
  echo [ERROR] Copy failed with code %ERRORLEVEL%.
) else (
  echo [OK] Deployment finished.
  echo      Verify DshDesk.dll in %DST% is 651,776 bytes.
  echo      Then start DshDesk.exe - expected: http://127.0.0.1:3080
)

:end
echo.
pause
endlocal
