@echo off
setlocal
cd /d "%~dp0"

rem Uses the C# compiler that ships with Windows (.NET Framework 4.x) - nothing to install.
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Could not find csc.exe from .NET Framework 4.
  pause
  exit /b 1
)

echo Closing any running ShiftPreview so the new version can replace it ...
taskkill /im ShiftPreview.exe /f >nul 2>&1
timeout /t 1 >nul

echo Building ShiftPreview.exe from:
echo   %~dp0ShiftPreview.cs
findstr /c:"public const string Version" ShiftPreview.cs
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:ShiftPreview.exe ^
  /lib:"%FW%" /lib:"%FW%\WPF" ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:UIAutomationClient.dll /r:UIAutomationTypes.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll ^
  ShiftPreview.cs

if errorlevel 1 (
  echo.
  echo Build FAILED - see the errors above.
  pause
  exit /b 1
)

echo.
echo Done! Starting ShiftPreview.exe - look for the blue play icon in the system tray.
start "" "%~dp0ShiftPreview.exe"
timeout /t 3 >nul
