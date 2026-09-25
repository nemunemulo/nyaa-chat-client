@echo off
chcp 65001 > nul
echo ============================================================================
echo  Nyaa Chat Pure Native Client (Direction B) - Windows 11 자체 컴파일 스크립트
echo ============================================================================
echo.
echo [안내] 외부 DLL이나 웹뷰(WebView2)를 일절 사용하지 않고,
echo        Windows 11 기본 내장 .NET Framework 4.8 컴파일러(csc.exe)만으로
echo        src\Program.cs 소스코드를 순수 네이티브 NyaaChat.exe로 빌드합니다.
echo.

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo [오류] Windows 내장 C# 컴파일러를 찾을 수 없습니다: %CSC%
    pause
    exit /b 1
)

%CSC% /target:winexe /out:NyaaChat.exe /platform:anycpu /optimize+ ^
    /r:System.dll ^
    /r:System.Core.dll ^
    /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll ^
    /r:System.Net.Http.dll ^
    /r:System.Web.Extensions.dll ^
    src\Program.cs

if %ERRORLEVEL% EQU 0 (
    echo [성공] NyaaChat.exe 순수 네이티브 빌드가 완료되었습니다! (외부 DLL 의존성 0개)
) else (
    echo [실패] 컴파일 중 오류가 발생했습니다.
)
echo.
pause
