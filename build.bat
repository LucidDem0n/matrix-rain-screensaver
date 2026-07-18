@echo off
setlocal
echo ============================================
echo   Building MatrixRain
echo ============================================
echo.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
  echo ERROR: Could not find the built-in C# compiler.
  echo Looked in %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\
  echo.
  pause
  exit /b 1
)

echo Using compiler: %CSC%
echo.

set ICON=
if exist "%~dp0matrix.ico" set ICON=/win32icon:"%~dp0matrix.ico"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu %ICON% ^
  /out:"%~dp0MatrixRain.scr" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  "%~dp0MatrixRain.cs"

if errorlevel 1 (
  echo.
  echo ******** BUILD FAILED - copy the errors above and send them back ********
  echo.
  pause
  exit /b 1
)

rem A shortcut cannot pass /c reliably to a .scr, so keep an .exe twin for the GUI.
copy /Y "%~dp0MatrixRain.scr" "%~dp0MatrixRainSettings.exe" >nul

echo Creating desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$d=[Environment]::GetFolderPath('Desktop');" ^
  "$w=New-Object -ComObject WScript.Shell;" ^
  "$s=$w.CreateShortcut((Join-Path $d 'Matrix Rain Settings.lnk'));" ^
  "$s.TargetPath='%~dp0MatrixRainSettings.exe';" ^
  "$s.Arguments='/c';" ^
  "$s.WorkingDirectory='%~dp0';" ^
  "$s.Description='Matrix Rain screensaver settings';" ^
  "$s.Save()"

echo.
echo ============================================
echo   BUILD OK
echo   %~dp0MatrixRain.scr
echo   %~dp0MatrixRainSettings.exe
echo   Desktop shortcut: "Matrix Rain Settings"
echo ============================================
echo.
echo Next: copy MatrixRain.scr into C:\Windows\System32 from an ADMIN prompt:
echo   copy /Y "%~dp0MatrixRain.scr" C:\Windows\System32\
echo.
pause
