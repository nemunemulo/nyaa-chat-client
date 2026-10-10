@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [ERROR] Cannot find C# compiler: %CSC%
    pause
    exit /b 1
)

echo [BUILD] Compiling NyaaChat.exe...
"%CSC%" /target:winexe /out:"%~dp0NyaaChat.exe" /platform:anycpu /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll "%~dp0src\Program.cs"

if %ERRORLEVEL% EQU 0 (
    echo [OK] NyaaChat.exe built successfully.
) else (
    echo [FAIL] Compilation failed with error code %ERRORLEVEL%.
)

endlocal
