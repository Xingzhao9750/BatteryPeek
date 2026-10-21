@echo off
rem Builds BatteryPeek.exe with the C# compiler bundled with .NET Framework 4.x.
rem Nothing needs to be installed: the result runs on any Windows 10/11 machine.
setlocal

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [x] csc.exe not found - .NET Framework 4.x is required.
    exit /b 1
)

if not exist "%ROOT%dist" mkdir "%ROOT%dist"

"%CSC%" /nologo /target:winexe /optimize+ ^
    /out:"%ROOT%dist\BatteryPeek.exe" ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "%ROOT%src\*.cs"

if errorlevel 1 (
    echo [x] build failed
    exit /b 1
)

echo [ok] %ROOT%dist\BatteryPeek.exe
