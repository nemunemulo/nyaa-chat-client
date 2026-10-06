@echo off
chcp 65001 > nul
echo ============================================================================
echo  NyaaChat Client 빌드 스크립트 (.NET Framework 4.8)
echo ============================================================================
echo.

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo [오류] Windows 내장 C# 컴파일러를 찾을 수 없습니다: %CSC%
    pause
    exit /b 1
)

%CSC% /target:winexe /out:NyaaChat.exe /platform:anycpu /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll "%~dp0src\Program.cs"

if %ERRORLEVEL% EQU 0 (
    echo [완료] NyaaChat.exe 빌드가 성공적으로 완료되었습니다.
) else (
    echo [실패] 컴파일 중 오류가 발생했습니다.
)
echo.
pause
